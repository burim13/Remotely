using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Remotely.Core.NetworkSetup;

public enum ReachabilityOutcome
{
    /// <summary>Connected, and (with TLS and a pin given) the server presented the expected key.</summary>
    Reachable,
    /// <summary>Connected and spoke TLS, but the key is not the expected one: another program, or a different certificate than the one the wizard shows.</summary>
    WrongServer,
    /// <summary>The machine answered and refused: nothing listens on that port, or a firewall rejects it.</summary>
    Refused,
    /// <summary>No answer at all. Typical for a firewall that drops packets, or an address that is not on this network.</summary>
    TimedOut,
    NameNotFound,
    /// <summary>Connected, but what answered did not complete a TLS handshake.</summary>
    NotTls,
    Failed,
}

public sealed record ReachabilityResult(ReachabilityOutcome Outcome, string Host, int Port, string? PresentedPin = null, CertificateSummary? Certificate = null);

public interface IReachabilityProbe
{
    /// <summary>
    /// Connects to host:port from this PC. With <paramref name="tls"/> it also completes the TLS handshake, reports the certificate the server presented and,
    /// when <paramref name="expectedPinHex"/> is given, compares its key pin.
    /// </summary>
    Task<ReachabilityResult> ProbeAsync(string host, int port, bool tls, string? expectedPinHex, TimeSpan timeout, CancellationToken ct = default);
}

public sealed class TlsReachabilityProbe : IReachabilityProbe
{
    public async Task<ReachabilityResult> ProbeAsync(string host, int port, bool tls, string? expectedPinHex, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            if (!tls) return new(ReachabilityOutcome.Reachable, host, port);

            CertificateSummary? summary = null;
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, cert, _, _) =>
            {
                if (cert is not null)
                {
                    using var x509 = new X509Certificate2(cert);
                    summary = CertificateInspector.Describe(x509);
                }
                return true;   // the pin comparison below is the trust decision, as in the clients
            });
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { return new(ReachabilityOutcome.NotTls, host, port); }
            var presented = summary?.PinHex;
            return string.IsNullOrEmpty(expectedPinHex) || string.Equals(presented, expectedPinHex, StringComparison.OrdinalIgnoreCase)
                ? new(ReachabilityOutcome.Reachable, host, port, presented, summary)
                : new(ReachabilityOutcome.WrongServer, host, port, presented, summary);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(ReachabilityOutcome.TimedOut, host, port); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return new(ReachabilityOutcome.Refused, host, port); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData) { return new(ReachabilityOutcome.NameNotFound, host, port); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { return new(ReachabilityOutcome.TimedOut, host, port); }
        catch (SocketException) { return new(ReachabilityOutcome.Failed, host, port); }
    }
}
