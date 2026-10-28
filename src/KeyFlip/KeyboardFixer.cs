using System.Runtime.InteropServices;
using static KeyFlip.Native;

namespace KeyFlip;

/// <summary>
/// Low-level keyboard hook. Keeps the keys of the word being typed; when the word ends and it is
/// gibberish in the current layout but a real word in the other one, it swallows the ending key,
/// erases the word, switches layout and retypes it. Backspace right after a fix undoes it.
/// The hook runs on the UI thread and must return fast: the fix itself runs via BeginInvoke.
/// </summary>
public sealed class KeyboardFixer : IDisposable
{
    private readonly Store _store;
    private readonly Decider _decider;
    private readonly FocusWatcher _watcher;
    private readonly Control _ui;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    private readonly WordTracker _word;
    private readonly TypingActivity _activity;
    private int _wordSeq;

    private sealed record LastFix(string Typed, string Fixed, uint EndVk, string EndTextBefore, string EndTextAfter, Lang Before, DateTime At);
    private LastFix? _lastFix;

    public KeyboardFixer(Store store, Decider decider, FocusWatcher watcher, WordChecker words, Control ui)
    {
        _store = store;
        _decider = decider;
        _watcher = watcher;
        _ui = ui;
        _activity = decider.Activity;
        _word = new WordTracker(words, _activity);
        _proc = HookProc;
    }

    /// <summary>A left click (mouse hook, UI thread, same as the keyboard hook): the caret may have moved
    /// into the middle of text, so the keys that follow are not known to start a word.</summary>
    public void OnClick(System.Drawing.Point _)
    {
        _word.Click(DateTime.Now);
        _lastFix = null;
    }

    public void Start() => _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                bool swallow = false;
                try { swallow = OnKeyDown(k); } catch { Reset(); }
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Reset()
    {
        _word.CaretMoved();
        _lastFix = null;
    }

    private bool OnKeyDown(KBDLLHOOKSTRUCT k)
    {
        uint vk = k.vkCode;
        bool isModifier = vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x14;
        if (isModifier) return false;
        _decider.NoteKey();
        bool shortcut = Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);
        var window = GetForegroundWindow();
        var now = DateTime.Now;
        // Tab, Enter, Escape and shortcuts (Ctrl+L, Ctrl+K...) move the focus on purpose; other keys are typing.
        _activity.Key(window, now, movesFocus: shortcut || vk is 0x09 or 0x0D or 0x1B);

        var ctx = _decider.Current;
        if (_store.Config.Paused || !_store.Config.FixerEnabled || !Layouts.Ready || ctx == null || ctx.IsPassword
            || _store.Config.FixerExcludedApps.Contains(ctx.App, StringComparer.OrdinalIgnoreCase))
        {
            Reset();
            return false;
        }

        // Shortcuts (Ctrl / Alt / Win + key) are never typing.
        if (shortcut) { Reset(); return false; }

        // A new field means a new word.
        if (_watcher.Seq != _wordSeq) { _word.NewField(); _wordSeq = _watcher.Seq; }

        if (vk == 0x08) // Backspace
        {
            var fix = _lastFix;
            if (_word.Count == 0 && fix != null && (DateTime.Now - fix.At).TotalSeconds < 10)
            {
                _lastFix = null;
                _ui.BeginInvoke(() => Undo(fix));
                return true;
            }
            _lastFix = null;
            _word.Backspace();
            return false;
        }

        var current = Layouts.ForegroundLang();
        if (current == Lang.Other) { Reset(); return false; }
        var other = Layouts.OtherOf(current);
        bool shift = Down(0x10);
        bool caps = (GetKeyState(0x14) & 1) != 0;
        var curText = Layouts.ToText(vk, k.scanCode, shift, caps, Layouts.Hkl(current));
        var othText = Layouts.ToText(vk, k.scanCode, shift, caps, Layouts.Hkl(other));

        if (curText.Length != 1) { Reset(); return false; } // arrows, F-keys, Delete, Escape...
        _lastFix = null;

        char oc = othText.Length == 1 ? othText[0] : '\0';
        if (WordTracker.IsWordKey(curText[0], oc, current))
        {
            _word.Letter(curText, othText, current);
            return false;
        }

        // Any other character ends the word.
        var ended = _word.End(curText, current, window, now, _store.NeverFlip);
        if (ended is not { } e) return false;
        if (e.Blocked != null)
        {
            // What the old rule would have flipped, and why it did not: the live log proves the guards.
            string app = ctx.App, site = ctx.Site;
            _ui.BeginInvoke(() => Log.Write(new { kind = "nofix", from = e.Typed, to = e.Meant, reason = e.Blocked, app, site }));
        }
        if (e.Fixed == null)
        {
            if (e.RealWord) _decider.NoteTypedWord(current);
            return false;
        }

        // Swallow the ending key; the fix retypes it after the corrected word.
        var endAfter = vk is 0x20 or 0x0D or 0x09 ? "" : Layouts.ToText(vk, k.scanCode, shift, caps, Layouts.Hkl(other));
        var fix2 = new LastFix(e.Typed, e.Fixed, vk, curText, endAfter, current, DateTime.Now);
        _ui.BeginInvoke(() => Apply(fix2, e.Keys));
        return true;
    }

    private void Apply(LastFix fix, int eraseCount)
    {
        var target = Layouts.OtherOf(fix.Before);
        var inputs = new List<INPUT>();
        for (int i = 0; i < eraseCount; i++) AddVk(inputs, 0x08);
        AddText(inputs, fix.Fixed);
        AddEnd(inputs, fix.EndVk, fix.EndTextAfter);
        _watcher.ExpectSwitch(target);
        Layouts.Switch(target);
        Send(inputs);
        // After Enter the line may already be sent (chat apps), so there is nothing safe to undo.
        _lastFix = fix.EndVk == 0x0D ? null : fix with { At = DateTime.Now };
        _store.Count(d => d.Fixes++);
        _decider.NoteTypedWord(target);
        Log.Write(new { kind = "fix", from = fix.Typed, to = fix.Fixed, lang = target, app = _decider.Current?.App, site = _decider.Current?.Site });
    }

    private void Undo(LastFix fix)
    {
        var inputs = new List<INPUT>();
        int erase = fix.Fixed.Length + (fix.EndVk is 0x20 or 0x09 ? 1 : fix.EndTextAfter.Length);        for (int i = 0; i < erase; i++) AddVk(inputs, 0x08);
        AddText(inputs, fix.Typed);
        AddEnd(inputs, fix.EndVk, fix.EndTextBefore);
        _watcher.ExpectSwitch(fix.Before);
        Layouts.Switch(fix.Before);
        Send(inputs);
        _store.AddNeverFlip(fix.Before == Lang.En ? fix.Typed.ToLowerInvariant() : fix.Typed);
        _store.Count(d => d.Undos++);
        Log.Write(new { kind = "undo", word = fix.Typed, was = fix.Fixed, app = _decider.Current?.App });
    }

    private static void AddEnd(List<INPUT> inputs, uint endVk, string endText)
    {
        if (endVk is 0x20 or 0x0D or 0x09) AddVk(inputs, (ushort)endVk);
        else AddText(inputs, endText);
    }

    private static void AddVk(List<INPUT> inputs, ushort vk)
    {
        inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } });
        inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } });
    }

    private static void AddText(List<INPUT> inputs, string text)
    {
        foreach (var ch in text)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
    }

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
