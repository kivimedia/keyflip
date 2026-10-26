namespace KeyFlip;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\KeyFlip-single-instance", out bool first);
        if (!first) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write(new { kind = "error", where = "ui", error = e.Exception.ToString() });
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write(new { kind = "crash", error = e.ExceptionObject.ToString() });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Write(new { kind = "exit" });

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
