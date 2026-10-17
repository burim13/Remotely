using System.ComponentModel;
using System.Diagnostics;
using Remotely.Unattended;

namespace Remotely.App.Views.Unattended;

public enum ElevatedOutcome
{
    /// <summary>The elevated program ran and reported success.</summary>
    Done,
    /// <summary>It ran and reported a failure, or did not finish in time.</summary>
    Failed,
    /// <summary>The person closed the Windows administrator prompt. Nothing was changed, and nothing asks again by itself.</summary>
    Cancelled,
    /// <summary>Remotely.Host.exe is not next to the app.</summary>
    NotFound,
    /// <summary>Remotely.Host.exe is somewhere an ordinary user can write (not below Program Files), so the app will not ask for administrator rights for it.</summary>
    NotTrusted,
    /// <summary>Another administrator action is still running.</summary>
    Busy,
    /// <summary>It ran, but Windows did not let this app read how it ended. The page shows the state as it is now.</summary>
    Unknown,
}

/// <summary>
/// Runs <c>Remotely.Host.exe</c> with administrator rights, one fixed action at a time (<see cref="ElevatedAction"/>). One Windows prompt per click: a refusal ends the action, there is
/// no retry and no loop. The program must be the one installed next to this app below Program Files (see <see cref="ElevatedActions.IsTrustedLocation"/>).
/// </summary>
internal static class ElevatedHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const int ErrorCancelled = 1223;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, ElevatedActions.HostFileName);

    /// <summary>Why elevation is not possible here, or null when it is.</summary>
    public static ElevatedOutcome? Unavailable()
    {
        if (!File.Exists(ExePath)) return ElevatedOutcome.NotFound;
        if (!ElevatedActions.IsTrustedLocation(ExePath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) return ElevatedOutcome.NotTrusted;
        return null;
    }

    public static async Task<ElevatedOutcome> RunAsync(ElevatedAction action, CancellationToken ct = default)
    {
        if (Unavailable() is { } problem) return problem;
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) return ElevatedOutcome.Busy;
        try
        {
            var info = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(' ', ElevatedActions.Arguments(action)),   // fixed words without spaces or quotes (a test checks this)
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process? process;
            try { process = Process.Start(info); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return ElevatedOutcome.Cancelled; }
            catch (Win32Exception) { return ElevatedOutcome.Failed; }
            if (process is null) return ElevatedOutcome.Failed;
            using (process)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return ElevatedOutcome.Failed; }   // an elevated process cannot be killed from here; it is left to finish
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return ElevatedOutcome.Unknown; }
                try { return process.ExitCode == 0 ? ElevatedOutcome.Done : ElevatedOutcome.Failed; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return ElevatedOutcome.Unknown; }
            }
        }
        finally { Gate.Release(); }
    }
}
