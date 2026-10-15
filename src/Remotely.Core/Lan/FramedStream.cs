using System.Buffers.Binary;
using Remotely.Core.Protocol;

namespace Remotely.Core.Lan;

/// <summary>Messages over a TCP stream: each is a 4-byte big-endian length followed by that many bytes.</summary>
public sealed class FramedStream(Stream stream) : IDisposable
{
    public const int MaxFrame = ProtocolInfo.MaxRelayBytes + 64;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrame) throw new InvalidDataException("Message too large.");
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)payload.Length);
        payload.CopyTo(buffer.AsMemory(4));
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>The next message, or null if the other side closed the connection cleanly between messages.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(header, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxFrame) throw new InvalidDataException("Message too large.");
        var payload = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    private async Task<bool> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) { if (read == 0) return false; throw new EndOfStreamException(); }
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        stream.Dispose();
        _sendGate.Dispose();
    }
}
