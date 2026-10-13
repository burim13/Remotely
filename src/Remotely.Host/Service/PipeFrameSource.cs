using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Remotely.HostCore;

namespace Remotely.Host.Service;

/// <summary>The service's end of the helper pipe, presented as a frame source. One request at a time; any pipe error drops the pipe so the supervisor restarts the helper.</summary>
internal sealed class PipeFrameSource : IFrameSource
{
    private const int MaxReply = 4 * 1024 * 1024;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile NamedPipeServerStream? _pipe;

    public bool IsAttached => _pipe is { IsConnected: true };

    public void Attach(NamedPipeServerStream? pipe)
    {
        var old = Interlocked.Exchange(ref _pipe, pipe);
        old?.Dispose();
    }

    public async Task<FrameInfo> CaptureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipe = _pipe ?? throw new InvalidOperationException("The screen helper is not running yet.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReplyTimeout);
            try
            {
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                var header = new byte[5];
                await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
                if (length > MaxReply) throw new InvalidDataException("Helper reply too large.");
                var payload = new byte[length];
                await pipe.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                return header[0] == 0
                    ? FrameCodec.Decode(payload)
                    : throw new InvalidOperationException(Encoding.UTF8.GetString(payload));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or FormatException
                                           || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                Attach(null); // out of step with the helper: drop it, the supervisor starts a fresh one
                throw new InvalidOperationException("The screen helper stopped responding; restarting it.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
