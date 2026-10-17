using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Remotely.Core.NetworkSetup;
using Remotely.Core.ServerManager;
using NetworkProcessRunner = Remotely.Core.NetworkSetup.ProcessRunner;

namespace Remotely.ServerManager.Setup;

/// <summary>
/// <see cref="IElevatedActions"/> for the Manager window, which never runs as administrator. Each change that needs administrator rights starts the elevated copy
/// (<see cref="ElevatedHelper"/>, one Windows permission prompt) with a fixed verb; that copy runs <see cref="SetupHelperMain"/>.
/// Reading the firewall rule needs no elevation and runs here.
/// </summary>
internal sealed partial class ElevatedSetupActions : IElevatedActions
{
    private static readonly TimeSpan PipeWait = TimeSpan.FromMinutes(2);

    public Task<FirewallRuleState> GetFirewallRuleAsync(int port, CancellationToken ct = default) =>
        App.Args.Fake
            ? Task.FromResult(FirewallRuleState.Present)
            : new NetshElevatedActions(new NetworkProcessRunner(), ManagerPaths.DataDir).GetFirewallRuleAsync(port, ct);

    public async Task<ActionResult> AddFirewallRuleAsync(int port, CancellationToken ct = default) =>
        ToResult(await ElevatedHelper.RunAsync(ServiceVerb.FirewallAdd, port.ToString(CultureInfo.InvariantCulture)), "Setup_Err_FirewallFailed");

    public async Task<ActionResult> RemoveFirewallRuleAsync(CancellationToken ct = default) =>
        ToResult(await ElevatedHelper.RunAsync(ServiceVerb.FirewallRemove), "Setup_Err_FirewallFailed");

    public async Task<ActionResult> RenewCertificateAsync(TimeSpan validity, CancellationToken ct = default) =>
        ToResult(await ElevatedHelper.RunAsync(ServiceVerb.CertRenew), "Setup_Pfx_CannotWrite");   // the helper renews for ten years; the span is fixed there

    /// <summary>The file is read here (as the signed-in user) and sent with its password through a private pipe, so neither appears on a command line and the elevated side opens no path.</summary>
    public async Task<ActionResult> InstallCertificateAsync(string pfxPath, string? password, CancellationToken ct = default)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(pfxPath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ActionResult.Failure("Setup_Pfx_CannotRead", ex.Message); }
        if (App.Args.Fake) return ActionResult.Success();

        var pipeName = "remotely-setup-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        var helper = ElevatedHelper.RunAsync(ServiceVerb.CertInstall, pipeName);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(PipeWait);
        var connect = server.WaitForConnectionAsync(wait.Token);
        var first = await Task.WhenAny(helper, connect);
        if (first == helper)
        {
            // The helper ended without connecting: the permission prompt was declined, or it failed before reading.
            wait.Cancel();
            try { await connect; } catch (OperationCanceledException) { }
            return ToResult(await helper, "Setup_Pfx_CannotWrite");
        }
        try
        {
            await connect;
            PfxTransfer.Write(server, bytes, password);
            await server.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return ActionResult.Failure("Setup_Pfx_CannotWrite", ex.Message);
        }
        return ToResult(await helper, "Setup_Pfx_CannotWrite");
    }

    [GeneratedRegex(@"Setup_[A-Za-z_]+")]
    private static partial Regex ErrorKeyIn();

    private static ActionResult ToResult(HelperResult result, string fallbackKey)
    {
        if (result.Cancelled) return ActionResult.Failure("Setup_Err_Cancelled");
        if (result.Ok) return ActionResult.Success(PinIn(result.Log));
        var key = ErrorKeyIn().Matches(result.Log).Select(m => m.Value).LastOrDefault() ?? fallbackKey;
        return ActionResult.Failure(key, result.Log);
    }

    private static readonly Regex PinLine = new(@"PIN ([0-9a-f]{64})", RegexOptions.Compiled);

    private static string? PinIn(string log) => PinLine.Match(log) is { Success: true } m ? m.Groups[1].Value : null;
}

/// <summary>Runs inside the elevated copy for the setup verbs. Writes "Setup_..." error keys and "PIN ..." into the helper log, which the Manager reads.</summary>
internal static class SetupHelperMain
{
    public static bool Handles(ServiceVerb verb) => verb is ServiceVerb.FirewallAdd or ServiceVerb.FirewallRemove or ServiceVerb.CertInstall or ServiceVerb.CertRenew;

    public static async Task<int> RunAsync(HelperRequest request, Action<string> log)
    {
        var actions = new NetshElevatedActions(new NetworkProcessRunner(), ManagerPaths.DataDir);
        log(request.Verb.ToString());
        ActionResult result;
        switch (request.Verb)
        {
            case ServiceVerb.FirewallAdd:
                if (!int.TryParse(request.Argument, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) { log("Setup_Err_PortInvalid"); return HelperExit.BadUsage; }
                result = await actions.AddFirewallRuleAsync(port);
                break;
            case ServiceVerb.FirewallRemove:
                result = await actions.RemoveFirewallRuleAsync();
                break;
            case ServiceVerb.CertRenew:
                result = await actions.RenewCertificateAsync(TimeSpan.FromDays(3650));
                break;
            case ServiceVerb.CertInstall:
                if (string.IsNullOrEmpty(request.Argument) || !Regex.IsMatch(request.Argument, "^remotely-setup-[0-9a-f]{32}$")) { log("Bad pipe name."); return HelperExit.BadUsage; }
                try
                {
                    using var client = new NamedPipeClientStream(".", request.Argument, PipeDirection.In);
                    await client.ConnectAsync(15000);
                    if (PfxTransfer.Read(client) is not { } received) { log("Setup_Pfx_NotAPfx"); return HelperExit.BadUsage; }
                    result = await actions.InstallCertificateBytesAsync(received.Pfx, received.Password);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception)
                {
                    log("Setup_Pfx_CannotRead " + ex.Message);
                    return HelperExit.StepFailed;
                }
                break;
            default:
                return HelperExit.BadUsage;
        }
        if (result.Ok)
        {
            if (result.Detail is { Length: 64 } pin) log("PIN " + pin);
            return HelperExit.Ok;
        }
        log($"{result.ErrorKey} {result.Detail}".Trim());
        return HelperExit.StepFailed;
    }
}
