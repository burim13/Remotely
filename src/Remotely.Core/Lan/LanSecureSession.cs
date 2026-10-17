using System.Security.Cryptography;
using System.Text;
using Remotely.Core.Crypto;

namespace Remotely.Core.Lan;

public sealed class LanAuthException(string message) : Exception(message);

/// <summary>An encrypted session over a direct TCP connection on the local network. After <see cref="InitiateAsync"/> / <see cref="AcceptAsync"/> it behaves like a relayed session.</summary>
public sealed class LanSecureSession : IMessageChannel
{
    private static readonly byte[] ProofPrefix = "RMTL1-proof:"u8.ToArray();
    private static readonly byte[] AcceptedMessage = "RMTL1-accepted"u8.ToArray();
    private readonly FramedStream _wire;
    private readonly SecureChannel _channel;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private LanSecureSession(FramedStream wire, SecureChannel channel)
    {
        _wire = wire;
        _channel = channel;
    }

    /// <summary>Controller side. Sends the proof of the token (and this PC's name) as its first encrypted message.</summary>
    public static async Task<LanSecureSession> InitiateAsync(FramedStream wire, LanToken token, string controllerName, CancellationToken ct)
    {
        using var handshake = new LanInitiatorHandshake(token);
        await wire.SendAsync(handshake.CreateHello1(), ct).ConfigureAwait(false);
        var hello2 = await wire.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new HandshakeException("The other PC closed the connection.");
        var session = new LanSecureSession(wire, handshake.ProcessHello2(hello2));
        await session.SendAsync(ProofPrefix.Concat(Encoding.UTF8.GetBytes(controllerName.Length > 60 ? controllerName[..60] : controllerName)).ToArray(), ct).ConfigureAwait(false);
        // The host answers only if the token was right; otherwise it just closes the connection.
        byte[]? answer;
        try { answer = await session.ReceiveAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is CryptographicException or IOException or EndOfStreamException) { answer = null; }
        if (answer is null || !answer.AsSpan().SequenceEqual(AcceptedMessage))
        {
            session.Dispose();
            throw new LanAuthException("The other PC did not accept the token.");
        }
        return session;
    }

    /// <summary>
    /// Host side. Only after the controller's proof decrypts (so it knew the token) does this return; a wrong token throws <see cref="LanAuthException"/>
    /// without the host having sent anything an eavesdropper could test guesses against.
    /// </summary>
    public static async Task<(LanSecureSession Session, string ControllerName)> AcceptAsync(FramedStream wire, LanTokenState state, CancellationToken ct)
    {
        var hello1 = await wire.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new HandshakeException("The connection closed before it started.");
        var hello2 = new LanResponderHandshake(state).ProcessHello1(hello1, out var channel);
        if (!state.TryBeginAttempt())
        {
            channel.Dispose();
            throw new LanAuthException("This token has been tried too often.");
        }
        await wire.SendAsync(hello2, ct).ConfigureAwait(false);
        var session = new LanSecureSession(wire, channel);
        try
        {
            var proof = await session.ReceiveAsync(ct).ConfigureAwait(false);
            if (proof is null || proof.Length < ProofPrefix.Length || !proof.AsSpan(0, ProofPrefix.Length).SequenceEqual(ProofPrefix))
                throw new LanAuthException("Wrong token.");
            await session.SendAsync(AcceptedMessage, ct).ConfigureAwait(false);
            return (session, Encoding.UTF8.GetString(proof.AsSpan(ProofPrefix.Length)));
        }
        catch (CryptographicException) { session.Dispose(); throw new LanAuthException("Wrong token."); }
        catch { session.Dispose(); throw; }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _wire.SendAsync(_channel.Seal(plaintext.Span), ct).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        var frame = await _wire.ReceiveAsync(ct).ConfigureAwait(false);
        return frame is null ? null : _channel.Open(frame);
    }

    public Task CloseAsync()
    {
        _wire.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel.Dispose();
        _wire.Dispose();
    }
}
