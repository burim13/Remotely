using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Remotely.Core.Crypto;

/// <summary>
/// One direction pair of AES-256-GCM keys with strictly increasing counters. Frame = counter(8, big-endian) || ciphertext || tag(16).
/// It assumes an ordered, reliable transport (WebSocket). A datagram transport will need a replay window instead.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const int Overhead = 8 + TagLength;
    private const int TagLength = 16;
    private static readonly byte[] Aad = "RMT1-data"u8.ToArray();

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly object _sendLock = new();
    private readonly object _receiveLock = new();
    private ulong _sendCounter;
    private ulong _receiveCounter;

    internal SecureChannel(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey)
    {
        _send = new AesGcm(sendKey, TagLength);
        _receive = new AesGcm(receiveKey, TagLength);
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var frame = new byte[Overhead + plaintext.Length];
        Span<byte> nonce = stackalloc byte[12];
        lock (_sendLock)
        {
            if (_sendCounter == ulong.MaxValue) throw new InvalidOperationException("Counter exhausted; rekey required.");
            var counter = _sendCounter++;
            BinaryPrimitives.WriteUInt64BigEndian(frame, counter);
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
            _send.Encrypt(nonce, plaintext, frame.AsSpan(8, plaintext.Length), frame.AsSpan(8 + plaintext.Length, TagLength), Aad);
        }
        return frame;
    }

    /// <exception cref="CryptographicException">Tampered, replayed, reordered or wrong-key frame.</exception>
    public byte[] Open(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Overhead) throw new CryptographicException("Frame too short.");
        var length = frame.Length - Overhead;
        var plaintext = new byte[length];
        Span<byte> nonce = stackalloc byte[12];
        lock (_receiveLock)
        {
            var counter = BinaryPrimitives.ReadUInt64BigEndian(frame);
            if (counter != _receiveCounter) throw new CryptographicException("Unexpected counter (replay, drop or reorder).");
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
            _receive.Decrypt(nonce, frame.Slice(8, length), frame.Slice(8 + length, TagLength), plaintext, Aad);
            _receiveCounter++;
        }
        return plaintext;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }
}
