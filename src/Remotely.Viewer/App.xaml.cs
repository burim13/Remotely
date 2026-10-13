using System.Windows;
using Remotely.Viewer.Shell;
using Remotely.Viewer.Views;

namespace Remotely.Viewer;

public partial class App : Application
{
    private ThemeManager? _theme;
    private Connection? _connection;

    internal ViewerSettings Settings { get; private set; } = new();
    internal Connection Connection => _connection!;

    internal static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = ViewerSettings.Load();
        _theme = new ThemeManager(Settings);
        _connection = new Connection(Settings, ViewerSettings.LoadOrCreateIdentity());
        new MainWindow().Show();
    }

    internal void ApplyTheme() => _theme?.Apply();

    protected override void OnExit(ExitEventArgs e)
    {
        _theme?.Dispose();
        _connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }
}
