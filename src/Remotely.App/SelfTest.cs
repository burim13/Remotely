using Remotely.Core.Lan;
using Remotely.Client;
using Remotely.ViewerCore;

namespace Remotely.App;

/// <summary>
/// <c>Remotely.exe --selftest result.txt</c> (same network) and <c>Remotely.exe --selftest-server result.txt hostIdPrefix</c> (through the configured server, the same calls the My computers page makes): shares this PC, connects to it with the displayed token, waits for a real screen frame, sends a mouse move and
/// writes PASS or FAIL plus details to the file. Used by CI on real Windows machines; also handy to check a PC.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string resultPath)
    {
        var lines = new List<string>();
        void Log(string s) { lock (lines) lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {s}"); }
        try
        {
            var share = new ShareService();
            var error = share.Start(Log);
            if (error is not null) throw new InvalidOperationException(error);
            var host = share.Host!;
            var token = host.CurrentToken.Display;
            Log($"Sharing, token {token}, port {host.TcpPort}");

            // Connect through the PC's own network address (loopback is refused by design, like any PC outside the subnet).
            var address = LanNetwork.LocalAddresses().FirstOrDefault()?.ToString() ?? throw new InvalidOperationException("No network address on this PC.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var found = await LanConnector.ConnectAsync(token, "selftest", $"{address}:{host.TcpPort}", cts.Token);
            Log($"Connected to {found.Endpoint}");
            await using var session = await ViewerSession.OpenAsync(found.Channel, "selftest", cts.Token);
            var gotFrame = new TaskCompletionSource<(int W, int H, int Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.FrameReceived += f => gotFrame.TrySetResult((f.Width, f.Height, f.Jpeg.Length));
            if (session.LatestFrame is { } early) gotFrame.TrySetResult((early.Width, early.Height, early.Jpeg.Length));   // arrived before we subscribed
            if (!session.CanControl) throw new InvalidOperationException("Session is view-only.");
            var frame = await gotFrame.Task.WaitAsync(cts.Token);
            Log($"Frame {frame.W}x{frame.H}, {frame.Bytes} bytes");
            session.SendInput(Remotely.HostCore.InputEvent.Move(30000, 30000));
            await Task.Delay(500, cts.Token);

            // The token is single use: a second try with the same token must be refused.
            try
            {
                await LanConnector.ConnectAsync(token, "selftest2", $"{address}:{host.TcpPort}", cts.Token);
                throw new InvalidOperationException("A used token was accepted again.");
            }
            catch (LanConnectException ex) { Log("Used token refused: " + ex.Message); }

            await session.DisposeAsync();
            await share.DisposeAsync();
            File.WriteAllLines(resultPath, ["PASS", .. lines]);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            File.WriteAllLines(resultPath, ["FAIL", .. lines]);
            return 1;
        }
    }

    public static async Task<int> RunServerAsync(string resultPath, string hostPrefix)
    {
        var lines = new List<string>();
        void Log(string s) => lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {s}");
        try
        {
            if (!App.Settings.IsConfigured) throw new InvalidOperationException("No server is configured for this app.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var client = await App.Connection.EnsureAsync(cts.Token);
            var devices = await client.ListDevicesAsync(cts.Token);
            Log($"Server lists {devices.Length} device(s): {string.Join(", ", devices.Select(d => d.Name + (d.Online ? " (online)" : "")))}");
            var host = devices.FirstOrDefault(d => d.Online && d.CanHost && d.DeviceId.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The host is not online in the server's list.");
            await using var session = await ViewerSession.OpenAsync(client, App.Connection.Identity, host.DeviceId, cts.Token);
            var gotFrame = new TaskCompletionSource<(int W, int H, int Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.FrameReceived += f => gotFrame.TrySetResult((f.Width, f.Height, f.Jpeg.Length));
            if (session.LatestFrame is { } early) gotFrame.TrySetResult((early.Width, early.Height, early.Jpeg.Length));   // arrived before we subscribed
            if (!session.CanControl) throw new InvalidOperationException("Session is view-only.");
            var frame = await gotFrame.Task.WaitAsync(cts.Token);
            Log($"Frame {frame.W}x{frame.H}, {frame.Bytes} bytes, Ctrl+Alt+Del offered: {session.CanSendSecureAttention}");
            session.SendInput(Remotely.HostCore.InputEvent.Move(20000, 20000));
            await Task.Delay(500, cts.Token);
            File.WriteAllLines(resultPath, ["PASS", .. lines]);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            Log("app logs:" + Environment.NewLine + App.ReadLogs());
            File.WriteAllLines(resultPath, ["FAIL", .. lines]);
            return 1;
        }
    }

    /// <summary>
    /// <c>Remotely.exe --selftest-shell result.txt</c>: this process plays the first instance (a test-only single-instance name, so a running Remotely is not disturbed),
    /// then starts the real Remotely.exe again with <c>--invite</c> and that name. The second copy must forward its arguments over the named pipe and exit with code 0;
    /// the first must receive exactly <c>--invite &lt;value&gt;</c>. The invite value is fake and never processed: the forwarding copy exits before reading any settings.
    /// </summary>
    public static async Task<int> RunShellAsync(string resultPath)
    {
        var lines = new List<string>();
        void Log(string s) => lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {s}");
        try
        {
            var name = "Remotely.App.selftest." + Guid.NewGuid().ToString("N");
            var invite = "RMT1-selftest-" + Guid.NewGuid().ToString("N");
            using var first = Remotely.App.Shell.SingleInstance.Acquire(name);
            if (!first.IsPrimary) throw new InvalidOperationException("Could not become the first instance under a fresh name.");
            var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Listen(a => received.TrySetResult(a));
            Log("First instance listening as " + name);

            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            using var second = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                ArgumentList = { "--invite", invite, "--instance-name", name },
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("The second copy did not start.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var args = await received.Task.WaitAsync(cts.Token);
            Log("First instance received: " + string.Join(" ", args));
            if (args.Length != 2 || args[0] != "--invite" || args[1] != invite)
                throw new InvalidOperationException("The forwarded arguments are not what the second copy was started with.");

            try { await second.WaitForExitAsync(cts.Token); }
            finally { if (!second.HasExited) second.Kill(); }
            Log("Second copy exited with code " + second.ExitCode);
            if (second.ExitCode != 0) throw new InvalidOperationException("The second copy did not exit with code 0.");
            File.WriteAllLines(resultPath, ["PASS", .. lines]);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            Log("app logs:" + Environment.NewLine + App.ReadLogs());
            File.WriteAllLines(resultPath, ["FAIL", .. lines]);
            return 1;
        }
    }
}
