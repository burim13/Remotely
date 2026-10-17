using Remotely.Core.Diagnostics;

namespace Remotely.App.Views.Help;

/// <summary>
/// The app's one rolling log, its diagnostics settings and the description of this PC for the export. Everything is local: nothing here opens a network connection.
/// The older single-purpose files (logs/app-startup.log, logs/app-crash.log) are still written by <c>App.LogInfo</c> and <c>App.LogCrash</c>; those methods also write here.
/// </summary>
internal static class DiagnosticsHub
{
    private static readonly object Gate = new();
    private static RollingFileLog? _log;

    public static string LogDir => Path.Combine(ViewerSettings.Dir, "logs");
    public static string CrashDumpDir => Path.Combine(ViewerSettings.Dir, "crashdumps");
    public static DiagnosticsSettings Settings { get; private set; } = DiagnosticsSettings.Load(ViewerSettings.Dir);

    public static RollingFileLog Log
    {
        get
        {
            lock (Gate) return _log ??= new RollingFileLog(LogDir, "app", Redactor.Shared, Settings.LogLevel);
        }
    }

    public static void Apply(DiagnosticsSettings settings)
    {
        Settings = settings;
        settings.TrySave(ViewerSettings.Dir);
        Log.Minimum = settings.LogLevel;
    }

    /// <summary>Called when the program is about to crash or has caught an unexpected error: one log line, and a dump when the person switched dumps on.</summary>
    public static void RecordCrash(Exception? ex)
    {
        Log.Error("crash", ErrorCatalogue.Describe(ex).Code + " " + (ex?.GetType().Name ?? "unknown"), ex);
        if (Settings.CrashDumps) CrashDumps.TryWrite(CrashDumpDir, "app");
    }

    public static string Version => typeof(DiagnosticsHub).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static SystemDiagnosticsSource Source() => new(
        "Remotely", Version,
        logs: [new LogSource("app", LogDir, "app")],
        settings: [new SettingsSource("viewer.json", Path.Combine(ViewerSettings.Dir, "viewer.json"),
            ["DeviceName", "Theme", "SyncClipboard", "AllowInsecureTransport", "OnboardingDone", "CloseToTray", "StartWithWindows", "ShareOnStart",
             "WindowX", "WindowY", "WindowWidth", "WindowHeight", "WindowMaximized"]),
            new SettingsSource(DiagnosticsSettings.FileName, Path.Combine(ViewerSettings.Dir, DiagnosticsSettings.FileName), ["LogLevel", "CrashDumps"])],
        crashFiles: [Path.Combine(LogDir, "app-crash.log"), Path.Combine(LogDir, "app-startup.log")],
        crashDumpDirectory: CrashDumpDir,
        firewallRules: SystemDiagnosticsSource.AppFirewallRules,
        serviceNames: ["Remotely.Host"]);
}
