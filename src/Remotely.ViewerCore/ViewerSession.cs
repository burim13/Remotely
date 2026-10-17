using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.HostCore;

namespace Remotely.ViewerCore;

/// <summary>
/// A viewer's live session with one host: pulls frames as fast as the host can produce them, sends input, syncs the clipboard and
/// moves files. UI-agnostic: a WPF window (or a test) subscribes to the events. Events are raised on background threads.
/// </summary>
public sealed partial class ViewerSession : IAsyncDisposable
{
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan UnchangedBackoff = TimeSpan.FromMilliseconds(60);

    private readonly IMessageChannel _secure;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _inputGate = new();
    private readonly List<InputEvent> _pendingInput = [];
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<string>> _acks = new();
    private readonly ConcurrentDictionary<uint, Download> _downloads = new();
    private TaskCompletionSource<bool> _frameWaiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<ShareEntry[]>? _listWaiter;
    private Task _receiveLoop = Task.CompletedTask;
    private Task _frameLoop = Task.CompletedTask;
    private Task _inputLoop = Task.CompletedTask;
    private readonly SemaphoreSlim _inputSignal = new(0);
    private uint _nextId;
    private int _disposed;

    private ViewerSession(IMessageChannel secure, HelloInfo hello, string hostDeviceId)
    {
        _secure = secure;
        Hello = hello;
        HostDeviceId = hostDeviceId;
    }

    public string HostDeviceId { get; }
    public HelloInfo Hello { get; }
    public bool CanControl => Hello.Capabilities.HasFlag(HostCapabilities.Control);
    public bool CanSendSecureAttention => Hello.Capabilities.HasFlag(HostCapabilities.SecureAttention);
    public bool HasClipboard => Hello.Capabilities.HasFlag(HostCapabilities.Clipboard);
    public bool HasFiles => Hello.Capabilities.HasFlag(HostCapabilities.Files);

    /// <summary>Where downloaded files are saved.</summary>
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public event Action<FrameInfo>? FrameReceived;

    /// <summary>The newest full picture received. A window that subscribes to <see cref="FrameReceived"/> after the session opened (the first frame can arrive before it does) reads this to get the picture it missed.</summary>
    public FrameInfo? LatestFrame { get; private set; }

    private void RaiseFrame(FrameInfo frame)
    {
        if (frame.Tiles is null) LatestFrame = frame;
        FrameReceived?.Invoke(frame);
    }
    public event Action<string>? ClipboardReceived;
    public event Action<string>? HostError;
    /// <summary>Raised once when the session ends; the argument is a reason, or null for a normal close.</summary>
    public event Action<string?>? Closed;

    public static async Task<ViewerSession> OpenAsync(CoordinatorClient client, DeviceIdentity identity, string hostDeviceId, CancellationToken ct = default)
    {
        var relay = await client.ConnectToAsync(hostDeviceId, ct).ConfigureAwait(false);
        SecureSession? secure = null;
        try
        {
            secure = await SecureSession.InitiateAsync(relay, identity, ct).ConfigureAwait(false);
            return await StartAsync(secure, hostDeviceId, ct).ConfigureAwait(false);
        }
        catch
        {
            secure?.Dispose();
            await relay.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a session over a channel that is already authenticated (for example a direct connection on the local network). Takes ownership of the channel.</summary>
    public static async Task<ViewerSession> OpenAsync(IMessageChannel channel, string hostLabel, CancellationToken ct = default)
    {
        try { return await StartAsync(channel, hostLabel, ct).ConfigureAwait(false); }
        catch
        {
            try { await channel.CloseAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            channel.Dispose();
            throw;
        }
    }

    private static async Task<ViewerSession> StartAsync(IMessageChannel secure, string hostLabel, CancellationToken ct)
    {
        var first = await secure.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new HandshakeException("Host closed the session.");
        var hello = HelloInfo.Decode(first) ?? throw new HandshakeException("Host did not introduce itself.");
        if (hello.Version is < 1 or > HostMessages.ProtocolVersion) throw new HandshakeException("Host speaks a different protocol version.");
        var session = new ViewerSession(secure, hello, hostLabel);
        session._receiveLoop = Task.Run(session.ReceiveLoopAsync);
        session._frameLoop = Task.Run(session.FrameLoopAsync);
        session._inputLoop = Task.Run(session.InputLoopAsync);
        return session;
    }

    // ---- loops ----


    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (await _secure.ReceiveAsync(_cts.Token).ConfigureAwait(false) is { } message)
            {
                if (message.Length == 0) continue;
                switch (message[0])
                {
                    case HostMessages.FrameReply:
                        try { RaiseFrame(FrameCodec.Decode(message.AsSpan(1))); }
                        catch (FormatException ex) { HostError?.Invoke("Bad frame: " + ex.Message); }
                        _frameWaiter.TrySetResult(true);
                        break;
                    case HostMessages.Unchanged:
                        _frameWaiter.TrySetResult(false);
                        break;
                    case HostMessages.ClipboardReply:
                        ClipboardReceived?.Invoke(Encoding.UTF8.GetString(message, 1, message.Length - 1));
                        break;
                    case HostMessages.Error:
                        HostError?.Invoke(Encoding.UTF8.GetString(message, 1, message.Length - 1));
                        _frameWaiter.TrySetResult(false); // an error also answers a frame request
                        break;
                    case HostMessages.FileAck:
                        HandleAck(message);
                        break;
                    case HostMessages.ShareListReply:
                        try
                        {
                            var entries = JsonSerializer.Deserialize<ShareEntry[]>(message.AsSpan(1)) ?? [];
                            _listWaiter?.TrySetResult(entries);
                        }
                        catch (JsonException) { _listWaiter?.TrySetResult([]); }
                        break;
                    case HostMessages.ShareFileStart or HostMessages.ShareFileChunk or HostMessages.ShareFileEnd:
                        HandleDownload(message);
                        break;
                    case >= 0xA0 and <= 0xAF:
                        HandleFrameMessage(message);
                        break;
                    case >= 0xB0 and <= 0xBF:
                        await HandleFidelityAsync(message).ConfigureAwait(false);
                        break;
                    case >= TransferMessages.FirstHostToViewer and <= TransferMessages.LastHostToViewer:
                        HandleTransferMessage(message);
                        break;
                }
            }
            await EndAsync(null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (CryptographicException ex) { await EndAsync("Secure connection was tampered with: " + ex.Message).ConfigureAwait(false); }
        catch (Exception ex) { await EndAsync(ex.Message).ConfigureAwait(false); }
    }

    private void HandleAck(byte[] message)
    {
        if (!HostMessages.TryReadId(message, out var id, out var rest) || rest.Length < 3) return;
        var ok = rest[0] == 1;
        var length = BinaryPrimitives.ReadUInt16BigEndian(rest[1..]);
        var text = rest.Length >= 3 + length ? Encoding.UTF8.GetString(rest.Slice(3, length)) : "";
        if (_acks.TryGetValue(id, out var ack))
        {
            if (ok) ack.TrySetResult(text); else ack.TrySetException(new IOException(text));
        }
        else if (_downloads.TryGetValue(id, out var download) && !ok)
        {
            download.Done.TrySetException(new IOException(text));
        }
    }

    private void HandleDownload(byte[] message)
    {
        if (!HostMessages.TryReadId(message, out var id, out var rest) || !_downloads.TryGetValue(id, out var d)) return;
        try
        {
            switch (message[0])
            {
                case HostMessages.ShareFileStart when rest.Length == 8 + 32:
                    d.Sink?.Dispose();
                    d.Sink = FileSink.Create(DownloadDirectory, d.RequestedName, (long)BinaryPrimitives.ReadUInt64BigEndian(rest), rest.Slice(8, 32).ToArray());
                    break;
                case HostMessages.ShareFileChunk when d.Sink is not null:
                    d.Sink.Write(rest);
                    break;
                case HostMessages.ShareFileEnd when d.Sink is not null:
                    d.Done.TrySetResult(d.Sink.Complete());
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            d.Done.TrySetException(new IOException(ex.Message));
        }
    }

    private async Task EndAsync(string? reason)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var a in _acks.Values) a.TrySetException(new IOException(reason ?? "Session ended."));
        foreach (var d in _downloads.Values) d.Done.TrySetException(new IOException(reason ?? "Session ended."));
        _frameWaiter.TrySetResult(false);
        _listWaiter?.TrySetException(new IOException(reason ?? "Session ended."));
        try { await _secure.CloseAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _secure.Dispose();
        Closed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        await EndAsync(null).ConfigureAwait(false);
        try { await Task.WhenAll(_receiveLoop, _frameLoop, _inputLoop).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
