using Microsoft.UI.Xaml;

namespace GigaGlade;

public partial class App : Application
{
    Window? _window;

    public App()
    {
        InitializeComponent();
        AppLog.Write($"start, administrator={Environment.IsPrivilegedProcess}, version={typeof(App).Assembly.GetName().Version}");
        UnhandledException += (_, e) => AppLog.Write($"error (UI): {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"crash: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Write($"error (task): {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
