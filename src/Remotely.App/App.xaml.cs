using Microsoft.UI.Xaml;
using Remotely.Core.Crypto;

namespace Remotely.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
    }

    internal static MainWindow? Main { get; private set; }
    public static ShareService Share { get; } = new();
    internal static ViewerSettings Settings { get; private set; } = new();
    internal static Connection Connection { get; private set; } = null!;
    /// <summary>--invite RMT1-...: join the server in the invite (the installer uses this).</summary>
    internal static string? InviteOnStart { get; private set; }
    /// <summary>--pair CODE: pair with the already configured server.</summary>
    internal static string? PairOnStart { get; private set; }

    /// <summary>A GUI app that dies silently is undiagnosable; leave the reason where support (and CI) can read it.</summary>
    internal static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(ViewerSettings.Dir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-crash.log"), DateTime.Now.ToString("u") + "  " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Applies the saved theme choice ("system", "dark" or "light") to a window's content.</summary>
    internal static void ApplyTheme(Window window)
    {
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = Settings.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        Settings = ViewerSettings.Load();
        DeviceIdentity identity;
        try { identity = ViewerSettings.LoadOrCreateIdentity(); }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            LogCrash(ex);
            identity = DeviceIdentity.Generate();   // the key file is unreadable (copied from another user?); start with a fresh identity
        }
        Connection = new Connection(Settings, identity);
        // The device id is what a host needs to trust this viewer; leave it where scripts and installers can read it.
        try { File.WriteAllText(Path.Combine(ViewerSettings.Dir, "device-id.txt"), identity.DeviceId); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var lan = Array.IndexOf(argv, "--selftest");
        if (lan >= 0 && lan + 1 < argv.Length) { Environment.Exit(await SelfTest.RunAsync(argv[lan + 1])); return; }
        var srv = Array.IndexOf(argv, "--selftest-server");
        if (srv >= 0 && srv + 2 < argv.Length) { Environment.Exit(await SelfTest.RunServerAsync(argv[srv + 1], argv[srv + 2])); return; }
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (argv[i] == "--pair") PairOnStart = argv[i + 1];
            if (argv[i] == "--invite") InviteOnStart = argv[i + 1];
        }
        var main = new MainWindow();
        Main = main;
        _window = main;
        main.Activate();
    }
}
