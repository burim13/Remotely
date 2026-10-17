using System.Net;
using System.Net.Sockets;
using Remotely.Core.Crypto;

namespace Remotely.Core.Lan.Consent;

/// <summary>An encrypted channel over a framed TCP stream whose keys come from the mutual-authentication handshake of the server mode (<see cref="InitiatorHandshake"/> / <see cref="ResponderHandshake"/>).</summary>
internal sealed class FramedSecureChannel(FramedStream wire, SecureChannel channel) : IMessageChannel
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await wire.SendAsync(channel.Seal(plaintext.Span), ct).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        var frame = await wire.ReceiveAsync(ct).ConfigureAwait(false);
        return frame is null ? null : channel.Open(frame);
    }

    public Task CloseAsync() { wire.Dispose(); return Task.CompletedTask; }

    public void Dispose() { channel.Dispose(); wire.Dispose(); }
}

/// <summary>
/// Token-less reconnect for a remembered PC. It is the same mutually authenticated handshake the server mode uses ("RMT1": each side signs its ephemeral key with its
/// long-term P-256 key, and the controller names the exact host key it expects), run directly over TCP. The host lets the controller in only if the controller's device id is on
/// the <see cref="RememberedDevices"/> list; the controller connects only if the host answers with the key it saved when it was first allowed in. No token is involved.
/// </summary>
public static class LanResume
{
    /// <summary>The first encrypted message the controller sends after a resume handshake.</summary>
    public const byte ConfirmMessage = 0xE5;

    /// <summary>True when the first frame of a connection is the start of a resume handshake rather than a token handshake.</summary>
    public static bool IsResumeHello(ReadOnlySpan<byte> firstFrame) => firstFrame.Length >= 5 && firstFrame[..4].SequenceEqual("RMT1"u8);

    /// <summary>Host side. Throws <see cref="HandshakeException"/> when the controller is not remembered or its signature is wrong.</summary>
    internal static async Task<(IMessageChannel Channel, RememberedDevice Device)> AcceptAsync(FramedStream wire, byte[] hello1, DeviceIdentity host, RememberedDevices remembered, CancellationToken ct)
    {
        using var responder = new ResponderHandshake(host);
        var hello2 = responder.ProcessHello1(hello1, remembered.Contains, out var controllerId, out var channel);
        if (!remembered.TryGet(controllerId, out var device) || device is null) { channel.Dispose(); throw new HandshakeException("That PC is not on the remembered list."); }
        var secure = new FramedSecureChannel(wire, channel);
        try
        {
            await wire.SendAsync(hello2, ct).ConfigureAwait(false);
            // Liveness: a recorded Hello1 can be replayed by anyone on the network and would pass the signature check, but only the real controller holds the
            // ephemeral private key that makes this first encrypted message decrypt. Without it the connection is dropped before any session starts.
            var confirm = await secure.ReceiveAsync(ct).ConfigureAwait(false);
            if (confirm is not { Length: 1 } || confirm[0] != ConfirmMessage) throw new HandshakeException("The other PC did not confirm the connection.");
        }
        catch (Exception ex) when (ex is not HandshakeException && ex is System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            secure.Dispose();
            throw new HandshakeException("The other PC did not confirm the connection.");
        }
        catch { secure.Dispose(); throw; }
        return (secure, device);
    }

    /// <summary>
    /// Controller side. <paramref name="expectedHostDeviceId"/> is the host key saved from <see cref="HostAnnouncement"/>; a different PC answering is refused.
    /// Throws <see cref="LanConnectException"/> with a sentence for the person.
    /// </summary>
    public static async Task<IMessageChannel> ConnectAsync(IPEndPoint endpoint, DeviceIdentity self, string expectedHostDeviceId, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var wire = new FramedStream(client.GetStream());
            using var handshake = new InitiatorHandshake(self, expectedHostDeviceId);
            await wire.SendAsync(handshake.CreateHello1(), timeout.Token).ConfigureAwait(false);
            var hello2 = await wire.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            if (hello2 is null)
                throw new LanConnectException("That PC did not accept this PC. It may have removed it from its list, or it is not sharing right now.");
            var secure = new FramedSecureChannel(wire, handshake.ProcessHello2(hello2));
            await secure.SendAsync(new[] { ConfirmMessage }, timeout.Token).ConfigureAwait(false);
            return secure;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new LanConnectException($"Could not reach the PC at {endpoint.Address}. Is it on, sharing, and on the same network?");
        }
        catch (Exception ex) when (ex is HandshakeException or FormatException)
        {
            client.Dispose();
            throw new LanConnectException("The PC that answered is not the one saved for this entry, or it did not accept this PC.");
        }
        catch { client.Dispose(); throw; }
    }
}

/// <summary>Replays bytes that were already read from a stream (the first frame, read to decide which handshake it belongs to) before reading the rest.</summary>
internal sealed class ReplayStream(byte[] prefix, Stream inner) : Stream
{
    private int _offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_offset < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }
        return inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < prefix.Length) return new(Read(buffer.Span));
        return inner.ReadAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
