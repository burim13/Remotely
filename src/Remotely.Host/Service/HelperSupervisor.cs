using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Remotely.Host.Interop;

namespace Remotely.Host.Service;

/// <summary>Keeps one capture helper running in the active console session, relaunching it when the console session changes or the helper dies.</summary>
internal sealed class HelperSupervisor(PipeFrameSource source, Action<string> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        Process? helper = null;
        uint helperSession = Native.NoSession;
        var announcedNoSession = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var console = Native.WTSGetActiveConsoleSessionId();
                var healthy = helper is { HasExited: false } && helperSession == console && source.IsAttached;
                if (!healthy)
                {
                    Stop(helper);
                    helper = null;
                    source.Attach(null);
                    if (console == Native.NoSession)
                    {
                        if (!announcedNoSession) log("No console session yet; waiting.");
                        announcedNoSession = true;
                    }
                    else
                    {
                        announcedNoSession = false;
                        helper = await TryLaunchAsync(console, ct).ConfigureAwait(false);
                        helperSession = helper is null ? Native.NoSession : console;
                    }
                }
                await Task.Delay(helper is null ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Stop(helper);
            source.Attach(null);
        }
    }

    private async Task<Process?> TryLaunchAsync(uint session, CancellationToken ct)
    {
        var pipeName = "remotely-helper-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
            var pid = SessionLauncher.StartInSession(session, $"\"{exe}\" helper --pipe {pipeName}");
            log($"Started helper (pid {pid}) in session {session}.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            // Only accept the process we started: the pipe is SYSTEM-only, but verify who actually connected.
            if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientPid) || clientPid != pid)
            {
                log($"Pipe client pid {clientPid} is not the helper we started ({pid}); rejecting.");
                KillQuietly(pid);
                pipe.Dispose();
                return null;
            }
            var process = Process.GetProcessById(pid);
            source.Attach(pipe);
            log("Helper connected; screen capture is available.");
            return process;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException or ArgumentException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) { pipe.Dispose(); throw; }
            log("Could not start the helper: " + ex.Message);
            pipe.Dispose();
            return null;
        }
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    private static void KillQuietly(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
