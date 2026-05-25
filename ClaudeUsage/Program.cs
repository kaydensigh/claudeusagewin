namespace ClaudeUsage;

// WPF Application.Run() provides the Win32 message loop (STA) required by H.NotifyIcon and the HUD window.
// ShutdownMode.OnExplicitShutdown keeps the process alive when the HUD is closed; tray Exit still calls Environment.Exit.
static class Program
{
    [STAThread]
    static void Main()
    {
        var wpfApp = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var app = new App();
        app.Start();
        wpfApp.Run();
        app.Shutdown();
    }
}
