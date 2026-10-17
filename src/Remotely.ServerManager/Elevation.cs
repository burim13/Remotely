using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager;

internal sealed record HelperResult(bool Cancelled, int ExitCode, string Log)
{
    public bool Ok => !Cancelled && ExitCode == HelperExit.Ok;
}

/// <summary>
/// Service control without ever running the Manager as administrator. The Manager starts a second copy of itself with <c>--service-helper &lt;verb&gt;</c> through the
/// shell's "runas" verb, which shows the normal Windows permission prompt. That copy runs <see cref="ElevatedHelperMain"/>, does one job and exits; the Manager reads
/// the exit code and the log file it left.
/// </summary>
internal static class ElevatedHelper
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<HelperResult> RunAsync(ServiceVerb verb, string? argument = null)
    {
        if (App.Args.Fake) return new HelperResult(false, HelperExit.Ok, "");   // demo mode: pretend, never ask Windows for permission
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path."))
        {
            Arguments = HelperCommandLine.Build(verb, argument),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null) return new HelperResult(false, HelperExit.StepFailed, "");
            await process.WaitForExitAsync();
            return new HelperResult(false, process.ExitCode, ReadLog());
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == HelperExit.Cancelled)
        {
            return new HelperResult(true, HelperExit.Cancelled, "");   // the user declined the permission prompt
        }
    }

    private static string ReadLog()
    {
        try { return File.ReadAllText(ManagerPaths.HelperLog).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}

/// <summary>Runs inside the elevated copy. Does exactly one request from <see cref="HelperCommandLine"/> and returns the process exit code.</summary>
internal static class ElevatedHelperMain
{
    public static async Task<int> RunAsync(HelperRequest request)
    {
        try
        {
            Directory.CreateDirectory(ManagerPaths.DataDir);
            File.WriteAllText(ManagerPaths.HelperLog, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        void Log(string line)
        {
            if (line.Length == 0) return;
            try { File.AppendAllText(ManagerPaths.HelperLog, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (!request.IsValid) { Log("Unknown request."); return HelperExit.BadUsage; }
        if (!ElevatedHelper.IsElevated()) { Log("The helper is not running as administrator."); return HelperExit.NotElevated; }
        if (Setup.SetupHelperMain.Handles(request.Verb)) return await Setup.SetupHelperMain.RunAsync(request, Log);
        var managerDir = AppContext.BaseDirectory;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var exe = ServerExeLocator.Find(ServerExeLocator.Candidates(request.Verb, managerDir, programFiles), File.Exists);
        var helper = new ServiceHelper(new ProcessRunner(), exe, Log, path => BackupRestorer.Restore(path, ManagerPaths.DataDir));
        try { return await helper.RunAsync(request.Verb, request.Argument); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            Log("Failed: " + ex.Message);
            return HelperExit.StepFailed;
        }
    }
}

/// <summary>"Start the Manager when I sign in": a value under the current user's Run key (no administrator rights needed).</summary>
internal static class AutoStart
{
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ManagerSettings.AutoStartKey);
        return key?.GetValue(ManagerSettings.AutoStartValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ManagerSettings.AutoStartKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ManagerSettings.AutoStartValueName, ManagerSettings.AutoStartCommand(exe));
        else key.DeleteValue(ManagerSettings.AutoStartValueName, throwOnMissingValue: false);
    }
}
