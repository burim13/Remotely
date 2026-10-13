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

    /// <summary>--connect &lt;host id or prefix&gt;: open a session to that host as soon as the device list loads (used by shortcuts and tests).</summary>
    internal string? ConnectOnStart { get; private set; }
    /// <summary>--invite &lt;RMT1-...&gt;: join the server in the invite (used by the installer after it has set up this PC's host).</summary>
    internal string? InviteOnStart { get; private set; }
    /// <summary>--pair &lt;code&gt;: pair this device with the configured server at start-up (used by installers and tests).</summary>
    internal string? PairOnStart { get; private set; }

    internal static new App Current => (App)Application.Current;

    // A GUI app that dies silently is undiagnosable; leave the reason where support (and CI) can read it.
    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogCrash(a.ExceptionObject as Exception);
    }

    public App()
    {
        DispatcherUnhandledException += (_, a) => LogCrash(a.Exception);
    }

    internal static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remotely", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "viewer-crash.log"), DateTime.Now.ToString("u") + "  " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = ViewerSettings.Load();
        _theme = new ThemeManager(Settings);
        var identity = ViewerSettings.LoadOrCreateIdentity();
        _connection = new Connection(Settings, identity);
        // The device id is what a host needs to trust this viewer; leave it where scripts and installers can read it.
        try { File.WriteAllText(Path.Combine(ViewerSettings.Dir, "device-id.txt"), identity.DeviceId); }
        catch (IOException) { /* not essential */ }
        catch (UnauthorizedAccessException) { }

        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--connect") ConnectOnStart = e.Args[i + 1];
            if (e.Args[i] == "--pair") PairOnStart = e.Args[i + 1];
            if (e.Args[i] == "--invite") InviteOnStart = e.Args[i + 1];
        }
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
