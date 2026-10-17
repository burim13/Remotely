using Microsoft.UI.Xaml;
using Remotely.Core.Admin;
using Remotely.Core.ServerManager;
using Remotely.ServerManager.Views;
using Remotely.ServerManager.Views.Help;

namespace Remotely.ServerManager;

public partial class App : Application
{
    private static DispatcherTimer? _timer;
    private static bool _polling;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
    }

    internal static ManagerArgs Args { get; private set; } = ManagerArgs.Parse([]);
    internal static IServerAdmin Admin { get; private set; } = new UnavailableServerAdmin("not started");
    internal static ServerMonitor Monitor { get; private set; } = new(new UnavailableServerAdmin("not started"));
    internal static ManagerSettings Settings { get; private set; } = new();
    internal static MainWindow? Main { get; private set; }
    internal static TrayIcon? Tray { get; private set; }
    internal static bool Exiting { get; private set; }

    /// <summary>Raised on the UI thread after every poll of the server.</summary>
    internal static event Action<MonitorSnapshot>? SnapshotChanged;

    /// <summary>A GUI app that dies silently is undiagnosable; leave the reason where support (and CI) can read it.</summary>
    internal static void LogCrash(Exception? ex)
    {
        Views.Help.DiagnosticsHub.RecordCrash(ex);
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(ManagerSettings.DefaultPath)!, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "manager-crash.log"), DateTime.Now.ToString("u") + "  " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void ApplyTheme(Window window)
    {
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = Settings.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs launchArgs)
    {
        var argv = Environment.GetCommandLineArgs();
        Args = ManagerArgs.Parse(argv);

        // The elevated copy started by ElevatedHelper: do the one job, show nothing, exit.
        if (HelperCommandLine.Parse(argv) is { } helper)
        {
            Environment.Exit(await ElevatedHelperMain.RunAsync(helper));
            return;
        }

        Settings = ManagerSettings.Load();
        Admin = AdminFactory.Create(Args.Fake);
        Monitor = new ServerMonitor(Admin, new ServiceQuery(Args.Fake ? new FakeProcessRunner() : new ProcessRunner()));

        if (Args.SelfTestFile is { } resultFile)
        {
            Environment.Exit(await RunSelfTestAsync(resultFile));
            return;
        }

        // Build every page once before showing anything: a page whose XAML or constructor is broken stops the Manager at start-up with a log entry,
        // which is what the CI "start for a few seconds" step needs in order to notice it.
        if (!Preflight.Run(out var failure))
        {
            LogCrash(failure);
            Environment.Exit(3);
            return;
        }

        Monitor.NewDevices += OnNewDevices;
        var window = new MainWindow();
        Main = window;
        Tray = TrayIcon.TryCreate(TrayStatus.Unknown, Ui.T("App_Name"));
        if (Tray is not null)
        {
            Tray.OpenRequested += ShowMainWindow;
            Tray.ExitRequested += Quit;
        }
        if (!(Args.Minimized && Tray is not null)) window.Activate();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        await PollAsync();
    }

    private static async Task<int> RunSelfTestAsync(string resultFile)
    {
        var lines = new List<string>();
        var ok = await ManagerSelfTest.RunAsync(Admin, Args.RevokeDeviceId, lines.Add);
        try { File.WriteAllLines(resultFile, new[] { ok ? "PASS" : "FAIL" }.Concat(lines)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogCrash(ex); return 2; }
        return ok ? 0 : 1;
    }

    internal static async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var snapshot = await Monitor.RefreshAsync();
            Tray?.SetStatus(ServerMonitor.TrayStatusOf(snapshot, DateTimeOffset.UtcNow), TrayTip(snapshot));
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCrash(ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private static string TrayTip(MonitorSnapshot snapshot)
    {
        var line = Headline.Of(snapshot) switch
        {
            ServerHeadline.Running => Ui.T("Tray_Running", snapshot.Status!.DevicesOnline),
            ServerHeadline.NotAnswering => Ui.T("Tray_NotAnswering"),
            ServerHeadline.Stopped => Ui.T("Tray_Stopped"),
            ServerHeadline.NotInstalled => Ui.T("Tray_NotInstalled"),
            _ => Ui.T("Tray_Unknown"),
        };
        return Ui.T("App_Name") + ": " + line;
    }

    private static void OnNewDevices(IReadOnlyList<AdminDevice> devices)
    {
        if (!Settings.NotifyNewDevices || Tray is null || devices.Count == 0) return;
        var text = devices.Count == 1 ? Ui.T("Notify_NewDevice", devices[0].Name) : Ui.T("Notify_NewDevices", devices.Count);
        Tray.Balloon(Ui.T("Notify_Title"), text);
    }

    internal static void ShowMainWindow()
    {
        if (Main is null) return;
        Main.AppWindow.Show();
        Main.Activate();
    }

    internal static void Quit()
    {
        Exiting = true;
        Cleanup();
        Main?.Close();   // raises Closed, which ends the process through OnWindowClosed
        Current.Exit();
    }

    internal static void OnWindowClosed()
    {
        Exiting = true;
        Cleanup();
        Current.Exit();
    }

    private static void Cleanup()
    {
        _timer?.Stop();
        Tray?.Dispose();
        Tray = null;
    }
}

/// <summary>Constructs each page once (without showing it) so XAML or constructor errors surface at start-up.</summary>
internal static class Preflight
{
    private static readonly Func<Microsoft.UI.Xaml.Controls.Page>[] Pages =
    [
        () => new DashboardPage(),
        () => new InvitePage(),
        () => new DevicesPage(),
        () => new ActivityPage(),
        () => new ServicePage(),
        () => new ServerSettingsPage(),
        () => new DiagnosticsPage(),
        () => new NetworkTestPage(),
    ];

    public static bool Run(out Exception? failure)
    {
        foreach (var make in Pages)
        {
            try { make(); }
            catch (Exception ex)
            {
                failure = ex;
                return false;
            }
        }
        failure = null;
        return true;
    }
}
