using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.HostCore;

namespace Remotely.ViewerCore;

public sealed partial class ViewerSession
{
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

    // ---- resumable transfers (HostCapabilities.ResumableFiles) ----

    private TransferClient? _transferClient;
    private ITransferLink? _transferLink;
    private readonly object _transferGate = new();

    /// <summary>True when the host can resume interrupted transfers and move whole folders.</summary>
    public bool HasResumableFiles => Hello.Capabilities.HasFlag(HostCapabilities.ResumableFiles);

    /// <summary>Limits applied to files this viewer receives (downloads). Set before the first download.</summary>
    public FileTransferOptions TransferOptions { get; set; } = new();

    /// <summary>
    /// The way to move files in this session: the resumable protocol when the host offers it, otherwise the original one-file-at-a-time flow (no folders, no resume).
    /// Give it to a <see cref="TransferQueue"/> with <see cref="TransferQueue.Attach"/>. Becomes unusable when the session ends.
    /// </summary>
    public ITransferLink FileLink
    {
        get
        {
            lock (_transferGate)
            {
                if (_transferLink is not null) return _transferLink;
                if (HasResumableFiles)
                {
                    var client = new TransferClient((data, ct) => _secure.SendAsync(data, ct), HostDeviceId, TransferOptions, () => DownloadDirectory);
                    _transferClient = client;
                    Closed += reason => client.Fail(reason ?? "The session ended.");
                    if (Volatile.Read(ref _disposed) != 0) client.Fail("The session ended.");
                    _transferLink = client;
                }
                else _transferLink = new LegacyLink(this);
                return _transferLink;
            }
        }
    }

    /// <summary>Called by the receive loop for every message with an id in 0xC0-0xCF.</summary>
    private void HandleTransferMessage(byte[] message) => _transferClient?.Handle(message);

    private sealed class LegacyLink(ViewerSession session) : ITransferLink
    {
        public bool Resumable => false;
        public bool Available => session.HasFiles && Volatile.Read(ref session._disposed) == 0;

        public async Task UploadAsync(TransferItem item, CancellationToken ct)
        {
            if (item.IsFolder) throw new TransferException(TransferError.Unsupported, "The other PC runs an older version of Remotely that cannot receive folders. Update it first.");
            try
            {
                var info = new FileInfo(item.LocalPath!);
                if (!info.Exists) throw new TransferException(TransferError.NotFound, "The file was not found.");
                item.Begin(info.Length, 0);
                var saved = await session.UploadFileAsync(item.LocalPath!, new Progress<double>(p => item.Progress((long)(p * info.Length))), ct).ConfigureAwait(false);
                item.Finish(saved, null);
            }
            catch (Exception ex) when (ex is not (TransferException or OperationCanceledException)) { throw Map(ex, session); }
        }

        public async Task DownloadAsync(TransferItem item, CancellationToken ct)
        {
            if (item.IsFolder) throw new TransferException(TransferError.Unsupported, "The other PC runs an older version of Remotely that cannot send folders. Update it first.");
            try
            {
                item.Begin(item.TotalBytes, 0);
                var path = await session.DownloadFileAsync(item.Name, ct).ConfigureAwait(false);
                item.Finish(Path.GetFileName(path), path);
            }
            catch (Exception ex) when (ex is not (TransferException or OperationCanceledException)) { throw Map(ex, session); }
        }

        public async Task<ShareEntryEx[]> ListAsync(CancellationToken ct) =>
            (await session.ListSharedFilesAsync(ct).ConfigureAwait(false)).Select(e => new ShareEntryEx(e.Name, e.Size, false)).ToArray();

        private static TransferException Map(Exception ex, ViewerSession session) =>
            Volatile.Read(ref session._disposed) != 0 || ex is TimeoutException
                ? new TransferException(TransferError.LinkLost, "The connection to the other PC was lost.", ex)
                : TransferException.From(ex);
    }
}

/// <summary>One side of a viewer's file transfers: what <see cref="TransferQueue"/> runs items on.</summary>
public interface ITransferLink
{
    /// <summary>True for the resumable protocol (partial files are kept and continued).</summary>
    bool Resumable { get; }
    /// <summary>False once the underlying session has ended.</summary>
    bool Available { get; }
    Task UploadAsync(TransferItem item, CancellationToken ct);
    Task DownloadAsync(TransferItem item, CancellationToken ct);
    Task<ShareEntryEx[]> ListAsync(CancellationToken ct);
}

public enum TransferDirection { Upload, Download }

public enum TransferState { Queued, Starting, Running, Interrupted, Completed, Failed, Cancelled }

/// <summary>One file or folder in the queue, with everything a screen shows. Properties are safe to read from any thread; <see cref="Changed"/> is raised on a background thread (progress at most ten times a second).</summary>
public sealed class TransferItem
{
    private static int _counter;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastSampleMs, _lastSampleBytes, _lastRaiseMs;
    private double _speed;

    public TransferItem(TransferDirection direction, string name, string? localPath, bool isFolder, long totalBytes)
    {
        Number = Interlocked.Increment(ref _counter);
        Direction = direction; Name = name; LocalPath = localPath; IsFolder = isFolder; TotalBytes = totalBytes;
    }

    public int Number { get; }
    public TransferDirection Direction { get; }
    /// <summary>File or folder name as the user knows it. For a download this is the name in the other PC's share folder.</summary>
    public string Name { get; }
    /// <summary>Upload: the source on this PC. Download: where the result was saved once finished.</summary>
    public string? LocalPath { get; internal set; }
    public bool IsFolder { get; }
    /// <summary>Total bytes, or -1 when not known yet. For a folder this is the uncompressed size, so progress is approximate.</summary>
    public long TotalBytes { get; private set; }
    public long BytesDone { get; private set; }
    /// <summary>Where this attempt started (non-zero when an interrupted transfer was resumed).</summary>
    public long ResumedFrom { get; private set; }
    public TransferState State { get; private set; } = TransferState.Queued;
    public TransferError Error { get; private set; }
    public string Message { get; private set; } = "";
    /// <summary>The name the file or folder was saved under (it can differ from <see cref="Name"/> when that name was taken).</summary>
    public string? SavedName { get; private set; }
    public int Attempts { get; private set; }
    /// <summary>Files of a folder that could not be read and were skipped.</summary>
    public int SkippedFiles { get; internal set; }
    internal string? PartPath { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
    internal bool CancelRequested { get; set; }

    public double BytesPerSecond => _speed;
    public double Fraction => State == TransferState.Completed ? 1 : TotalBytes > 0 ? Math.Min(0.99, (double)BytesDone / TotalBytes) : 0;
    public bool IsFinished => State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;

    /// <summary>Estimated time left, or null when the speed or size is not known.</summary>
    public TimeSpan? Remaining =>
        State == TransferState.Running && _speed > 1 && TotalBytes > BytesDone ? TimeSpan.FromSeconds((TotalBytes - BytesDone) / _speed) : null;

    public event Action<TransferItem>? Changed;

    public void Begin(long total, long resumedFrom)
    {
        lock (_gate)
        {
            TotalBytes = total; ResumedFrom = resumedFrom; BytesDone = resumedFrom; _speed = 0;
            _lastSampleMs = _clock.ElapsedMilliseconds; _lastSampleBytes = resumedFrom;
            State = TransferState.Running; Error = TransferError.None; Message = "";
        }
        Raise(force: true);
    }

    internal void Starting()
    {
        lock (_gate) { State = TransferState.Starting; Attempts++; Error = TransferError.None; Message = ""; BytesDone = 0; ResumedFrom = 0; _speed = 0; }
        Raise(force: true);
    }

    public void Progress(long done)
    {
        lock (_gate)
        {
            BytesDone = done;
            var now = _clock.ElapsedMilliseconds;
            if (now - _lastSampleMs >= 400)
            {
                var instant = (done - _lastSampleBytes) * 1000.0 / (now - _lastSampleMs);
                _speed = _speed <= 0 ? instant : _speed * 0.6 + instant * 0.4;
                _lastSampleMs = now; _lastSampleBytes = done;
            }
        }
        Raise(force: false);
    }

    public void Finish(string savedName, string? savedPath)
    {
        lock (_gate)
        {
            State = TransferState.Completed; SavedName = savedName; BytesDone = TotalBytes >= 0 ? Math.Max(BytesDone, TotalBytes) : BytesDone; _speed = 0;
            if (savedPath is not null) LocalPath = savedPath;
        }
        Raise(force: true);
    }

    public void Fail(TransferState state, TransferError error, string message)
    {
        lock (_gate) { State = state; Error = error; Message = message; _speed = 0; }
        Raise(force: true);
    }

    internal void Requeue()
    {
        lock (_gate) { State = TransferState.Queued; Error = TransferError.None; Message = ""; _speed = 0; CancelRequested = false; }
        Raise(force: true);
    }

    private void Raise(bool force)
    {
        var now = _clock.ElapsedMilliseconds;
        lock (_gate)
        {
            if (!force && now - _lastRaiseMs < 100) return;
            _lastRaiseMs = now;
        }
        Changed?.Invoke(this);
    }
}

/// <summary>Formatting helpers for screens (no language-specific words).</summary>
public static class TransferFormat
{
    public static string Size(long bytes) => TransferReceiver.FormatBytes(Math.Max(0, bytes));
    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "" : TransferReceiver.FormatBytes((long)bytesPerSecond) + "/s";
    public static string Clock(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
}

/// <summary>
/// Runs uploads and downloads for one viewer, two at a time. Items survive the session: when the link drops, running items become Interrupted, and when a new link is attached
/// they continue where the receiver says it stopped (for files; a folder starts again).
/// </summary>
public sealed class TransferQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly List<TransferItem> _items = [];
    private readonly int _maxParallel;
    private ITransferLink? _link;
    private int _running;
    private bool _disposed;

    public TransferQueue(int maxParallel = 2) { _maxParallel = Math.Max(1, maxParallel); }

    /// <summary>Resume interrupted items by themselves when a new link is attached. Default true.</summary>
    public bool AutoResume { get; set; } = true;

    public event Action<TransferItem>? ItemAdded;
    public event Action<TransferItem>? ItemChanged;
    public event Action<TransferItem>? ItemRemoved;

    public IReadOnlyList<TransferItem> Items { get { lock (_gate) return [.. _items]; } }
    public bool HasLink { get { lock (_gate) return _link is { Available: true }; } }
    public ITransferLink? Link { get { lock (_gate) return _link; } }

    /// <summary>Uses this link for all items from now on (null = no connection; queued items wait). Interrupted items are queued again when <see cref="AutoResume"/> is on.</summary>
    public void Attach(ITransferLink? link)
    {
        List<TransferItem> toResume = [];
        lock (_gate)
        {
            _link = link;
            if (link is not null && AutoResume) toResume = _items.Where(i => i.State == TransferState.Interrupted).ToList();
        }
        foreach (var item in toResume) item.Requeue();
        Schedule();
    }

    /// <summary>Adds a file or a folder (decided by what the path is) to the upload list.</summary>
    public TransferItem EnqueueUpload(string path)
    {
        var folder = Directory.Exists(path);
        var name = folder ? new DirectoryInfo(Path.GetFullPath(path)).Name : Path.GetFileName(path);
        long size = -1;
        if (!folder) { try { size = new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        return Add(new TransferItem(TransferDirection.Upload, name, path, folder, size));
    }

    /// <summary>Adds a file or folder of the other PC's share folder to the download list.</summary>
    public TransferItem EnqueueDownload(string name, bool isFolder, long size = -1) =>
        Add(new TransferItem(TransferDirection.Download, name, null, isFolder, size));

    private TransferItem Add(TransferItem item)
    {
        item.Changed += i => ItemChanged?.Invoke(i);
        lock (_gate) _items.Add(item);
        ItemAdded?.Invoke(item);
        Schedule();
        return item;
    }

    /// <summary>Stops a running item (the receiver deletes its partial file) or drops a waiting one.</summary>
    public void Cancel(TransferItem item)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (item.IsFinished) return;
            item.CancelRequested = true;
            cts = item.Cts;
        }
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } return; }
        if (item.Direction == TransferDirection.Download && item.PartPath is { } part) { try { File.Delete(part); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        item.Fail(TransferState.Cancelled, TransferError.Cancelled, "");
    }

    /// <summary>Tries a failed, cancelled or interrupted item again. A file continues where the receiver stopped.</summary>
    public void Retry(TransferItem item)
    {
        lock (_gate) { if (item.State is not (TransferState.Failed or TransferState.Cancelled or TransferState.Interrupted)) return; }
        item.Requeue();
        Schedule();
    }

    public void Remove(TransferItem item)
    {
        Cancel(item);
        bool removed;
        lock (_gate) removed = _items.Remove(item);
        if (removed) ItemRemoved?.Invoke(item);
    }

    public void ClearFinished()
    {
        List<TransferItem> done;
        lock (_gate)
        {
            done = _items.Where(i => i.State is TransferState.Completed or TransferState.Cancelled).ToList();
            foreach (var i in done) _items.Remove(i);
        }
        foreach (var i in done) ItemRemoved?.Invoke(i);
    }

    private void Schedule()
    {
        while (true)
        {
            TransferItem? next;
            ITransferLink link;
            lock (_gate)
            {
                if (_disposed || _link is not { Available: true } current || _running >= _maxParallel) return;
                next = _items.FirstOrDefault(i => i.State == TransferState.Queued && i.Cts is null);
                if (next is null) return;
                link = current;
                _running++;
                next.Cts = new CancellationTokenSource();
            }
            next.Starting();
            _ = Task.Run(() => RunAsync(next, link));
        }
    }

    private async Task RunAsync(TransferItem item, ITransferLink link)
    {
        var cts = item.Cts!;
        try
        {
            if (item.CancelRequested) cts.Cancel();
            if (item.Direction == TransferDirection.Upload) await link.UploadAsync(item, cts.Token).ConfigureAwait(false);
            else await link.DownloadAsync(item, cts.Token).ConfigureAwait(false);
            if (!item.IsFinished) item.Finish(item.Name, item.LocalPath);
        }
        catch (OperationCanceledException) when (item.CancelRequested)
        {
            item.Fail(TransferState.Cancelled, TransferError.Cancelled, "");
        }
        catch (Exception ex)
        {
            var failure = TransferException.From(ex);
            if (failure.Code == TransferError.LinkLost && !item.CancelRequested)
                item.Fail(TransferState.Interrupted, TransferError.LinkLost, failure.Message);
            else item.Fail(TransferState.Failed, failure.Code, failure.Message);
        }
        finally
        {
            lock (_gate) { _running--; item.Cts = null; }
            cts.Dispose();
            Schedule();
        }
    }

    public void Dispose()
    {
        List<CancellationTokenSource> running;
        lock (_gate)
        {
            _disposed = true;
            running = _items.Select(i => i.Cts).Where(c => c is not null).Select(c => c!).ToList();
        }
        foreach (var c in running) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
    }
}

/// <summary>
/// The viewer's end of the resumable file protocol, independent of the transport (it is given a way to send, and is fed the messages that arrive).
/// Uploads send a file or a zipped folder; downloads receive one. Interrupted file transfers keep their partial file on the receiving side.
/// </summary>
public sealed class TransferClient : ITransferLink
{
    private static readonly TimeSpan AnswerWait = TimeSpan.FromMinutes(3);   // the owner of the other PC may be asked first
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(90);
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _send;
    private readonly string _hostLabel;
    private readonly FileTransferOptions _options;
    private readonly Func<string> _downloadDirectory;
    private readonly ConcurrentDictionary<uint, Pending> _pending = new();
    private TaskCompletionSource<ShareEntryEx[]>? _listWaiter;
    private uint _nextId;
    private string? _failed;

    private sealed class Pending(TransferItem item)
    {
        public TransferItem Item { get; } = item;
        public TaskCompletionSource<long> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TransferWire.Announce> Begun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TransferReceiver? Receiver { get; set; }
        public long LastActivity { get; set; } = Environment.TickCount64;
    }

    public TransferClient(Func<ReadOnlyMemory<byte>, CancellationToken, Task> send, string hostLabel, FileTransferOptions options, Func<string> downloadDirectory)
    {
        _send = send; _hostLabel = hostLabel; _options = options; _downloadDirectory = downloadDirectory;
    }

    public bool Resumable => true;
    public bool Available => _failed is null;

    /// <summary>The link is gone: everything waiting ends as "connection lost", and the queue marks those items Interrupted.</summary>
    public void Fail(string reason)
    {
        _failed = reason;
        var error = new TransferException(TransferError.LinkLost, "The connection to the other PC was lost.");
        foreach (var p in _pending.Values)
        {
            p.Accepted.TrySetException(error); p.Begun.TrySetException(error); p.Done.TrySetException(error);
        }
        _listWaiter?.TrySetException(error);
    }

    private async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (_failed is not null) throw new TransferException(TransferError.LinkLost, "The connection to the other PC was lost.");
        try { await _send(message, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (TransferException) { throw; }
        catch (Exception ex) { throw new TransferException(TransferError.LinkLost, "The connection to the other PC was lost.", ex); }
    }

    private async Task TrySendAsync(ReadOnlyMemory<byte> message)
    {
        try { await _send(message, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* best effort: the link may already be gone */ }
    }

    private Pending Register(TransferItem item, out uint id)
    {
        id = Interlocked.Increment(ref _nextId);
        var p = new Pending(item);
        _pending[id] = p;
        if (_failed is not null) p.Done.TrySetException(new TransferException(TransferError.LinkLost, "The connection to the other PC was lost."));
        return p;
    }

    // ---- messages from the host ----

    /// <summary>Feeds one message received from the host (kinds 0xC0-0xCF).</summary>
    public void Handle(byte[] message)
    {
        if (message.Length == 0 || !HostMessages.TryReadId(message, out var id, out _)) return;
        var body = message.AsMemory(5);
        if (message[0] == TransferMessages.EntriesReply)
        {
            try { _listWaiter?.TrySetResult(JsonSerializer.Deserialize<ShareEntryEx[]>(message.AsSpan(1)) ?? []); }
            catch (JsonException) { _listWaiter?.TrySetResult([]); }
            return;
        }
        if (!_pending.TryGetValue(id, out var p)) return;
        p.LastActivity = Environment.TickCount64;
        switch (message[0])
        {
            case TransferMessages.Accept:
                if (TransferWire.TryReadOffset(body.Span, out var offset)) p.Accepted.TrySetResult(offset);
                else p.Accepted.TrySetException(new TransferException(TransferError.Protocol, "Bad answer from the other PC."));
                break;
            case TransferMessages.Result:
                if (!TransferWire.TryReadResult(body.Span, out var code, out var text)) { code = TransferError.Protocol; text = "Bad answer from the other PC."; }
                if (code == TransferError.None) p.Done.TrySetResult(text);
                else
                {
                    var error = new TransferException(code, text);
                    p.Accepted.TrySetException(error); p.Begun.TrySetException(error); p.Done.TrySetException(error);
                }
                break;
            case TransferMessages.Begin:
                if (TransferWire.TryReadAnnounce(body.Span, out var announce)) p.Begun.TrySetResult(announce);
                else p.Begun.TrySetException(new TransferException(TransferError.Protocol, "Bad answer from the other PC."));
                break;
            case TransferMessages.DataToViewer:
                ReceiveData(id, p, body);
                break;
            case TransferMessages.EndToViewer:
                ReceiveEnd(id, p, body.ToArray());
                break;
        }
    }

    private void ReceiveData(uint id, Pending p, ReadOnlyMemory<byte> body)
    {
        if (p.Receiver is not { } receiver || p.Done.Task.IsCompleted) return;
        try
        {
            if (!TransferWire.TryReadData(body.Span, out var offset, out var data)) throw new TransferException(TransferError.Protocol, "Bad data from the other PC.");
            receiver.Write(offset, data);
            p.Item.Progress(receiver.Offset);
        }
        catch (Exception ex)
        {
            var failure = TransferException.From(ex);
            if (failure.Code == TransferError.DiskFull) receiver.Dispose(); else receiver.Discard();
            p.Done.TrySetException(failure);
            _ = TrySendAsync(HostMessages.WithId(TransferMessages.Cancel, id, []));
        }
    }

    private void ReceiveEnd(uint id, Pending p, byte[] body)
    {
        if (p.Receiver is not { } receiver || p.Done.Task.IsCompleted) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!TransferWire.TryReadEnd(body, out var total, out var sha)) throw new TransferException(TransferError.Protocol, "Bad data from the other PC.");
                p.Done.TrySetResult(receiver.Complete(total, sha));
            }
            catch (Exception ex) { p.Done.TrySetException(TransferException.From(ex)); }
        });
    }

    // ---- uploads ----

    public async Task UploadAsync(TransferItem item, CancellationToken ct)
    {
        var path = item.LocalPath!;
        var source = await Task.Run(() => item.IsFolder ? TransferSource.ForFolder(path) : TransferSource.ForFile(path), ct).ConfigureAwait(false);
        var p = Register(item, out var id);
        try
        {
            await SendAsync(TransferWire.EncodeAnnounce(TransferMessages.Offer, id, source.Announce), ct).ConfigureAwait(false);
            var offset = await p.Accepted.Task.WaitAsync(AnswerWait, ct).ConfigureAwait(false);
            if (offset > source.Size && !source.IsFolder) offset = 0;
            item.Begin(source.Size, source.IsFolder ? 0 : offset);
            var (total, hash) = await TransferPump.RunAsync(source, TransferMessages.Data, id, source.IsFolder ? 0 : offset,
                async (data, t) =>
                {
                    if (p.Done.Task.IsFaulted) await p.Done.Task.ConfigureAwait(false);   // the other PC refused or failed: stop sending
                    await SendAsync(data, t).ConfigureAwait(false);
                    p.LastActivity = Environment.TickCount64;
                },
                position => item.Progress(position), ct).ConfigureAwait(false);
            await SendAsync(TransferWire.EncodeEnd(TransferMessages.End, id, total, hash), ct).ConfigureAwait(false);
            var saved = await AwaitAsync(p, ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            item.SkippedFiles = source.SkippedFiles;
            item.Finish(saved, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TrySendAsync(HostMessages.WithId(TransferMessages.Cancel, id, [])).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException ex) { throw new TransferException(TransferError.LinkLost, "The other PC did not answer.", ex); }
        finally { _pending.TryRemove(id, out _); }
    }

    // ---- downloads ----

    public async Task DownloadAsync(TransferItem item, CancellationToken ct)
    {
        var p = Register(item, out var id);
        try
        {
            await SendAsync(TransferWire.EncodePull(id, item.Name), ct).ConfigureAwait(false);
            var announce = await p.Begun.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            TransferReceiver receiver;
            try { receiver = TransferReceiver.Open(_downloadDirectory(), _hostLabel, announce, _options); }
            catch (Exception) { await TrySendAsync(HostMessages.WithId(TransferMessages.Cancel, id, [])).ConfigureAwait(false); throw; }
            p.Receiver = receiver;
            item.PartPath = receiver.PartPath;
            item.Begin(announce.Size, receiver.Offset);
            try
            {
                await SendAsync(TransferWire.EncodeOffset(TransferMessages.Start, id, receiver.Offset), ct).ConfigureAwait(false);
                var stored = await AwaitAsync(p, ct, null).ConfigureAwait(false);
                item.Finish(stored, Path.Combine(_downloadDirectory(), stored));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                receiver.Discard();   // cancelled by the user: nothing is kept
                await TrySendAsync(HostMessages.WithId(TransferMessages.Cancel, id, [])).ConfigureAwait(false);
                throw;
            }
            finally { receiver.Dispose(); }   // a lost link keeps the partial file for the next attempt
        }
        catch (TimeoutException ex) { throw new TransferException(TransferError.LinkLost, "The other PC did not answer.", ex); }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Waits for the final answer; gives up when nothing has been heard from the other PC for a while.</summary>
    private static async Task<string> AwaitAsync(Pending p, CancellationToken ct, TimeSpan? overall)
    {
        var started = Environment.TickCount64;
        while (!p.Done.Task.IsCompleted)
        {
            await Task.WhenAny(p.Done.Task, Task.Delay(1000, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            if (now - p.LastActivity > IdleLimit.TotalMilliseconds) throw new TransferException(TransferError.LinkLost, "Nothing was heard from the other PC for a while.");
            if (overall is { } limit && now - started > limit.TotalMilliseconds) throw new TransferException(TransferError.LinkLost, "The other PC did not answer.");
        }
        return await p.Done.Task.ConfigureAwait(false);
    }

    public async Task<ShareEntryEx[]> ListAsync(CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<ShareEntryEx[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listWaiter = waiter;
        await SendAsync(HostMessages.WithId(TransferMessages.ListEntries, 0, []), ct).ConfigureAwait(false);
        return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }
}
