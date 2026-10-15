using Remotely.Core.Lan;
using Remotely.ViewerCore;

namespace Remotely.App;

/// <summary>
/// <c>Remotely.exe --selftest result.txt</c>: shares this PC, connects to it with the displayed token, waits for a real screen frame, sends a mouse move and
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
}
