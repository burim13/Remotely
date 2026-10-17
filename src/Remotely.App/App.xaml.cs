using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Remotely.App.Services;
using Remotely.App.Shell;
using Remotely.Core.Crypto;
using Remotely.UI.Localization;

namespace Remotely.App;

public partial class App : Application
{
    private Window? _window;
    private static SingleInstance? _single;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            if (CrashReporter.TryHandle(e.Exception ?? new InvalidOperationException(e.Message))) e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);
    }

    internal static MainWindow? Main { get; private set; }
    public static ShareService Share { get; } = new();
    internal static ViewerSettings Settings { get; private set; } = new();
    internal static Connection Connection { get; private set; } = null!;
    internal static AppServices Services { get; } = new();
    internal static TrayIcon? Tray { get; private set; }
    /// <summary>The parsed command line of this process (see docs/cli.md).</summary>
    internal static AppOptions Options { get; private set; } = new();
    /// <summary>--invite RMT1-...: join the server in the invite (the installer uses this).</summary>
    internal static string? InviteOnStart => Options.Invite;
    /// <summary>--pair CODE: pair with the already configured server.</summary>
    internal static string? PairOnStart => Options.Pair;
    /// <summary>--show PageName: open the app on that page (any <c>Page</c> class in this assembly, by short name). Used by CI to start and photograph every screen.</summary>
    internal static string? ShowOnStart => Options.Show;

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

    /// <summary>One line in logs/app-startup.log: what the shell decided at start-up (single instance, options, invite result). Read by --selftest-server when it fails.</summary>
    internal static void LogInfo(string text)
    {
        try
        {
            var dir = Path.Combine(ViewerSettings.Dir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-startup.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + Environment.ProcessId + "] " + text + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string ReadLogs()
    {
        var text = new System.Text.StringBuilder();
        foreach (var name in new[] { "app-startup.log", "app-crash.log" })
        {
            try
            {
                var path = Path.Combine(ViewerSettings.Dir, "logs", name);
                if (File.Exists(path)) text.AppendLine("--- " + name).AppendLine(string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(40)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return text.ToString();
    }

    /// <summary>Applies the saved theme choice ("system", "dark" or "light") to a window's content.</summary>
    internal static void ApplyTheme(Window window)
    {
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = Settings.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
    }

    /// <summary>Called when the main window has closed: the tray icon and the single-instance claim go away with it.</summary>
    internal static void ReleaseShell()
    {
        Tray?.Dispose();
        Tray = null;
        _single?.Dispose();
        _single = null;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        Options = AppOptions.Parse(argv);
        LogInfo($"start: args=[{string.Join(" ", argv.Select(a => a.Length > 12 ? a[..12] + "..." : a))}] singleInstance={Options.UsesSingleInstance}");
        foreach (var problem in Options.Problems) LogCrash(new ArgumentException("Command line: " + problem));

        if (Options.UsesSingleInstance && !await ClaimSingleInstanceAsync(argv))
        {
            Environment.Exit(0);
            return;
        }

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

        if (Options.SelfTestResultPath is { } lanFile) { Environment.Exit(await SelfTest.RunAsync(lanFile)); return; }
        if (Options.SelfTestServerResultPath is { } serverFile && Options.SelfTestServerHostPrefix is { } hostPrefix)
        {
            Environment.Exit(await SelfTest.RunServerAsync(serverFile, hostPrefix));
            return;
        }

        // CI starts every page with --show. A build whose strings did not load would show resource keys everywhere, so fail that launch loudly.
        if ((Options.Show is not null || Options.UiaScanResultPath is not null) && Loc.Get("App_Name") == "App_Name")
        {
            LogCrash(new InvalidOperationException("The string resources (Strings/en-US/Resources.resw) did not load."));
            Environment.Exit(3);
            return;
        }

        if (Options.UiaScanResultPath is { } scanFile) { Environment.Exit(await UiaScan.RunAsync(scanFile, Options.UiaScanPagesPath)); return; }

        try
        {
            BuiltInPages.Register();
            var main = new MainWindow();
            Main = main;
            _window = main;
            if (Options.UsesSingleInstance) Tray = CreateTray();

            if (Options.Minimized && Tray is { IsAdded: true } && !MainWindow.NeedsOnboarding) { /* start in the tray */ }
            else main.ShowInitially();

            if (Settings.ShareOnStart && Options.UsesSingleInstance && Share.Start(_ => { }) is { } shareError) LogCrash(new InvalidOperationException(shareError));
            RunOptions(main, Options);
        }
        catch (Exception ex)
        {
            // A window that failed to build must not leave an invisible process behind (CI would call that "running").
            LogCrash(ex);
            Environment.Exit(2);
        }
    }

    private static void RunOptions(MainWindow main, AppOptions options) =>
        _ = main.HandleOptionsAsync(options).ContinueWith(t => LogCrash(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

    private static TrayIcon? CreateTray()
    {
        try { return TrayMenu.Create(); }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            LogCrash(ex);   // no tray: the window then always opens and closing it quits, as before
            return null;
        }
    }

    /// <summary>True when this process is the only one (or the running one did not answer); false when the arguments were handed to the running instance.</summary>
    private static async Task<bool> ClaimSingleInstanceAsync(string[] argv)
    {
        var user = new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
        var name = $"Remotely.App.{user}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        // A previous copy that was just killed can still own the mutex and a pipe for a moment (process teardown is not instant). So each round
        // asks the owner briefly, and a round that finds no owner any more simply becomes the first instance.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        do
        {
            var instance = SingleInstance.Acquire(name);
            if (instance.IsPrimary)
            {
                LogInfo("single instance: this process is the first one");
                instance.Listen(OnForwarded);
                _single = instance;
                return true;
            }
            var forwarded = await instance.ForwardAsync(argv, TimeSpan.FromMilliseconds(800), pid => AllowSetForegroundWindow(pid));
            instance.Dispose();
            LogInfo("single instance: another copy owns the name; forwarded=" + forwarded);
            if (forwarded) return false;
            await Task.Delay(150);
        }
        while (DateTime.UtcNow < deadline);
        LogCrash(new TimeoutException("Another Remotely is running but did not answer; starting anyway."));
        return true;
    }

    /// <summary>Arguments of a later launch arrive here (on a worker thread): bring the window up and act on them.</summary>
    private static void OnForwarded(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options.Minimized && !options.HasWork) return;   // Windows started a second copy at sign-in: nothing to show
        Main?.DispatcherQueue.TryEnqueue(() =>
        {
            Main?.ShowAndFocus();
            if (Main is { } m) RunOptions(m, options);
        });
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
