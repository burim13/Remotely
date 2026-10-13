using System.Security.Cryptography;
using Remotely.Core.Crypto;

namespace Remotely.Client;

/// <summary>An end-to-end encrypted, mutually authenticated session layered over a <see cref="RelaySession"/>.</summary>
public sealed class SecureSession : IDisposable
{
    private readonly RelaySession _relay;
    private readonly SecureChannel _channel;

    private SecureSession(RelaySession relay, SecureChannel channel, string peerDeviceId)
    {
        _relay = relay;
        _channel = channel;
        PeerDeviceId = peerDeviceId;
    }

    public string PeerDeviceId { get; }

    /// <summary>Viewer side: proves its identity and verifies the host is the device it asked for.</summary>
    public static async Task<SecureSession> InitiateAsync(RelaySession relay, DeviceIdentity self, CancellationToken ct = default)
    {
        using var handshake = new InitiatorHandshake(self, relay.PeerDeviceId);
        await relay.SendAsync(handshake.CreateHello1(), ct).ConfigureAwait(false);
        var hello2 = await relay.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new HandshakeException("Session ended during handshake.");
        return new SecureSession(relay, handshake.ProcessHello2(hello2), relay.PeerDeviceId);
    }

    /// <summary>Host side: <paramref name="isTrusted"/> decides whether this viewer may connect (and is where an approval prompt hooks in).</summary>
    public static async Task<SecureSession> AcceptAsync(RelaySession relay, DeviceIdentity self, Func<string, bool> isTrusted, CancellationToken ct = default)
    {
        var hello1 = await relay.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new HandshakeException("Session ended during handshake.");
        using var handshake = new ResponderHandshake(self);
        var hello2 = handshake.ProcessHello1(hello1, isTrusted, out var initiatorId, out var channel);
        if (initiatorId != relay.PeerDeviceId) throw new HandshakeException("Initiator identity does not match the session.");
        await relay.SendAsync(hello2, ct).ConfigureAwait(false);
        return new SecureSession(relay, channel, initiatorId);
    }

    public Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
        _relay.SendAsync(_channel.Seal(plaintext.Span), ct);

    /// <summary>Next decrypted message, or null if the session ended. Throws <see cref="CryptographicException"/> if the relay tampers with traffic.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        var frame = await _relay.ReceiveAsync(ct).ConfigureAwait(false);
        return frame is null ? null : _channel.Open(frame);
    }

    public Task CloseAsync() => _relay.CloseAsync();

    public void Dispose() => _channel.Dispose();
}
