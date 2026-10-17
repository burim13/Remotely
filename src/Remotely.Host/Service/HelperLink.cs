using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Remotely.Host.Helper;
using Remotely.HostCore;

namespace Remotely.Host.Service;

/// <summary>One request/reply pipe to the helper. One call at a time; any pipe error drops the pipe so the supervisor starts a fresh helper.</summary>
internal sealed class HelperPipe
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile NamedPipeServerStream? _pipe;

    public bool IsConnected => _pipe is { IsConnected: true };

    public void Attach(NamedPipeServerStream? pipe) => Interlocked.Exchange(ref _pipe, pipe)?.Dispose();

    public async Task<byte[]> CallAsync(byte kind, byte[] payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipe = _pipe ?? throw new InvalidOperationException("The screen helper is not running yet.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReplyTimeout);
            try
            {
                await pipe.WriteAsync(HelperRpc.Frame(kind, payload), timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                var header = new byte[5];
                await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
                if (length > HelperRpc.MaxPayload) throw new InvalidDataException("Helper reply too large.");
                var reply = new byte[length];
                await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
                return header[0] == 0 ? reply : throw new InvalidOperationException(Encoding.UTF8.GetString(reply));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException
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

/// <summary>The service's view of the helper: screen, input and clipboard, each forwarded over a pipe to the process running in the user's session.</summary>
internal sealed class HelperLink : ITiledFrameSource, IInputSink, IClipboardSync
{
    public HelperPipe Main { get; } = new();
    public HelperPipe Input { get; } = new();

    public bool IsAttached => Main.IsConnected && Input.IsConnected;

    public void Detach()
    {
        Main.Attach(null);
        Input.Attach(null);
    }

    public async Task<FrameInfo> CaptureAsync(CancellationToken ct) =>
        FrameCodec.Decode(await Main.CallAsync(HelperRpc.Capture, [], ct).ConfigureAwait(false));

    public async Task<TileCapture> CaptureTilesAsync(TileRequest request, CancellationToken ct) =>
        FrameWire.DecodeCapture(await Main.CallAsync(HelperRpc.Capture, FrameWire.EncodeRequest(request), ct).ConfigureAwait(false));

    public async Task InjectAsync(IReadOnlyList<InputEvent> events, CancellationToken ct) =>
        await Input.CallAsync(HelperRpc.Input, InputCodec.Encode(events), ct).ConfigureAwait(false);

    public async Task<(long Sequence, string? Text)> ReadAsync(CancellationToken ct)
    {
        var reply = await Main.CallAsync(HelperRpc.ClipboardRead, [], ct).ConfigureAwait(false);
        if (reply.Length < 9) throw new InvalidOperationException("Bad clipboard reply.");
        return (BinaryPrimitives.ReadInt64BigEndian(reply), reply[8] == 1 ? Encoding.UTF8.GetString(reply, 9, reply.Length - 9) : null);
    }

    public async Task<long> WriteAsync(string text, CancellationToken ct)
    {
        var reply = await Main.CallAsync(HelperRpc.ClipboardWrite, Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        return reply.Length >= 8 ? BinaryPrimitives.ReadInt64BigEndian(reply) : 0;
    }
}
