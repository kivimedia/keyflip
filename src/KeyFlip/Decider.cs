using System.Drawing;

namespace KeyFlip;

/// <summary>
/// The decision ladder for a newly focused field:
/// learned answer -> text already in the field -> Jev -> history for the app/site -> Haiku -> keep.
/// A switch is applied only if nothing was typed and no manual switch happened since the focus.
/// </summary>
public sealed class Decider
{
    private readonly Store _store;
    private readonly FocusWatcher _watcher;
    private readonly JevClient _jev = new();
    private readonly HaikuClient _haiku;

    private volatile FieldContext? _current;
    private int _currentSeq;
    private volatile bool _typedSinceFocus;
    private volatile bool _manualSinceFocus;

    public event Action? StateChanged;

    public Decider(Store store, FocusWatcher watcher)
    {
        _store = store;
        _watcher = watcher;
        _haiku = new HaikuClient(store.Config.AnthropicApiKey);
        watcher.FocusChanged += OnFocus;
        watcher.ManualSwitch += OnManualSwitch;
    }

    public FieldContext? Current => _current;
    public bool JevAvailable => _jev.Available;
    public bool HaikuAvailable => _haiku.Available;

    private int _clickSeq;
    private volatile bool _typedSinceClick;
    private volatile bool _manualSinceClick;

    /// <summary>Called by the keyboard hook on every real keystroke.</summary>
    public void NoteKey()
    {
        _typedSinceFocus = true;
        _typedSinceClick = true;
    }

    /// <summary>
    /// A click inside a document (Google Docs, Word): the paragraph clicked into decides, not the doc as a whole
    /// (26-Oct: an English-named doc with Hebrew paragraphs stayed English). The text is read off the screen,
    /// because Docs draws it on a canvas. Waits a moment so the focus watcher sees the doc first.
    /// </summary>
    public void OnClick(Point click)
    {
        if (_store.Config.Paused || !_haiku.Available || !Layouts.Ready) return;
        int seq = Interlocked.Increment(ref _clickSeq);
        _typedSinceClick = false;
        _manualSinceClick = false;
        var window = Native.GetForegroundWindow();
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            var ctx = _current;
            if (ctx == null || !ctx.IsDocument || ctx.IsPassword || Volatile.Read(ref _clickSeq) != seq) return;
            if (Native.GetForegroundWindow() != window) return;
            var started = DateTime.Now;
            var png = LineReader.CaptureStrip(window, click);
            if (png == null) return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var a = await _haiku.AskLineAsync(png, cts.Token);
            _store.Count(d => { d.Haiku++; d.Usd += a.Usd; });

            string result;
            if (a.Lang == null) result = "unsure";
            else if (Volatile.Read(ref _clickSeq) != seq) result = "skipped: clicked again";
            else if (_typedSinceClick) result = "skipped: already typing";
            else if (_manualSinceClick) result = "skipped: manual switch";
            else if (Layouts.ForegroundLang() == a.Lang) result = "already";
            else
            {
                _watcher.ExpectSwitch(a.Lang.Value);
                Layouts.Switch(a.Lang.Value);
                result = "switched";
            }
            Log.Write(new { kind = "line", answer = a.Raw, lang = a.Lang, result, app = ctx.App, site = ctx.Site, page = ctx.Page, ms = (long)(DateTime.Now - started).TotalMilliseconds, usd = a.Usd, error = a.Error });
            StateChanged?.Invoke();
        });
    }

    /// <summary>Called by the fixer when a finished word was a real word in the current layout.</summary>
    public void NoteTypedWord(Lang lang)
    {
        var ctx = _current;
        if (ctx == null) return;
        _store.SetHistory(ctx.AppSiteKey, lang);
        _store.SetField(ctx.FieldKey, lang, "typed");
    }

    private void OnManualSwitch(Lang lang)
    {
        _manualSinceFocus = true;
        _manualSinceClick = true;
        var ctx = _current;
        if (ctx != null && ctx.IsText)
        {
            _store.SetField(ctx.FieldKey, lang, "manual");
            _store.SetHistory(ctx.AppSiteKey, lang);
        }
        Log.Write(new { kind = "manual", lang, app = ctx?.App, site = ctx?.Site, field = ctx?.Label });
        StateChanged?.Invoke();
    }

    private void OnFocus(FieldContext ctx, int seq)
    {
        _current = ctx;
        Volatile.Write(ref _currentSeq, seq);
        _typedSinceFocus = false;
        _manualSinceFocus = false;
        StateChanged?.Invoke();

        if (_store.Config.Paused || !ctx.IsText || ctx.IsPassword || !Layouts.Ready) return;
        if (_store.Config.ExcludedApps.Contains(ctx.App, StringComparer.OrdinalIgnoreCase)) return;
        _ = Task.Run(() => DecideAsync(ctx, seq));
    }

    private async Task DecideAsync(FieldContext ctx, int seq)
    {
        var started = DateTime.Now;
        _store.Count(d => d.Focus++);

        // 1. Learned for this exact field (manual switches, typing, or an earlier model answer).
        var learned = _store.GetField(ctx.FieldKey);
        if (learned != null) { Apply(ctx, seq, learned.Lang, "learned:" + learned.Source, started); return; }

        // 2. Text already in the field, clearly in one script.
        var local = ScriptOf(ctx.Text);
        if (local != null) { Remember(ctx, local.Value, "local"); Apply(ctx, seq, local.Value, "local", started); return; }

        // 2b. The user-written part of the page title (a document name, an email subject).
        var title = ctx.TitleHint();
        if (title != null) { Remember(ctx, title.Value, "title"); Apply(ctx, seq, title.Value, "title", started); return; }

        // 3. Jev.
        double usd = 0;
        if (_jev.Available)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var a = await _jev.AskAsync(ctx.ToState(), cts.Token);
            usd += a.Usd;
            _store.Count(d => { d.Jev++; d.Usd += a.Usd; });
            Log.Write(new { kind = "jev", app = ctx.App, site = ctx.Site, field = ctx.Label, answer = a.Raw, prob = Math.Round(a.Confidence, 3), opposite = Math.Round(a.OppositeProb, 3), ms = a.LatencyMs, usd = a.Usd, error = a.Error });
            if (a.Accept(_store.Config.JevThreshold))
            {
                Remember(ctx, a.Lang.Value, "jev");
                Apply(ctx, seq, a.Lang.Value, "jev", started, usd);
                return;
            }
        }

        // 4. What the user last typed in on this app / site.
        var hist = _store.GetHistory(ctx.AppSiteKey);
        if (hist != null) { Apply(ctx, seq, hist.Value, "history", started, usd); return; }

        // 5. Haiku, only when there is real text to read.
        if (_haiku.Available && ctx.HasRealText)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var a = await _haiku.AskAsync(ctx.ToState(), cts.Token);
            usd += a.Usd;
            _store.Count(d => { d.Haiku++; d.Usd += a.Usd; });
            Log.Write(new { kind = "haiku", app = ctx.App, site = ctx.Site, field = ctx.Label, answer = a.Raw, ms = a.LatencyMs, usd = a.Usd, error = a.Error });
            if (a.Lang != null)
            {
                Remember(ctx, a.Lang.Value, "haiku");
                Apply(ctx, seq, a.Lang.Value, "haiku", started, usd);
                return;
            }
        }

        // 6. Keep the current layout.
        Log.Write(new { kind = "focus", step = "keep", app = ctx.App, site = ctx.Site, field = ctx.Label, ms = (long)(DateTime.Now - started).TotalMilliseconds, usd });
    }

    private void Remember(FieldContext ctx, Lang lang, string source) => _store.SetField(ctx.FieldKey, lang, source);

    private void Apply(FieldContext ctx, int seq, Lang lang, string step, DateTime started, double usd = 0)
    {
        string result;
        if (Volatile.Read(ref _currentSeq) != seq) result = "skipped: focus moved";
        else if (_typedSinceFocus) result = "skipped: already typing";
        else if (_manualSinceFocus) result = "skipped: manual switch";
        else if (Layouts.ForegroundLang() == lang) result = "already";
        else
        {
            _watcher.ExpectSwitch(lang);
            Layouts.Switch(lang);
            result = "switched";
        }
        Log.Write(new { kind = "focus", step, lang, result, app = ctx.App, site = ctx.Site, field = ctx.Label, ms = (long)(DateTime.Now - started).TotalMilliseconds, usd });
        StateChanged?.Invoke();
    }

    /// <summary>Hebrew or English when at least 4 letters are there and 80% are one script.</summary>
    public static Lang? ScriptOf(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var tail = text.Length > 200 ? text[^200..] : text;
        int he = tail.Count(WordChecker.IsHebrewLetter), en = tail.Count(WordChecker.IsLatinLetter);
        int total = he + en;
        if (total < 4) return null;
        if (he >= total * 0.8) return Lang.He;
        if (en >= total * 0.8) return Lang.En;
        return null;
    }
}
