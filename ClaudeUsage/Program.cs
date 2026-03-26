using System.Runtime.InteropServices;

namespace ClaudeUsage;

// Raw Win32 message pump — replaces WinForms Application.Run().
// The tray icons (H.NotifyIcon) need a message loop on an STA thread to receive
// shell notification callbacks (mouse clicks, context-menu events, etc.).
// GetMessage blocks until a message arrives; it returns 0 only when WM_QUIT is
// posted (via PostQuitMessage in App.cs), which cleanly breaks the loop.
static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [STAThread]
    static void Main()
    {
        var app = new App();
        app.Start();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        app.Shutdown();
    }
}
