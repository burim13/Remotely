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
public sealed class ViewerSession : IAsyncDisposable
{
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan UnchangedBackoff = TimeSpan.FromMilliseconds(60);

    private readonly SecureSession _secure;
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

    private ViewerSession(SecureSession secure, HelloInfo hello, string hostDeviceId)
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
            var first = await secure.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new HandshakeException("Host closed the session.");
            var hello = HelloInfo.Decode(first) ?? throw new HandshakeException("Host did not introduce itself.");
            if (hello.Version != HostMessages.ProtocolVersion) throw new HandshakeException("Host speaks a different protocol version.");
            var session = new ViewerSession(secure, hello, hostDeviceId);
            session._receiveLoop = Task.Run(session.ReceiveLoopAsync);
            session._frameLoop = Task.Run(session.FrameLoopAsync);
            session._inputLoop = Task.Run(session.InputLoopAsync);
            return session;
        }
        catch
        {
            secure?.Dispose();
            await relay.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ---- input ----

    /// <summary>Queues an event. Mouse moves are coalesced so a fast mouse cannot flood the link; everything else keeps its order.</summary>
    public void SendInput(InputEvent e)
    {
        if (!CanControl) return;
        lock (_inputGate)
        {
            if (e.Kind == InputKind.MouseMove && _pendingInput.Count > 0 && _pendingInput[^1].Kind == InputKind.MouseMove)
                _pendingInput[^1] = e;
            else if (_pendingInput.Count < 4096)
                _pendingInput.Add(e);
        }
        _inputSignal.Release();
    }

    public void ReleaseAllInput() => SendInput(new InputEvent(InputKind.ReleaseAll));

    private async Task InputLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _inputSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                while (true)
                {
                    List<InputEvent> batch;
                    lock (_inputGate)
                    {
                        if (_pendingInput.Count == 0) break;
                        var take = Math.Min(_pendingInput.Count, InputCodec.MaxEventsPerMessage);
                        batch = _pendingInput.GetRange(0, take);
                        _pendingInput.RemoveRange(0, take);
                    }
                    await _secure.SendAsync(InputCodec.Encode(batch), _cts.Token).ConfigureAwait(false);
                    await Task.Delay(8, _cts.Token).ConfigureAwait(false); // ~120 batches/s ceiling
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { _ = EndAsync(ex.Message); }
    }

    public Task SendSecureAttentionAsync() =>
        CanSendSecureAttention ? _secure.SendAsync(new[] { HostMessages.SecureAttention }, _cts.Token) : Task.CompletedTask;

    // ---- clipboard ----

    public Task SendClipboardAsync(string text)
    {
        if (!HasClipboard || text.Length > HostMessages.MaxClipboardChars) return Task.CompletedTask;
        return _secure.SendAsync(HostMessages.Prefixed(HostMessages.Clipboard, Encoding.UTF8.GetBytes(text)), _cts.Token);
    }

    // ---- files ----

    /// <summary>Uploads a file into the host's receive folder. Returns the name it was saved under.</summary>
    public async Task<string> UploadFileAsync(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!HasFiles) throw new InvalidOperationException("The host does not accept files.");
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > HostMessages.MaxFileBytes) throw new InvalidOperationException("That file is too large to send.");
        var hash = await SHA256.HashDataAsync(stream, link.Token).ConfigureAwait(false);
        stream.Position = 0;

        var id = Interlocked.Increment(ref _nextId);
        var ack = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acks[id] = ack;
        try
        {
            var name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            var header = new byte[8 + 32 + 2 + name.Length];
            BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)stream.Length);
            hash.CopyTo(header, 8);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(40), (ushort)name.Length);
            name.CopyTo(header, 42);
            await _secure.SendAsync(HostMessages.WithId(HostMessages.FileStart, id, header), link.Token).ConfigureAwait(false);

            var buffer = new byte[HostMessages.FileChunkBytes];
            long sent = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, link.Token).ConfigureAwait(false)) > 0)
            {
                if (ack.Task.IsCompleted) break; // host refused or failed: stop sending
                await _secure.SendAsync(HostMessages.WithId(HostMessages.FileChunk, id, buffer.AsSpan(0, read)), link.Token).ConfigureAwait(false);
                sent += read;
                progress?.Report(stream.Length == 0 ? 1 : (double)sent / stream.Length);
            }
            if (!ack.Task.IsCompleted)
                await _secure.SendAsync(HostMessages.WithId(HostMessages.FileEnd, id, []), link.Token).ConfigureAwait(false);
            return await ack.Task.WaitAsync(TimeSpan.FromSeconds(30), link.Token).ConfigureAwait(false);
        }
        finally
        {
            _acks.TryRemove(id, out _);
        }
    }

    public async Task<ShareEntry[]> ListSharedFilesAsync(CancellationToken ct = default)
    {
        if (!HasFiles) return [];
        var waiter = new TaskCompletionSource<ShareEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listWaiter = waiter;
        await _secure.SendAsync(new[] { HostMessages.ShareList }, ct).ConfigureAwait(false);
        return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a file from the host's shared folder into <see cref="DownloadDirectory"/>. Returns the saved path.</summary>
    public async Task<string> DownloadFileAsync(string name, CancellationToken ct = default)
    {
        if (!HasFiles) throw new InvalidOperationException("The host does not share files.");
        var id = Interlocked.Increment(ref _nextId);
        var download = new Download();
        _downloads[id] = download;
        try
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var payload = new byte[2 + nameBytes.Length];
            BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)nameBytes.Length);
            nameBytes.CopyTo(payload, 2);
            await _secure.SendAsync(HostMessages.WithId(HostMessages.ShareGet, id, payload), ct).ConfigureAwait(false);
            download.RequestedName = name;
            return await download.Done.Task.WaitAsync(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        }
        finally
        {
            _downloads.TryRemove(id, out var d);
            d?.Sink?.Dispose();
        }
    }

    private sealed class Download
    {
        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileSink? Sink { get; set; }
        public string RequestedName { get; set; } = "";
    }

    // ---- loops ----

    private async Task FrameLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                var waiter = _frameWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _secure.SendAsync(new[] { HostMessages.FrameRequest }, _cts.Token).ConfigureAwait(false);
                var changed = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                var wait = (changed ? MinFrameInterval : UnchangedBackoff) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (TimeoutException) { _ = EndAsync("The host stopped responding."); }
        catch (Exception ex) { _ = EndAsync(ex.Message); }
    }

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
                        try { FrameReceived?.Invoke(FrameCodec.Decode(message.AsSpan(1))); }
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
