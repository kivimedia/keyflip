using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static KeyFlip.Native;

namespace KeyFlip;

/// <summary>
/// Watches left clicks (low-level mouse hook, UI thread) and captures a thin strip of the screen around
/// the click. Google Docs draws text on a canvas, so the screen is the only place its text exists.
/// </summary>
public sealed class LineReader : IDisposable
{
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    /// <summary>Raised on the UI thread with the click point in physical screen pixels.</summary>
    public event Action<Point>? Clicked;

    public LineReader() => _proc = HookProc;

    public void Start() => _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_LBUTTONUP)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((m.flags & LLMHF_INJECTED) == 0)
            {
                try { Clicked?.Invoke(new Point(m.pt.X, m.pt.Y)); } catch { }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// PNG of the window's width (at most 1600 px, centered on the click) and 96 px tall around the click,
    /// with a red dot at the click so the model knows which line is meant. Null if the click is outside the window.
    /// </summary>
    public static byte[]? CaptureStrip(IntPtr window, Point click)
    {
        if (!GetWindowRect(window, out var r)) return null;
        var area = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return Strip(area, click, (g, src) => g.CopyFromScreen(src.Location, Point.Empty, src.Size));
    }

    /// <summary>The same strip cut from a saved screenshot (used by the tests).</summary>
    public static byte[]? StripFromImage(Bitmap screenshot, Point click) =>
        Strip(new Rectangle(0, 0, screenshot.Width, screenshot.Height), click,
              (g, src) => g.DrawImage(screenshot, new Rectangle(Point.Empty, src.Size), src, GraphicsUnit.Pixel));

    private static byte[]? Strip(Rectangle area, Point click, Action<Graphics, Rectangle> draw)
    {
        if (!area.Contains(click)) return null;
        int width = Math.Min(area.Width, 1600);
        int height = Math.Min(area.Height, 96);
        if (width < 50) return null;
        int left = Math.Clamp(click.X - width / 2, area.Left, area.Right - width);
        int top = Math.Clamp(click.Y - height / 2, area.Top, area.Bottom - height);
        var src = new Rectangle(left, top, width, height);

        using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            draw(g, src);
            using var red = new SolidBrush(Color.FromArgb(230, 0, 0));
            g.FillEllipse(red, click.X - left - 5, click.Y - top - 5, 10, 10);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
