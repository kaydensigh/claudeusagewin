using Forms = System.Windows.Forms;

namespace ClaudeUsage;

static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new App();
        app.Start();
        Forms.Application.Run();
        app.Shutdown();
    }
}
