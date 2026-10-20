using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public partial class App : Application
{
    private MainWindow? _window;
    private SingleInstance? _instance;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write(e.ExceptionObject as Exception ?? new Exception("Unhandled exception"));
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ApplicationIdentity.Initialize();
        if (!Environment.GetCommandLineArgs().Contains("--smoke-test"))
        {
            _instance = new SingleInstance();
            if (!_instance.IsPrimary) { _instance.Dispose(); Exit(); return; }
        }
        StartupSession? startup = null;
        if (!Environment.GetCommandLineArgs().Contains("--smoke-test") || Environment.GetCommandLineArgs().Contains("--startup-parallel-check"))
        {
            Diagnostics.StartupTrace.Mark("Load settings");
            var settings = SettingsStore.Load(); Diagnostics.StartupTrace.Mark("Settings loaded; decrypt session");
            var api = new ApiClient(settings); Diagnostics.StartupTrace.Mark("Session decrypted");
            startup = new(settings, api);
            Diagnostics.StartupTrace.Mark("Startup requests scheduled before window construction");
        }
        Diagnostics.StartupTrace.Mark("Create window"); _window = new MainWindow(startup); Diagnostics.StartupTrace.Mark("Window created");
        _instance?.Listen(() => _window.DispatcherQueue.TryEnqueue(_window.RestoreWindow));
        _window.Closed += (_, _) => _instance?.Dispose();
        Diagnostics.StartupTrace.Mark("Activate window"); _window.Activate(); Diagnostics.StartupTrace.Mark("Window activated");
    }
}
