using Remotely.Core.Diagnostics;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager.Views.Help;

/// <summary>The Manager's rolling log, diagnostics settings and the description of this PC and the server's files for the export. Nothing here opens a network connection.</summary>
internal static class DiagnosticsHub
{
    private static readonly object Gate = new();
    private static RollingFileLog? _log;

    public static string Dir { get; } = Path.GetDirectoryName(ManagerSettings.DefaultPath)!;
    public static string LogDir => Path.Combine(Dir, "logs");
    public static string CrashDumpDir => Path.Combine(Dir, "crashdumps");
    public static DiagnosticsSettings Settings { get; private set; } = DiagnosticsSettings.Load(Dir);

    public static RollingFileLog Log
    {
        get
        {
            lock (Gate) return _log ??= new RollingFileLog(LogDir, "manager", Redactor.Shared, Settings.LogLevel);
        }
    }

    public static void Apply(DiagnosticsSettings settings)
    {
        Settings = settings;
        settings.TrySave(Dir);
        Log.Minimum = settings.LogLevel;
    }

    public static void RecordCrash(Exception? ex)
    {
        Log.Error("crash", ErrorCatalogue.Describe(ex).Code + " " + (ex?.GetType().Name ?? "unknown"), ex);
        if (Settings.CrashDumps) CrashDumps.TryWrite(CrashDumpDir, "manager");
    }

    public static string Version => typeof(DiagnosticsHub).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static SystemDiagnosticsSource Source() => new(
        "Remotely Server Manager", Version,
        logs: [new LogSource("manager", LogDir, "manager"), new LogSource("server", Path.Combine(ManagerPaths.DataDir, "logs"), "server", ServerStyle: true)],
        settings: [new SettingsSource("manager.json", ManagerSettings.DefaultPath, ["StartWithWindows", "CloseToTray", "NotifyNewDevices", "Theme"]),
            new SettingsSource(DiagnosticsSettings.FileName, Path.Combine(Dir, DiagnosticsSettings.FileName), ["LogLevel", "CrashDumps"])],
        crashFiles: [Path.Combine(LogDir, "manager-crash.log"), ManagerPaths.HelperLog],
        crashDumpDirectory: CrashDumpDir,
        firewallRules: SystemDiagnosticsSource.ServerFirewallRules,
        serviceNames: [ServiceQuery.ServiceName]);
}
