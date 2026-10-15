namespace Remotely.Core.Crypto;

/// <summary>
/// An encrypted, authenticated message pipe between a host and a viewer, whatever carries it (a relay through your server, or a direct
/// connection on the local network). The screen, input, clipboard and file code only ever sees this.
/// </summary>
public interface IMessageChannel : IDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);

    /// <summary>The next message, or null once the peer has gone.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct = default);

    Task CloseAsync();
}
