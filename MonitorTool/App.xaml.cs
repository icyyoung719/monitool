using System.Text;
using Microsoft.UI.Xaml;

namespace MonitorTool;

/// <summary>
/// Application entry point. Creates the overlay window on launch.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorTool");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "startup-error.log");

            var sb = new StringBuilder();
            sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Message: {e.Message}");
            sb.AppendLine($"Exception: {e.Exception}");
            sb.AppendLine(new string('-', 80));

            File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
