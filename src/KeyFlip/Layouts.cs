using System.Text;
using static KeyFlip.Native;

namespace KeyFlip;

public enum Lang { Other, He, En }

/// <summary>Finds the Hebrew and English layouts, reads and switches the foreground layout,
/// and maps a physical key to the character it gives under a layout (no hard-coded maps).</summary>
internal static class Layouts
{
    public static IntPtr He { get; private set; }
    public static IntPtr En { get; private set; }
    public static bool Ready => He != IntPtr.Zero && En != IntPtr.Zero;

    public static void Init()
    {
        int n = GetKeyboardLayoutList(0, null);
        var list = new IntPtr[n];
        GetKeyboardLayoutList(n, list);
        foreach (var hkl in list)
        {
            var lang = LangOf(hkl);
            if (lang == Lang.He && He == IntPtr.Zero) He = hkl;
            if (lang == Lang.En && En == IntPtr.Zero) En = hkl;
        }
    }

    public static Lang LangOf(IntPtr hkl)
    {
        int langId = (int)((long)hkl & 0xFFFF);
        if (langId == 0x040D) return Lang.He;
        if ((langId & 0x3FF) == 0x09) return Lang.En;
        return Lang.Other;
    }

    public static IntPtr Hkl(Lang lang) => lang == Lang.He ? He : En;
    public static Lang OtherOf(Lang lang) => lang == Lang.He ? Lang.En : Lang.He;

    public static IntPtr ForegroundHkl(out IntPtr foreground)
    {
        foreground = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(foreground, out _);
        return GetKeyboardLayout(thread);
    }

    public static Lang ForegroundLang() => LangOf(ForegroundHkl(out _));

    /// <summary>Asks the focused window to change layout. Posting to the focus window
    /// (not just the top-level one) is what Chrome and Electron apps honor.</summary>
    public static void Switch(Lang lang)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        uint thread = GetWindowThreadProcessId(fg, out _);
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        var target = GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg;
        PostMessage(target, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, Hkl(lang));
    }

    /// <summary>The text a key produces under a layout. Flag 4 keeps the kernel keyboard
    /// state untouched, so calling it from the hook does not break dead keys.</summary>
    public static string ToText(uint vk, uint scan, bool shift, bool caps, IntPtr hkl)
    {
        var state = new byte[256];
        if (shift) { state[0x10] = 0x80; state[0xA0] = 0x80; }
        if (caps) state[0x14] = 0x01;
        if (scan == 0) scan = MapVirtualKeyEx(vk, 0, hkl);
        var sb = new StringBuilder(8);
        int n = ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 4, hkl);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    /// <summary>Retypes text as if it had been typed on the other layout. Used by the tests
    /// to turn "writing" into what the Hebrew layout really produces for those keys.</summary>
    public static string Transliterate(string text, IntPtr fromHkl, IntPtr toHkl)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            short scan = VkKeyScanEx(ch, fromHkl);
            if (scan == -1) { sb.Append(ch); continue; }
            uint vk = (uint)(scan & 0xFF);
            bool shift = (scan & 0x100) != 0;
            var t = ToText(vk, 0, shift, false, toHkl);
            sb.Append(t.Length > 0 ? t : ch.ToString());
        }
        return sb.ToString();
    }
}
