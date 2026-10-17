using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Remotely.Core.ServerManager;

public enum ServiceState { Unknown, NotInstalled, Stopped, StartPending, Running, StopPending, Paused }

/// <summary>Reads the output of <c>sc.exe query</c>. Uses the numeric state, which is the same in every Windows language.</summary>
public static partial class ScOutput
{
    public const int ServiceDoesNotExist = 1060;
    public const int ServiceAlreadyRunning = 1056;
    public const int ServiceNotActive = 1062;

    [GeneratedRegex(@"STATE\s*:\s*(\d+)")]
    private static partial Regex StateLine();

    public static ServiceState Parse(int exitCode, string output)
    {
        if (exitCode == ServiceDoesNotExist) return ServiceState.NotInstalled;
        var match = StateLine().Match(output ?? "");
        if (!match.Success) return ServiceState.Unknown;
        return match.Groups[1].Value switch
        {
            "1" => ServiceState.Stopped,
            "2" => ServiceState.StartPending,
            "3" => ServiceState.StopPending,
            "4" => ServiceState.Running,
            "5" or "6" => ServiceState.StartPending,
            "7" => ServiceState.Paused,
            _ => ServiceState.Unknown,
        };
    }
}

public interface IProcessRunner
{
    /// <summary>Runs a program without a window, waits, returns the exit code and what it printed.</summary>
    Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}

/// <summary>Asks Windows for the state of the server's service. Needs no administrator rights (reading a service's state is open to normal users on a default install; not verified on a real machine).</summary>
public sealed class ServiceQuery(IProcessRunner runner)
{
    public const string ServiceName = "Remotely.Server";

    public async Task<ServiceState> QueryAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, output) = await runner.RunAsync("sc.exe", ["query", ServiceName], ct).ConfigureAwait(false);
            return ScOutput.Parse(code, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ServiceState.Unknown;
        }
    }
}

public enum ServiceVerb { Install, Uninstall, Start, Stop, Restart, Restore }

/// <summary>
/// The command line of the elevated helper. The Manager (never elevated) starts a second copy of itself with the verb <c>runas</c>; that copy parses this,
/// does one job with <see cref="ServiceHelper"/> and exits. Only the fixed verbs below are accepted, and only one argument (a path, for restore).
/// </summary>
public static class HelperCommandLine
{
    public const string Switch = "--service-helper";

    public static string Build(ServiceVerb verb, string? argument = null) =>
        Switch + " " + verb.ToString().ToLowerInvariant() + (argument is null ? "" : " \"" + argument.Replace("\"", "") + "\"");

    /// <summary>Null when the arguments are not a helper request (the normal start of the Manager). A request with an unknown verb comes back with <see cref="HelperRequest.IsValid"/> false.</summary>
    public static HelperRequest? Parse(IReadOnlyList<string> argv)
    {
        var at = -1;
        for (var i = 0; i < argv.Count; i++) if (argv[i] == Switch) { at = i; break; }
        if (at < 0) return null;
        if (at + 1 >= argv.Count || !Enum.TryParse<ServiceVerb>(argv[at + 1], ignoreCase: true, out var verb) || !Enum.IsDefined(verb))
            return new HelperRequest(ServiceVerb.Stop, null, false);
        return new HelperRequest(verb, at + 2 < argv.Count ? argv[at + 2] : null, true);
    }
}

public sealed record HelperRequest(ServiceVerb Verb, string? Argument, bool IsValid);

public static class HelperExit
{
    public const int Ok = 0;
    public const int StepFailed = 1;
    public const int BadUsage = 2;
    public const int ServerExeMissing = 3;
    public const int NotElevated = 4;
    public const int Cancelled = 1223;   // ERROR_CANCELLED: the user declined the UAC prompt (reported by the launcher, not by the helper)
}

/// <summary>Fixed places the elevated helper and the Manager agree on. Same data folder as <c>ServerOptions.ResolveDataDir</c> on Windows.</summary>
public static class ManagerPaths
{
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remotely Server");

    /// <summary>What the last elevated helper run did. Written by the helper, read by the Manager (normal users can read ProgramData).</summary>
    public static string HelperLog => Path.Combine(DataDir, "manager-helper.log");
}

/// <summary>Where the helper looks for <c>Remotely.Server.exe</c>. Fixed places only: the helper runs elevated, so it never runs a path the caller supplies.</summary>
public static class ServerExeLocator
{
    public const string ExeName = "Remotely.Server.exe";

    public static IReadOnlyList<string> Candidates(ServiceVerb verb, string managerDir, string programFilesDir)
    {
        var installed = Path.Combine(programFilesDir, "Remotely Server", ExeName);
        var beside = Path.Combine(managerDir, ExeName);
        var inFolder = Path.Combine(managerDir, "Server", ExeName);
        // Installing copies the server from where it runs, so a fresh copy beside the Manager wins; every other verb prefers the installed one.
        return verb == ServiceVerb.Install ? [beside, inFolder, installed] : [installed, beside, inFolder];
    }

    public static string? Find(IEnumerable<string> candidates, Func<string, bool> exists) => candidates.FirstOrDefault(exists);
}

/// <summary>
/// The work the elevated helper does. It runs only the server's own commands (<c>Remotely.Server.exe install|uninstall</c>) and <c>sc.exe start|stop</c> for the one
/// service named <see cref="ServiceQuery.ServiceName"/>.
/// </summary>
public sealed class ServiceHelper(IProcessRunner runner, string? serverExe, Action<string> log, Func<string, int>? restore = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<int> RunAsync(ServiceVerb verb, string? argument, CancellationToken ct = default)
    {
        log($"{verb} {argument}".Trim());
        switch (verb)
        {
            case ServiceVerb.Install:
            case ServiceVerb.Uninstall:
                if (serverExe is null) { log("Remotely.Server.exe was not found."); return HelperExit.ServerExeMissing; }
                return await ServerCommandAsync(verb.ToString().ToLowerInvariant(), ct).ConfigureAwait(false);
            case ServiceVerb.Start:
                return await StartAsync(ct).ConfigureAwait(false);
            case ServiceVerb.Stop:
                return await StopAsync(ct).ConfigureAwait(false);
            case ServiceVerb.Restart:
                var stopResult = await StopAsync(ct).ConfigureAwait(false);
                return stopResult != HelperExit.Ok ? stopResult : await StartAsync(ct).ConfigureAwait(false);
            case ServiceVerb.Restore:
                if (string.IsNullOrWhiteSpace(argument) || restore is null) { log("Restore needs the path of a backup."); return HelperExit.BadUsage; }
                var stop = await StopAsync(ct).ConfigureAwait(false);
                if (stop != HelperExit.Ok) return stop;
                try { log($"Restored {restore(argument)} file(s)."); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    log("Restore failed: " + ex.Message);
                    await StartAsync(ct).ConfigureAwait(false);   // leave the server running with what it had
                    return HelperExit.StepFailed;
                }
                return await StartAsync(ct).ConfigureAwait(false);
            default:
                return HelperExit.BadUsage;
        }
    }

    private async Task<int> ServerCommandAsync(string command, CancellationToken ct)
    {
        var (code, output) = await runner.RunAsync(serverExe!, [command], ct).ConfigureAwait(false);
        log(output.Trim());
        return code == 0 ? HelperExit.Ok : HelperExit.StepFailed;
    }

    private async Task<int> StartAsync(CancellationToken ct)
    {
        var (code, output) = await runner.RunAsync("sc.exe", ["start", ServiceQuery.ServiceName], ct).ConfigureAwait(false);
        log(output.Trim());
        if (code != 0 && code != ScOutput.ServiceAlreadyRunning) return HelperExit.StepFailed;
        return await WaitForAsync(ServiceState.Running, ct).ConfigureAwait(false) ? HelperExit.Ok : HelperExit.StepFailed;
    }

    private async Task<int> StopAsync(CancellationToken ct)
    {
        var (code, output) = await runner.RunAsync("sc.exe", ["stop", ServiceQuery.ServiceName], ct).ConfigureAwait(false);
        log(output.Trim());
        if (code != 0 && code != ScOutput.ServiceNotActive && code != ScOutput.ServiceDoesNotExist) return HelperExit.StepFailed;
        return await WaitForAsync(ServiceState.Stopped, ct).ConfigureAwait(false) ? HelperExit.Ok : HelperExit.StepFailed;
    }

    private async Task<bool> WaitForAsync(ServiceState wanted, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            var (code, output) = await runner.RunAsync("sc.exe", ["query", ServiceQuery.ServiceName], ct).ConfigureAwait(false);
            var state = ScOutput.Parse(code, output);
            if (state == wanted || (wanted == ServiceState.Stopped && state == ServiceState.NotInstalled)) return true;
            await _delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        log("Timed out waiting for the service to become " + wanted);
        return false;
    }
}
