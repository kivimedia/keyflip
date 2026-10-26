using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace KeyFlip;

public sealed class FieldContext
{
    public string App { get; init; } = "";
    public string Site { get; init; } = "";
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public string Help { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsPassword { get; init; }
    public bool IsText { get; init; }

    public string AppSiteKey => $"{App}|{Site}";

    /// <summary>In a browser the same generic field ("Document content", "Message Body") appears on every
    /// page of a site, so the page title is part of its identity. Unread counters like "(3)" are dropped.</summary>
    public string Page => Site.Length == 0 ? "" :
        Regex.Replace(Regex.Replace(Title, @" - (Google Chrome|Microsoft​? Edge|Brave|Vivaldi|Opera)$", ""), @"\(\d+\)\s*", "").Trim();

    public string FieldKey => $"{App}|{Site}|{Cut(Page, 80)}|{Kind}|{Cut(AutomationId, 60)}|{Cut(Label, 60)}";

    /// <summary>What the models see: names and labels, plus at most 300 chars already in the field.</summary>
    public object ToState() => new
    {
        app = App,
        site = Site,
        // Tested 26-Oct: a separate page title plus this note took a Google Doc named in English
        // from 0.65 to 0.93 and a Hebrew-named one to 0.99; "Untitled document" stays unclear.
        page_title = Cut(Site.Length > 0 ? Page : Title, 160),
        page_title_note = "On document, email and chat sites the page title is usually written by the user " +
                          "(a document name, an email subject, a contact name), so its language is a strong hint.",
        field_kind = Kind,
        field_label = Cut(Label, 120),
        field_hint = Cut(Help, 120),
        text_already_in_field = Cut(Text, 300),
    };

    private static readonly HashSet<string> DocumentSites = new(StringComparer.OrdinalIgnoreCase)
        { "docs.google.com", "notion.so", "onedrive.live.com", "word.cloud.microsoft", "paper.dropbox.com", "coda.io" };

    /// <summary>
    /// Language of the user-written part of a browser page title (the last " - App" segment dropped).
    /// Hebrew counts on any site: the browser UI is English, so Hebrew in a title was written by a person.
    /// English counts only on document sites, where the title is the document name; on WhatsApp or Gmail an
    /// English title is just the app. Added 26-Oct after Jev wavered on an English-named Google Doc.
    /// </summary>
    public Lang? TitleHint()
    {
        if (Site.Length == 0) return null;
        var parts = Page.Split(" - ");
        var own = parts.Length > 1 ? string.Join(" - ", parts[..^1]) : Page;
        own = Regex.Replace(own, @"\S+@\S+|https?://\S+", "");
        if (own.TrimStart().StartsWith("Untitled", StringComparison.OrdinalIgnoreCase)) return null;
        int he = own.Count(WordChecker.IsHebrewLetter), en = own.Count(WordChecker.IsLatinLetter);
        int total = he + en;
        if (total < 5) return null;
        if (he >= total * 0.6) return Lang.He;
        if (en >= total * 0.9 && DocumentSites.Contains(Site)) return Lang.En;
        return null;
    }

    private static readonly HashSet<string> DocumentApps = new(StringComparer.OrdinalIgnoreCase) { "WINWORD", "soffice.bin" };

    /// <summary>Long documents mix languages paragraph by paragraph, so a click inside one is re-read.</summary>
    public bool IsDocument => IsText && (DocumentSites.Contains(Site) || DocumentApps.Contains(App));

    public bool HasRealText => (Label + Help + Text + Site).Count(char.IsLetter) >= 3;

    internal static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
}

/// <summary>
/// Polls the foreground window and the UI Automation focused element on its own thread
/// (UIA calls can block, and the keyboard hook thread must never block). Raises FocusChanged
/// for every new text field, and ManualSwitch when the layout changes without us.
/// </summary>
public sealed class FocusWatcher
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "brave", "vivaldi", "opera" };

    public event Action<FieldContext, int>? FocusChanged;
    public event Action<Lang>? ManualSwitch;

    private readonly Dictionary<uint, string> _procNames = new();
    private readonly Dictionary<IntPtr, AutomationElement?> _addressBars = new();
    private Thread? _thread;
    private volatile bool _stop;

    private IntPtr _lastFg;
    private string _lastFocusId = "";
    private string _lastFieldId = "";
    private IntPtr _lastHkl;
    private int _seq;

    private IntPtr _expectedHkl;
    private DateTime _expectedUntil;

    public int Seq => Volatile.Read(ref _seq);

    /// <summary>Tell the watcher a layout change is ours, so it is not read as the user's manual switch.</summary>
    public void ExpectSwitch(Lang lang)
    {
        _expectedHkl = Layouts.Hkl(lang);
        _expectedUntil = DateTime.Now.AddMilliseconds(1500);
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "KeyFlip focus" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop() => _stop = true;

    private void Loop()
    {
        while (!_stop)
        {
            try { Tick(); } catch { }
            Thread.Sleep(200);
        }
    }

    private void Tick()
    {
        var hkl = Layouts.ForegroundHkl(out var fg);
        if (fg == IntPtr.Zero) return;
        bool fgChanged = fg != _lastFg;

        // A layout change inside the same window that we did not ask for = the user switched by hand.
        if (!fgChanged && hkl != _lastHkl && _lastHkl != IntPtr.Zero)
        {
            bool ours = hkl == _expectedHkl && DateTime.Now < _expectedUntil;
            var lang = Layouts.LangOf(hkl);
            if (!ours && lang != Lang.Other) ManualSwitch?.Invoke(lang);
        }
        _lastHkl = hkl;
        _lastFg = fg;

        AutomationElement? el;
        try { el = AutomationElement.FocusedElement; } catch { return; }
        if (el == null) return;
        string id;
        try { id = fg + ":" + string.Join(".", el.GetRuntimeId()); } catch { return; }
        if (!fgChanged && id == _lastFocusId) return;
        _lastFocusId = id;

        var ctx = Read(el, fg);
        if (ctx == null) return;
        // Chrome re-announces the same field while you type (new runtime id, same field). That is not a
        // new focus: it must not reset the typing guards, the word being typed, or re-apply a layout.
        var fieldId = fg + "|" + ctx.FieldKey;
        if (!fgChanged && fieldId == _lastFieldId) return;
        _lastFieldId = fieldId;
        int seq = Interlocked.Increment(ref _seq);
        FocusChanged?.Invoke(ctx, seq);
    }

    private FieldContext? Read(AutomationElement el, IntPtr fg)
    {
        Native.GetWindowThreadProcessId(fg, out var pid);
        var app = ProcName(pid);
        if (app.Equals("KeyFlip", StringComparison.OrdinalIgnoreCase)) return null;

        var c = el.Current;
        var type = c.ControlType;
        bool isText = type == ControlType.Edit || type == ControlType.Document || type == ControlType.ComboBox;
        string text = "";
        if (type != ControlType.Document && el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
        {
            var v = (ValuePattern)vp;
            try
            {
                if (!v.Current.IsReadOnly) isText = true;
                text = v.Current.Value ?? "";
            }
            catch { }
        }

        var title = new StringBuilder(256);
        Native.GetWindowText(fg, title, title.Capacity);

        return new FieldContext
        {
            App = app,
            Site = Browsers.Contains(app) ? SiteOf(fg) : "",
            Title = title.ToString(),
            Kind = type.ProgrammaticName.Replace("ControlType.", ""),
            Label = c.Name ?? "",
            Help = c.HelpText ?? "",
            AutomationId = c.AutomationId ?? "",
            Text = c.IsPassword ? "" : text,
            IsPassword = c.IsPassword,
            IsText = isText,
        };
    }

    private string ProcName(uint pid)
    {
        if (_procNames.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById((int)pid).ProcessName; } catch { n = "?"; }
        return _procNames[pid] = n;
    }

    /// <summary>Host name from the browser address bar, found once per window and cached.</summary>
    private string SiteOf(IntPtr fg)
    {
        try
        {
            if (!_addressBars.TryGetValue(fg, out var bar))
            {
                var root = AutomationElement.FromHandle(fg);
                bar = root.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, "Address and search bar")));
                _addressBars[fg] = bar;
            }
            if (bar == null) return "";
            var url = ((ValuePattern)bar.GetCurrentPattern(ValuePattern.Pattern)).Current.Value ?? "";
            if (!url.Contains("://")) url = "https://" + url;
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";
        }
        catch
        {
            _addressBars.Remove(fg);
            return "";
        }
    }
}
