using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;

namespace KeyFlip;

/// <summary>Tray icon showing the current layout (HE / EN), with pause, today's cost and the log.</summary>
public sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Store _store;
    private readonly FocusWatcher _watcher;
    private readonly Decider _decider;
    private readonly KeyboardFixer _fixer;
    private readonly LineReader _lines;
    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly Dictionary<string, Icon> _icons = new();
    private string _shown = "";

    public TrayApp()
    {
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;

        Layouts.Init();
        _store = new Store();
        var words = WordChecker.Load(Path.Combine(AppContext.BaseDirectory, "dict"));
        _watcher = new FocusWatcher();
        _decider = new Decider(_store, _watcher);
        _fixer = new KeyboardFixer(_store, _decider, _watcher, words, _ui);
        _lines = new LineReader();
        _lines.Clicked += _decider.OnClick;

        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePause(); };

        _refresh = new System.Windows.Forms.Timer { Interval = 300 };
        _refresh.Tick += (_, _) => UpdateIcon();
        _refresh.Start();

        _fixer.Start();
        _lines.Start();
        _watcher.Start();
        UpdateIcon();

        Log.Write(new { kind = "start", version = Application.ProductVersion, he = Layouts.He.ToString("X"), en = Layouts.En.ToString("X"), jev = _decider.JevAvailable, haiku = _decider.HaikuAvailable });
        if (!Layouts.Ready)
            _tray.ShowBalloonTip(5000, "KeyFlip", "Needs both a Hebrew and an English keyboard installed in Windows settings.", ToolTipIcon.Warning);
    }

    private void UpdateIcon()
    {
        var lang = Layouts.ForegroundLang();
        var label = _store.Config.Paused ? "||" : lang == Lang.He ? "HE" : lang == Lang.En ? "EN" : "??";
        if (label == _shown) return;
        _shown = label;
        _tray.Icon = IconFor(label);
        var t = _store.Today;
        _tray.Text = Trunc($"KeyFlip {(_store.Config.Paused ? "paused" : "on")} - {label}\nToday: {t.Focus} fields, {t.Fixes} fixes, ${t.Usd:0.0000}");
    }

    private static string Trunc(string s) => s.Length > 127 ? s[..127] : s;

    private Icon IconFor(string label)
    {
        if (_icons.TryGetValue(label, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var bg = label == "HE" ? Color.FromArgb(0, 56, 184) : label == "EN" ? Color.FromArgb(200, 30, 45) : Color.FromArgb(90, 90, 90);
            using var brush = new SolidBrush(bg);
            g.FillRectangle(brush, 0, 0, 32, 32);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(label, font);
            g.DrawString(label, font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        var h = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return _icons[label] = icon;
    }

    private void BuildMenu()
    {
        var m = _tray.ContextMenuStrip!;
        m.Items.Clear();
        var t = _store.Today;
        m.Items.Add(new ToolStripMenuItem($"Today: {t.Focus} fields, {t.Jev} Jev, {t.Haiku} Haiku, {t.Fixes} fixes, ${t.Usd:0.0000}") { Enabled = false });
        m.Items.Add(new ToolStripMenuItem($"This month: ${_store.MonthUsd():0.0000}") { Enabled = false });
        var ctx = _decider.Current;
        if (ctx != null)
            m.Items.Add(new ToolStripMenuItem($"Field: {FieldContext.Cut($"{ctx.App} {ctx.Site} \"{ctx.Label}\"", 70)}") { Enabled = false });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem(_store.Config.Paused ? "Resume" : "Pause", null, (_, _) => TogglePause()));
        m.Items.Add(new ToolStripMenuItem("Fix words typed on the wrong layout", null, (_, _) =>
        {
            _store.Config.FixerEnabled = !_store.Config.FixerEnabled;
            _store.SaveConfig();
        }) { Checked = _store.Config.FixerEnabled });
        m.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartup(!IsStartup())) { Checked = IsStartup() });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => Open(Store.LogPath)));
        m.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => Open(Store.Dir)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
    }

    private void TogglePause()
    {
        _store.Config.Paused = !_store.Config.Paused;
        _store.SaveConfig();
        _shown = "";
        UpdateIcon();
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    public static bool IsStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("KeyFlip") is string;
    }

    public static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("KeyFlip", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("KeyFlip", false);
    }

    protected override void ExitThreadCore()
    {
        _watcher.Stop();
        _fixer.Dispose();
        _lines.Dispose();
        _store.Flush();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
