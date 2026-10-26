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
    private readonly WordChecker _words;
    private readonly Control _ui;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    private readonly record struct Key(uint Vk, uint Scan, bool Shift, bool Caps);
    private readonly List<Key> _word = new();
    private Lang _wordLang;
    private int _wordSeq;

    private sealed record LastFix(string Typed, string Fixed, uint EndVk, string EndTextBefore, string EndTextAfter, Lang Before, DateTime At);
    private LastFix? _lastFix;

    public KeyboardFixer(Store store, Decider decider, FocusWatcher watcher, WordChecker words, Control ui)
    {
        _store = store;
        _decider = decider;
        _watcher = watcher;
        _words = words;
        _ui = ui;
        _proc = HookProc;
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
        _word.Clear();
        _lastFix = null;
    }

    private bool OnKeyDown(KBDLLHOOKSTRUCT k)
    {
        uint vk = k.vkCode;
        bool isModifier = vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x14;
        if (isModifier) return false;
        _decider.NoteKey();

        var ctx = _decider.Current;
        if (_store.Config.Paused || !_store.Config.FixerEnabled || !Layouts.Ready || ctx == null || ctx.IsPassword
            || _store.Config.FixerExcludedApps.Contains(ctx.App, StringComparer.OrdinalIgnoreCase))
        {
            Reset();
            return false;
        }

        // Shortcuts (Ctrl / Alt / Win + key) are never typing.
        if (Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C)) { Reset(); return false; }

        // A new field means a new word.
        if (_watcher.Seq != _wordSeq) { _word.Clear(); _wordSeq = _watcher.Seq; }

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
            if (_word.Count > 0) _word.RemoveAt(_word.Count - 1);
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

        char cc = curText[0], oc = othText.Length == 1 ? othText[0] : '\0';
        bool wordKey = current == Lang.En
            ? char.IsLetter(cc) || WordChecker.IsHebrewLetter(oc)
            : WordChecker.IsHebrewLetter(cc) || WordChecker.IsLatinLetter(oc);

        if (wordKey)
        {
            if (_word.Count == 0) _wordLang = current;
            if (_wordLang != current) _word.Clear();
            _wordLang = current;
            _word.Add(new Key(vk, k.scanCode, shift, caps));
            return false;
        }

        // Any other character ends the word.
        if (_word.Count == 0) return false;
        var keys = _word.ToArray();
        _word.Clear();
        var typed = string.Concat(keys.Select(x => Layouts.ToText(x.Vk, x.Scan, x.Shift, x.Caps, Layouts.Hkl(current))));
        var meant = string.Concat(keys.Select(x => Layouts.ToText(x.Vk, x.Scan, x.Shift, x.Caps, Layouts.Hkl(other))));

        var fixedWord = _words.Fix(typed, meant, current, _store.NeverFlip);
        if (fixedWord == null)
        {
            bool real = current == Lang.En ? _words.EnglishRank(typed.Trim(',', '.', ';', '\'')) != 0
                                           : !WordChecker.LooksBrokenHebrew(typed) && _words.HebrewRank(typed) != 0;
            if (real && typed.Length >= 3) _decider.NoteTypedWord(current);
            return false;
        }

        // Swallow the ending key; the fix retypes it after the corrected word.
        var endAfter = vk is 0x20 or 0x0D or 0x09 ? "" : Layouts.ToText(vk, k.scanCode, shift, caps, Layouts.Hkl(other));
        var fix2 = new LastFix(typed, fixedWord, vk, curText, endAfter, current, DateTime.Now);
        _ui.BeginInvoke(() => Apply(fix2, keys.Length));
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
