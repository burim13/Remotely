using System.Net;
using Remotely.Core.Crypto;
using Remotely.Core.Lan.Consent;

namespace Remotely.Core.Lan;

/// <summary>An open channel to a sharing PC. <see cref="Host"/> is set when the PC introduced its device key; <see cref="Remembered"/> says it will let this PC back in without a token.</summary>
public sealed record ControllerConnection(IMessageChannel Channel, string HostName, IPEndPoint Endpoint, HostAnnouncement? Host, bool Remembered, bool Resumed);

/// <summary>
/// The controller side of a same-network connection, from typing the token (or choosing a remembered PC) to a channel that is ready for a session:
/// token handshake, device proof, the wait while the person at the other PC decides, and the token-less reconnect for remembered PCs.
/// </summary>
public static class LanControllerFlow
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Longer than the other PC's own question timeout (25 s by default), so its answer, or its refusal for no answer, arrives first.</summary>
    public static readonly TimeSpan DefaultConsentWait = TimeSpan.FromSeconds(60);

    public const string DeniedMessage = "The other PC did not allow this connection. The person there chose Deny, or nobody answered in time. Nothing was shown or controlled.";

    /// <param name="self">This PC's device key. Null connects like a first-release controller: no device proof, so the other PC can only allow once.</param>
    /// <param name="userCt">The Cancel button. The connection stage has its own <paramref name="connectTimeout"/>; the wait for the other person's decision has <paramref name="consentWait"/>.</param>
    public static async Task<ControllerConnection> ConnectWithTokenAsync(string tokenText, string controllerName, string? hostAddress, DeviceIdentity? self, CancellationToken userCt,
        IProgress<LanConnectStep>? progress = null, LanDiscovery.Nearby? knownPc = null, TimeSpan? connectTimeout = null, TimeSpan? consentWait = null,
        IReadOnlyList<IPEndPoint>? extraDiscoveryTargets = null, int discoveryPort = LanNetwork.DiscoveryPort, TimeSpan? discoveryTimeout = null)
    {
        var name = self is null ? controllerName : ControllerHello.Encode(controllerName);
        LanConnector.Result result;
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(userCt))
        {
            connect.CancelAfter(connectTimeout ?? DefaultConnectTimeout);
            result = await LanConnector.ConnectAsync(tokenText, name, hostAddress, connect.Token, extraDiscoveryTargets, discoveryPort, discoveryTimeout, knownPc, progress).ConfigureAwait(false);
        }
        if (self is null) return new ControllerConnection(result.Channel, result.HostName, result.Endpoint, null, false, false);

        try
        {
            progress?.Report(LanConnectStep.WaitingForApproval);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(userCt);
            wait.CancelAfter(consentWait ?? DefaultConsentWait);
            var (host, remembered) = await DeviceProof.RespondAsync(result.Channel, self, name, wait.Token).ConfigureAwait(false);
            return new ControllerConnection(result.Channel, string.IsNullOrEmpty(result.HostName) ? host.Name : result.HostName, result.Endpoint, host, remembered, false);
        }
        catch (ConsentDeniedException)
        {
            result.Channel.Dispose();
            throw new ConsentDeniedException(DeniedMessage);
        }
        catch (OperationCanceledException) when (!userCt.IsCancellationRequested)
        {
            result.Channel.Dispose();
            throw new LanConnectException("The other PC did not answer the question in time. Ask the person there to look for the Remotely window, then try again.");
        }
        catch (Exception ex) when (ex is HandshakeException or IOException or System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            result.Channel.Dispose();
            throw new LanConnectException("The other PC closed the connection before it was approved. Try again.");
        }
        catch
        {
            result.Channel.Dispose();
            throw;
        }
    }

    /// <summary>Reconnects to a PC that remembered this one, with no token. Throws <see cref="LanConnectException"/> with a sentence for the person.</summary>
    public static async Task<ControllerConnection> ResumeAsync(RecentPc pc, DeviceIdentity self, CancellationToken userCt, TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(pc.HostDeviceId) || !IPEndPoint.TryParse(pc.Address, out var endpoint) || endpoint.Port == 0)
            throw new LanConnectException("This PC was not saved for connecting without a token. Type the token the other PC shows.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        limit.CancelAfter(timeout ?? DefaultConnectTimeout);
        var channel = await LanResume.ConnectAsync(endpoint, self, pc.HostDeviceId, limit.Token).ConfigureAwait(false);
        return new ControllerConnection(channel, pc.Name, endpoint, null, true, true);
    }
}
