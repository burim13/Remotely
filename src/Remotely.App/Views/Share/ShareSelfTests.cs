using Microsoft.UI.Dispatching;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.ViewerCore;

namespace Remotely.App.Views.Share;

/// <summary>
/// The host-side checks of "Share this PC", run on a real Windows machine against the real screen and input code:
/// a refused PC gets no session; "Always allow" lets the same device key reconnect without a token and a different key is refused; removing it from the list ends that;
/// the banner window opens when a session starts and closes when it ends; "Pause control" is reflected in the state.
/// Called from <c>SelfTest</c> (or on its own with <see cref="RunAsync(string)"/>, which writes PASS or FAIL like the other self-tests). Must run on the UI thread so the banner can be created.
/// </summary>
internal static class ShareSelfTests
{
    /// <summary><c>Remotely.exe --selftest-share result.txt</c> once the shell calls this.</summary>
    public static async Task<int> RunAsync(string resultPath)
    {
        var lines = new List<string>();
        void Log(string s) { lock (lines) lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {s}"); }
        try
        {
            await RunCoreAsync(Log, DispatcherQueue.GetForCurrentThread());
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

    public static async Task RunCoreAsync(Action<string> log, DispatcherQueue? ui)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "remotely-share-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var share = new ShareService(dataDirectory);
            if (ui is not null) share.AttachUi(ui);
            var error = share.Start(log);
            if (error is not null) throw new InvalidOperationException(error);
            var host = share.Host!;
            var address = LanNetwork.LocalAddresses().FirstOrDefault()?.ToString() ?? throw new InvalidOperationException("No network address on this PC.");
            var endpoint = $"{address}:{host.TcpPort}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var controller = DeviceIdentity.Generate();
            const string Name = "selftest-pc";
            Require(share.State.Current.State == ShareState.Ready, "state after Start is Ready");

            // 1. Denied: the other PC is told no, gets no session, and the refusal is in the history.
            share.PromptOverride = (_, _) => Task.FromResult(ConsentDecision.Deny);
            var denied = await LanConnector.ConnectAsync(host.CurrentToken.Display, ControllerHello.Encode(Name), endpoint, cts.Token);
            try
            {
                await DeviceProof.RespondAsync(denied.Channel, controller, ControllerHello.Encode(Name), cts.Token);
                throw new InvalidOperationException("A denied PC was told it may connect.");
            }
            catch (ConsentDeniedException) { log("Denied PC was told no."); }
            var refused = false;
            try { await (await ViewerSession.OpenAsync(denied.Channel, "selftest", cts.Token)).DisposeAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { refused = true; }
            Require(refused, "a denied PC could not open a session");
            await WaitAsync(() => share.State.Current.State == ShareState.Ready && share.Coordinator.History.All.Count == 1, "back to Ready after a denial");
            Require(share.Coordinator.History.All[0].Outcome == nameof(ConsentOutcome.Denied), "the denial is in the history");
            Require(ui is null || !share.Ui!.BannerOpen, "no banner after a denial");

            // 2. Always allow: a session opens, the banner appears, pause is reflected, the device is remembered.
            share.PromptOverride = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
            var allowed = await LanConnector.ConnectAsync(host.CurrentToken.Display, ControllerHello.Encode(Name), endpoint, cts.Token);
            var (hostKey, remembered) = await DeviceProof.RespondAsync(allowed.Channel, controller, ControllerHello.Encode(Name), cts.Token);
            Require(remembered, "the host said the PC was remembered");
            Require(hostKey.DeviceId == share.Identity.DeviceId, "the host proved the device key it will use later");
            await using (var session = await ViewerSession.OpenAsync(allowed.Channel, "selftest", cts.Token))
            {
                var frame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.FrameReceived += _ => frame.TrySetResult(true);
                await frame.Task.WaitAsync(cts.Token);
                log("Frame received in the allowed session.");
                await WaitAsync(() => share.State.Current.State == ShareState.InSession, "state InSession");
                if (ui is not null)
                {
                    await WaitAsync(() => share.Ui!.BannerOpen, "the banner window opens during a session");
                    log("Banner window is open.");
                }
                await share.Coordinator.SetControlPausedAsync(true, "paused by selftest");
                Require(share.State.Current.ControlPaused && host.ControlPaused, "pause control is reflected");
                await share.Coordinator.SetControlPausedAsync(false, "");
                Require(!host.ControlPaused, "resume control is reflected");
            }
            await WaitAsync(() => share.State.Current.State == ShareState.Ready, "Ready after the session ends");
            if (ui is not null) await WaitAsync(() => !share.Ui!.BannerOpen, "the banner closes when the session ends");
            Require(share.Coordinator.Remembered.Contains(controller.DeviceId), "the PC is on the remembered list");

            // 3. The remembered PC returns without a token; another key is refused; removing it ends access.
            var endpointIp = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(address), host.TcpPort);
            await using (var again = await ViewerSession.OpenAsync(await LanResume.ConnectAsync(endpointIp, controller, share.Identity.DeviceId, cts.Token), "selftest", cts.Token))
            {
                var frame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                again.FrameReceived += _ => frame.TrySetResult(true);
                await frame.Task.WaitAsync(cts.Token);
                log("Remembered PC reconnected without a token and received a frame.");
            }
            await WaitAsync(() => !host.IsConnected, "host free after the second session");
            using (var stranger = DeviceIdentity.Generate())
            {
                try { await LanResume.ConnectAsync(endpointIp, stranger, share.Identity.DeviceId, cts.Token); throw new InvalidOperationException("A PC with another key reconnected."); }
                catch (LanConnectException) { log("A different key was refused."); }
            }
            Require(share.Coordinator.Remembered.Remove(controller.DeviceId), "remove from the list");
            try { await LanResume.ConnectAsync(endpointIp, controller, share.Identity.DeviceId, cts.Token); throw new InvalidOperationException("A removed PC reconnected."); }
            catch (LanConnectException) { log("A removed PC was refused."); }

            await WaitAsync(() => share.Coordinator.History.All.Count == 3 && share.Coordinator.History.All.All(r => r.EndedUtc is not null), "history has the refusal and both sessions, all finished");
            var rows = share.Coordinator.History.All;
            Require(rows.Any(r => r.Via == "remembered"), "the token-free session is in the history");
            log("History: " + string.Join("; ", rows.Select(r => $"{r.ControllerName} {r.Via} {r.Outcome} {r.EndReason}")));
        }
        finally
        {
            try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + what);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(50);
        }
    }
}
