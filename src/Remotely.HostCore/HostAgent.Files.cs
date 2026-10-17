using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;

namespace Remotely.HostCore;

public sealed partial class HostAgent
{
    /// <summary>Policy for received files: size and quota limits, the confirmation hook, folder unpacking. The defaults accept everything within generous limits.</summary>
    public FileTransferOptions FileOptions { get; init; } = new();

    private readonly ConditionalWeakTable<Session, HostTransferEndpoint> _endpoints = new();

    private async Task HandleUploadAsync(Session s, byte[] message, CancellationToken ct)
    {
        if (!HostMessages.TryReadId(message, out var id, out var rest)) { await ErrorAsync(s, "Bad file message.", ct).ConfigureAwait(false); return; }
        switch (message[0])
        {
            case HostMessages.FileStart:
                s.Upload?.Dispose();
                s.Upload = null;
                try
                {
                    if (rest.Length < 8 + 32 + 2) throw new InvalidDataException("Bad file header.");
                    var size = (long)BinaryPrimitives.ReadUInt64BigEndian(rest);
                    var hash = rest.Slice(8, 32).ToArray();
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(rest[40..]);
                    if (rest.Length != 42 + nameLength) throw new InvalidDataException("Bad file header.");
                    var requested = Encoding.UTF8.GetString(rest.Slice(42, nameLength));
                    var sink = FileSink.Create(services.ReceiveDirectory!, requested, size, hash);
                    var refusal = await LegacyPreflightAsync(s, requested, size, ct).ConfigureAwait(false);
                    if (refusal is not null)
                    {
                        sink.Dispose();
                        await AckAsync(s, id, false, refusal, ct).ConfigureAwait(false);
                        break;
                    }
                    s.Upload = sink;
                    s.UploadId = id;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    await AckAsync(s, id, false, TransferException.From(ex).Message, ct).ConfigureAwait(false);
                }
                break;
            case HostMessages.FileChunk when s.Upload is not null && s.UploadId == id:
                try { s.Upload.Write(rest); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    s.Upload.Dispose(); s.Upload = null;
                    await AckAsync(s, id, false, TransferException.From(ex).Message, ct).ConfigureAwait(false);
                }
                break;
            case HostMessages.FileEnd when s.Upload is not null && s.UploadId == id:
                string? stored = null, failure = null;
                try { stored = Path.GetFileName(s.Upload.Complete()); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { failure = TransferException.From(ex).Message; }
                // Release (and, on failure, delete) the partial file BEFORE telling the viewer, so "rejected" always means "nothing left behind".
                s.Upload.Dispose(); s.Upload = null;
                if (failure is null)
                {
                    log($"Received file '{stored}' from {DeviceIds.Short(s.ViewerId)}.");
                    await AckAsync(s, id, true, stored!, ct).ConfigureAwait(false);
                }
                else await AckAsync(s, id, false, failure, ct).ConfigureAwait(false);
                break;
        }
    }

    private void StartDownload(Session s, byte[] message, CancellationToken ct)
    {
        if (s.Download is { IsCompleted: false }) { _ = ErrorAsync(s, "A download is already running.", ct); return; }
        s.Download = Task.Run(async () =>
        {
            uint id = 0;
            try
            {
                if (!HostMessages.TryReadId(message, out id, out var rest) || rest.Length < 2) throw new InvalidDataException("Bad request.");
                var nameLength = BinaryPrimitives.ReadUInt16BigEndian(rest);
                if (rest.Length != 2 + nameLength) throw new InvalidDataException("Bad request.");
                var file = ShareDirectory.Resolve(services.ShareDirectory!, Encoding.UTF8.GetString(rest.Slice(2, nameLength)))
                           ?? throw new FileNotFoundException("That file is not shared.");
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = await SHA256.HashDataAsync(stream, s.Cancel.Token).ConfigureAwait(false);
                stream.Position = 0;
                var header = new byte[8 + 32];
                BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)stream.Length);
                hash.CopyTo(header, 8);
                await s.Secure.SendAsync(HostMessages.WithId(HostMessages.ShareFileStart, id, header), s.Cancel.Token).ConfigureAwait(false);
                var buffer = new byte[HostMessages.FileChunkBytes];
                int read;
                while ((read = await stream.ReadAsync(buffer, s.Cancel.Token).ConfigureAwait(false)) > 0)
                    await s.Secure.SendAsync(HostMessages.WithId(HostMessages.ShareFileChunk, id, buffer.AsSpan(0, read)), s.Cancel.Token).ConfigureAwait(false);
                await s.Secure.SendAsync(HostMessages.WithId(HostMessages.ShareFileEnd, id, []), s.Cancel.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                await AckAsync(s, id, false, ex is FileNotFoundException ? ex.Message : TransferException.From(ex).Message, s.Cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    /// <summary>Limits, quota and the owner's confirmation for the original (non-resumable) upload flow. Returns the reason to refuse, or null.</summary>
    private async Task<string?> LegacyPreflightAsync(Session s, string requestedName, long size, CancellationToken ct)
    {
        var o = FileOptions;
        if (size > o.MaxFileBytes) return $"The file is larger than the allowed {TransferReceiver.FormatBytes(o.MaxFileBytes)}.";
        if (o.ReceiveQuotaBytes > 0 && TransferReceiver.UsedBytes(services.ReceiveDirectory!, "") + size > o.ReceiveQuotaBytes)
            return $"The receive folder is limited to {TransferReceiver.FormatBytes(o.ReceiveQuotaBytes)} and this would go over it.";
        if (o.ConfirmIncoming is null) return null;
        var accepted = await HostTransferEndpoint.ConfirmAsync(o, new IncomingFileRequest(s.ViewerId, FileNames.Sanitize(requestedName), size, false), ct).ConfigureAwait(false);
        return accepted ? null : "The owner of this PC declined the file.";
    }

    /// <summary>Capability bits for the resumable file protocol. <c>Capabilities</c> adds them to the hello.</summary>
    private HostCapabilities TransferCapabilities(Session s) =>
        s.Control && (services.ReceiveDirectory is not null || services.ShareDirectory is not null) ? HostCapabilities.ResumableFiles : HostCapabilities.None;

    /// <summary>Handles one message with an id in 0x40-0x4F (resumable transfers). Partial files stay on disk when the session ends so a later session can resume.</summary>
    private Task HandleTransferAsync(Session s, byte[] message, CancellationToken ct)
    {
        var endpoint = _endpoints.GetValue(s, key =>
        {
            var created = new HostTransferEndpoint(key.ViewerId, services.ReceiveDirectory, services.ShareDirectory, FileOptions,
                (data, token) => key.Secure.SendAsync(data, token), log);
            key.Cancel.Token.Register(created.Dispose);
            return created;
        });
        return endpoint.HandleAsync(message, ct);
    }
}

/// <summary>
/// The host's side of the resumable file protocol for one viewer session: receives uploads into the receive folder (asking the owner first when a confirmation hook is set) and streams
/// files and zipped folders from the share folder. Independent of the transport: it is given a way to send and is fed the messages received.
/// </summary>
public sealed class HostTransferEndpoint : IDisposable
{
    private const int MaxConcurrentDownloads = 2;
    private readonly string _viewer;
    private readonly string? _receiveDirectory;
    private readonly string? _shareDirectory;
    private readonly FileTransferOptions _options;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _send;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<uint, Incoming> _incoming = new();
    private readonly ConcurrentDictionary<uint, Outgoing> _outgoing = new();
    private int _purged;

    private sealed class Incoming(TransferWire.Announce announce, CancellationTokenSource cts)
    {
        public TransferWire.Announce Announce { get; } = announce;
        public CancellationTokenSource Cts { get; } = cts;
        public TransferReceiver? Receiver { get; set; }
    }

    private sealed class Outgoing(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public TaskCompletionSource<long> Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public HostTransferEndpoint(string viewerLabel, string? receiveDirectory, string? shareDirectory, FileTransferOptions options,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> send, Action<string> log)
    {
        _viewer = viewerLabel; _receiveDirectory = receiveDirectory; _shareDirectory = shareDirectory; _options = options; _send = send; _log = log;
    }

    /// <summary>Asks the confirmation hook; a hook that is cancelled, or does not answer in time, means "no".</summary>
    internal static async Task<bool> ConfirmAsync(FileTransferOptions options, IncomingFileRequest request, CancellationToken ct)
    {
        if (options.ConfirmIncoming is not { } confirm) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ConfirmTimeout);
        try { return await confirm(request, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public async Task HandleAsync(byte[] message, CancellationToken ct)
    {
        if (message.Length == 0 || !HostMessages.TryReadId(message, out var id, out _)) return;
        var body = message.AsMemory(5);
        try
        {
            switch (message[0])
            {
                case TransferMessages.Offer: await OfferAsync(id, body).ConfigureAwait(false); break;
                case TransferMessages.Data: await DataAsync(id, body).ConfigureAwait(false); break;
                case TransferMessages.End: End(id, body); break;
                case TransferMessages.Cancel: Cancel(id); break;
                case TransferMessages.Pull: await PullAsync(id, body).ConfigureAwait(false); break;
                case TransferMessages.Start: StartOutgoing(id, body.Span); break;
                case TransferMessages.ListEntries:
                    if (_shareDirectory is null) { await ResultAsync(id, TransferError.Unsupported, "This PC does not share files.").ConfigureAwait(false); break; }
                    await SendAsync(HostMessages.Prefixed(TransferMessages.EntriesReply, JsonSerializer.SerializeToUtf8Bytes(ShareDirectoryEx.List(_shareDirectory)))).ConfigureAwait(false);
                    break;
                default: await ResultAsync(id, TransferError.Unsupported, "That request is not available.").ConfigureAwait(false); break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = TransferException.From(ex);
            _log($"Transfer {id} from {_viewer}: {failure.Message}");
            await ResultAsync(id, failure.Code, failure.Message).ConfigureAwait(false);
        }
    }

    // ---- uploads (viewer -> this PC) ----

    private async Task OfferAsync(uint id, ReadOnlyMemory<byte> body)
    {
        if (_receiveDirectory is null) { await ResultAsync(id, TransferError.Unsupported, "This PC does not accept files.").ConfigureAwait(false); return; }
        if (!TransferWire.TryReadAnnounce(body.Span, out var announce)) { await ResultAsync(id, TransferError.Protocol, "Bad file header.").ConfigureAwait(false); return; }
        if (_incoming.Count >= _options.MaxConcurrentIncoming) { await ResultAsync(id, TransferError.Busy, "Too many files are being received at once.").ConfigureAwait(false); return; }
        var incoming = new Incoming(announce, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        if (!_incoming.TryAdd(id, incoming))
        {
            incoming.Cts.Dispose();
            await ResultAsync(id, TransferError.Protocol, "That transfer id is already in use.").ConfigureAwait(false);
            return;
        }
        if (Interlocked.Exchange(ref _purged, 1) == 0) TransferReceiver.PurgeStalePartials(_receiveDirectory, _options.StalePartialAge);

        // The owner may take a while to answer: do not hold up frames and input for this viewer meanwhile.
        _ = Task.Run(async () =>
        {
            try
            {
                var token = incoming.Cts.Token;
                var resuming = announce.Resumable && File.Exists(TransferReceiver.PartPathFor(_receiveDirectory, _viewer, announce));
                if (!resuming && !await ConfirmAsync(_options, new IncomingFileRequest(_viewer, FileNames.Sanitize(announce.Name), announce.Size, announce.IsFolder), token).ConfigureAwait(false))
                {
                    _incoming.TryRemove(id, out _);
                    _log($"Declined '{announce.Name}' from {_viewer}.");
                    await ResultAsync(id, TransferError.Declined, "The owner of this PC declined the file.").ConfigureAwait(false);
                    return;
                }
                token.ThrowIfCancellationRequested();
                incoming.Receiver = TransferReceiver.Open(_receiveDirectory, _viewer, announce, _options);
                await SendAsync(TransferWire.EncodeOffset(TransferMessages.Accept, id, incoming.Receiver.Offset)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { _incoming.TryRemove(id, out _); }
            catch (Exception ex)
            {
                _incoming.TryRemove(id, out _);
                var failure = TransferException.From(ex);
                _log($"Could not accept '{announce.Name}' from {_viewer}: {failure.Message}");
                await ResultAsync(id, failure.Code, failure.Message).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    private async Task DataAsync(uint id, ReadOnlyMemory<byte> body)
    {
        if (!_incoming.TryGetValue(id, out var incoming) || incoming.Receiver is not { } receiver) return;   // declined, cancelled or failed earlier: drop the rest
        try
        {
            if (!TransferWire.TryReadData(body.Span, out var offset, out var data)) throw new TransferException(TransferError.Protocol, "Bad data message.");
            receiver.Write(offset, data);
        }
        catch (Exception ex)
        {
            _incoming.TryRemove(id, out _);
            var failure = TransferException.From(ex);
            if (failure.Code == TransferError.DiskFull) receiver.Dispose(); else receiver.Discard();   // a full disk keeps the partial file so the transfer can resume
            incoming.Cts.Dispose();
            _log($"Receiving '{incoming.Announce.Name}' from {_viewer} failed: {failure.Message}");
            await ResultAsync(id, failure.Code, failure.Message).ConfigureAwait(false);
        }
    }

    private void End(uint id, ReadOnlyMemory<byte> body)
    {
        if (!_incoming.TryRemove(id, out var incoming) || incoming.Receiver is not { } receiver) return;
        var end = body.ToArray();
        // Verifying and unpacking can take a while for big folders: not on the loop that also serves frames.
        _ = Task.Run(async () =>
        {
            var code = TransferError.None;
            string text;
            try
            {
                if (!TransferWire.TryReadEnd(end, out var total, out var sha)) throw new TransferException(TransferError.Protocol, "Bad end message.");
                text = receiver.Complete(total, sha);
                _log($"Received '{text}' from {_viewer}.");
            }
            catch (Exception ex)
            {
                var failure = TransferException.From(ex);
                code = failure.Code; text = failure.Message;
                _log($"Receiving '{incoming.Announce.Name}' from {_viewer} failed: {text}");
            }
            finally
            {
                receiver.Dispose();
                incoming.Cts.Dispose();
            }
            // The partial file is stored or gone before the viewer is told.
            await ResultAsync(id, code, text).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    private void Cancel(uint id)
    {
        if (_incoming.TryRemove(id, out var incoming))
        {
            incoming.Cts.Cancel();
            incoming.Receiver?.Discard();
            incoming.Cts.Dispose();
            _log($"{_viewer} cancelled sending '{incoming.Announce.Name}'.");
        }
        if (_outgoing.TryGetValue(id, out var outgoing)) outgoing.Cts.Cancel();
    }

    // ---- downloads (this PC -> viewer) ----

    private async Task PullAsync(uint id, ReadOnlyMemory<byte> body)
    {
        if (_shareDirectory is null) { await ResultAsync(id, TransferError.Unsupported, "This PC does not share files.").ConfigureAwait(false); return; }
        if (!TransferWire.TryReadPull(body.Span, out var name)) { await ResultAsync(id, TransferError.Protocol, "Bad request.").ConfigureAwait(false); return; }
        if (_outgoing.Count >= MaxConcurrentDownloads) { await ResultAsync(id, TransferError.Busy, "Too many files are being sent at once.").ConfigureAwait(false); return; }
        var entry = ShareDirectoryEx.Resolve(_shareDirectory, name);
        if (entry is null) { await ResultAsync(id, TransferError.NotFound, "That file is not shared.").ConfigureAwait(false); return; }
        if (!entry.IsFolder && entry.Size > _options.MaxFileBytes)
        {
            await ResultAsync(id, TransferError.TooLarge, $"The file is larger than the allowed {TransferReceiver.FormatBytes(_options.MaxFileBytes)}.").ConfigureAwait(false);
            return;
        }
        var outgoing = new Outgoing(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        if (!_outgoing.TryAdd(id, outgoing))
        {
            outgoing.Cts.Dispose();
            await ResultAsync(id, TransferError.Protocol, "That transfer id is already in use.").ConfigureAwait(false);
            return;
        }
        var path = Path.Combine(_shareDirectory, entry.Name);

        _ = Task.Run(async () =>
        {
            var token = outgoing.Cts.Token;
            try
            {
                var source = entry.IsFolder ? TransferSource.ForFolder(path) : TransferSource.ForFile(path);
                await SendAsync(TransferWire.EncodeAnnounce(TransferMessages.Begin, id, source.Announce)).ConfigureAwait(false);
                var offset = await outgoing.Start.Task.WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                if (source.IsFolder || offset > source.Size) offset = 0;
                var (total, hash) = await TransferPump.RunAsync(source, TransferMessages.DataToViewer, id, offset, (data, t) => _send(data, t), null, token).ConfigureAwait(false);
                await SendAsync(TransferWire.EncodeEnd(TransferMessages.EndToViewer, id, total, hash)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* the viewer cancelled, or the session ended */ }
            catch (TimeoutException) { _log($"{_viewer} did not answer a download offer in time."); }
            catch (Exception ex)
            {
                var failure = TransferException.From(ex);
                _log($"Sending '{name}' to {_viewer} failed: {failure.Message}");
                await ResultAsync(id, failure.Code, failure.Message).ConfigureAwait(false);
            }
            finally
            {
                _outgoing.TryRemove(id, out _);
                outgoing.Cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void StartOutgoing(uint id, ReadOnlySpan<byte> body)
    {
        if (_outgoing.TryGetValue(id, out var outgoing) && TransferWire.TryReadOffset(body, out var offset)) outgoing.Start.TrySetResult(offset);
    }

    // ---- plumbing ----

    private async Task SendAsync(ReadOnlyMemory<byte> message)
    {
        try { await _send(message, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or System.Net.WebSockets.WebSocketException)
        {
            // the link is gone: the viewer notices that by itself
        }
    }

    private Task ResultAsync(uint id, TransferError code, string text) => SendAsync(TransferWire.EncodeResult(id, code, text));

    /// <summary>The session ended. Partial files of resumable uploads stay on disk; everything that is running stops.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var (_, incoming) in _incoming) incoming.Receiver?.Dispose();
        _incoming.Clear();
    }
}
