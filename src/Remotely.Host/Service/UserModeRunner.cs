using System.Runtime.InteropServices;
using Remotely.Host.Helper;
using Remotely.Host.Unattended;
using Remotely.HostCore;

namespace Remotely.Host.Service;

/// <summary>
/// User-only mode: one background process in the signed-in user's session (started by a Run key at sign-in). Needs no administrator rights.
/// It stops when the user signs out, cannot show the lock or sign-in screen, and cannot type into windows running as administrator.
/// </summary>
internal static class UserModeRunner
{
    public const string RunKeyName = "Remotely.Host";
    private const string StopEventName = @"Local\Remotely.Host.User.Stop";
    private const string SingleInstanceName = @"Local\Remotely.Host.User";

    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    public static async Task<int> RunAsync(FileLog log, bool keepConsole)
    {
        using var single = new Mutex(true, SingleInstanceName, out var first);
        if (!first) { log.Write("Remotely.Host (user mode) is already running."); return 0; }
        if (!keepConsole) FreeConsole(); // started at sign-in: do not leave a console window open

        var identity = HostPaths.LoadOrCreateIdentity();
        log.Write($"Remotely.Host (user mode) starting. Device id: {identity.DeviceId}");
        Directory.CreateDirectory(HostPaths.ReceiveDir);
        Directory.CreateDirectory(HostPaths.ShareDir);

        using var cts = new CancellationTokenSource();
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        _ = Task.Run(() => { stop.WaitOne(); cts.Cancel(); });

        var settings = new CachedSettings(new UnattendedSettingsStore(HostPaths.UnattendedFile));
        using var power = new PowerRequest(log.Write);
        var activity = new SessionActivity(power, () => settings.Current.KeepAwake);
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try { while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) activity.Tick(); }
            catch (OperationCanceledException) { }
        });

        using var desktop = new DesktopThread();
        await HostConnection.RunForeverAsync(HostPaths.ConfigFile, identity,
            (trusted, control) => HostRuntime.ForUser(desktop, activity, trusted, control, log.Write), log.Write, cts.Token).ConfigureAwait(false);
        activity.Shutdown();
        log.Write("Remotely.Host (user mode) stopped.");
        return 0;
    }

    /// <summary>Asks a running user-mode host in this session to stop.</summary>
    public static bool RequestStop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var handle)) return false;
        using (handle) return handle.Set();
    }
}
