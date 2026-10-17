using Remotely.Core.Lan;
using Remotely.Unattended;
using Remotely.ViewerCore;

namespace Remotely.App.Views.Unattended;

/// <summary>
/// <c>Remotely.exe --selftest-service result.txt [Desktop]</c> (the flag is wired in the app's start-up code, see docs/unattended.md): with the service running and sharing through the
/// service allowed, asks the service over the pipe to listen, takes the token it made, connects with that token over the PC's own network address, waits for a real frame and
/// reports which Windows desktop it came from. When <c>Desktop</c> is given (for example <c>Winlogon</c> while the PC is locked) the frame must come from that desktop.
/// Writes PASS or FAIL plus details to the file. Any question the service relays is answered "allow once" without a window.
/// </summary>
internal static class UnattendedSelfTest
{
    public static async Task<int> RunAsync(string resultPath, string? expectedDesktop)
    {
        var lines = new List<string>();
        void Log(string s) { lock (lines) lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {s}"); }
        var controller = UnattendedController.Instance;
        try
        {
            controller.QuestionOverride = q =>
            {
                Log($"The service asked about {q.ControllerName}; answering allow once.");
                return Task.FromResult(PipeDecisions.AllowOnce);
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            await controller.RefreshAsync();
            var view = controller.View;
            Log($"Service: {view.Service}; pipe problem: {view.Link?.ToString() ?? "none"}");
            if (view.Service != ServiceState.Running) throw new InvalidOperationException("The service is not running.");
            if (view.Status is not { } status) throw new InvalidOperationException("The pipe did not answer: " + view.Link);
            Log($"Sharing through the service allowed: {status.LanAllowed}; ask before connecting: {status.AskBeforeConnect}; helper attached: {status.HelperAttached}");

            if (await controller.StartListeningAsync() is { } error) throw new InvalidOperationException("The service would not start listening: " + error);
            await controller.RefreshAsync();
            status = controller.View.Status ?? throw new InvalidOperationException("No status after start.");
            if (status.Token is not { } token || status.Port == 0) throw new InvalidOperationException("The service is not showing a token.");
            Log($"Listening on port {status.Port}.");

            var address = LanNetwork.LocalAddresses().FirstOrDefault()?.ToString() ?? throw new InvalidOperationException("No network address on this PC.");
            // The service asks its question while the connection is being made; the controller's loop answers it, so keep looking at the service meanwhile.
            using var watch = new CancellationTokenSource();
            var poller = Task.Run(async () =>
            {
                while (!watch.IsCancellationRequested)
                {
                    await controller.RefreshAsync();
                    try { await Task.Delay(300, watch.Token); } catch (OperationCanceledException) { }
                }
            });
            try
            {
                var found = await LanConnector.ConnectAsync(token, "selftest-service", $"{address}:{status.Port}", cts.Token);
                Log($"Connected to {found.Endpoint}");
                await using var session = await ViewerSession.OpenAsync(found.Channel, "selftest-service", cts.Token);
                var gotFrame = new TaskCompletionSource<(int W, int H, string Desktop)>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.FrameReceived += f => gotFrame.TrySetResult((f.Width, f.Height, f.Desktop));
                if (session.LatestFrame is { } early) gotFrame.TrySetResult((early.Width, early.Height, early.Desktop));
                var frame = await gotFrame.Task.WaitAsync(cts.Token);
                Log($"Frame {frame.W}x{frame.H} from the {frame.Desktop} desktop.");
                if (expectedDesktop is not null && !string.Equals(frame.Desktop, expectedDesktop, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Expected the {expectedDesktop} desktop but the frame came from {frame.Desktop}.");
                await controller.RefreshAsync();
                var inSession = controller.View.Status?.Controller;
                Log($"The service reports the connected PC as: {inSession ?? "nobody"}; capture desktop: {controller.View.Status?.CaptureDesktop ?? "none"}; awake request held: {controller.View.Status?.AwakeHeld}");
                if (inSession is null) throw new InvalidOperationException("The service does not report the session.");
            }
            finally
            {
                watch.Cancel();
                await poller;
            }

            await controller.StopListeningAsync();
            File.WriteAllLines(resultPath, ["PASS", .. lines]);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            try { await controller.StopListeningAsync(); } catch (Exception) { /* the service may be the problem */ }
            File.WriteAllLines(resultPath, ["FAIL", .. lines]);
            return 1;
        }
        finally { controller.QuestionOverride = null; }
    }
}
