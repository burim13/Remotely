using System.Threading.Channels;

namespace Remotely.Client;

/// <summary>One relayed byte pipe to a peer device. The coordinator can see sizes and timing but, once <see cref="SecureSession"/> is layered on top, not content.</summary>
public sealed class RelaySession
{
    private readonly Channel<byte[]> _inbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task> _send;
    private readonly Func<Guid, Task> _close;

    internal RelaySession(Guid id, string peerDeviceId, bool isInitiator,
        Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task> send, Func<Guid, Task> close)
    {
        Id = id;
        PeerDeviceId = peerDeviceId;
        IsInitiator = isInitiator;
        _send = send;
        _close = close;
    }

    public Guid Id { get; }
    public string PeerDeviceId { get; }
    public bool IsInitiator { get; }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) => _send(Id, payload, ct);

    /// <summary>Next payload from the peer, or null once the session has ended.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        try { return await _inbox.Reader.ReadAsync(ct).ConfigureAwait(false); }
        catch (ChannelClosedException) { return null; }
    }

    public Task CloseAsync() => _close(Id);

    internal ValueTask DeliverAsync(byte[] payload, CancellationToken ct) => _inbox.Writer.WriteAsync(payload, ct);
    internal void Complete() => _inbox.Writer.TryComplete();
}
