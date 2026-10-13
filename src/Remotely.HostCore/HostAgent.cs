using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;

namespace Remotely.HostCore;

/// <summary>
/// The host's side of a coordinator connection: accepts sessions that viewers open, authenticates each viewer end to end, then serves
/// frames, input, clipboard and files. Viewers that are trusted but not allowed to control get frames only.
/// </summary>
public sealed class HostAgent(DeviceIdentity identity, HostServices services, Action<string> log)
{
    private const int MaxConcurrentSessions = 4;
    private int _active;

    /// <summary>Runs until the coordinator connection ends or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CoordinatorClient client, CancellationToken ct)
    {
        var running = new List<Task>();
        try
        {
            while (true)
            {
                var relay = await client.IncomingSessions.ReadAsync(ct).ConfigureAwait(false);
                running.RemoveAll(t => t.IsCompleted);
                if (Interlocked.Increment(ref _active) > MaxConcurrentSessions)
                {
                    Interlocked.Decrement(ref _active);
                    log($"Refused a session from {DeviceIds.Short(relay.PeerDeviceId)}: too many sessions.");
                    await relay.CloseAsync().ConfigureAwait(false);
                    continue;
                }
                running.Add(ServeAsync(relay, ct));
            }
        }
        catch (ChannelClosedException) { /* connection to the coordinator ended */ }
        catch (OperationCanceledException) { }
        finally
        {
            try { await Task.WhenAll(running).ConfigureAwait(false); } catch { /* each session logs its own failure */ }
        }
    }

    private async Task ServeAsync(RelaySession relay, CancellationToken ct)
    {
        var viewer = DeviceIds.Short(relay.PeerDeviceId);
        try
        {
            using var secure = await SecureSession.AcceptAsync(relay, identity, services.IsTrusted, ct).ConfigureAwait(false);
            var session = new Session(secure, relay.PeerDeviceId, services.CanControl(relay.PeerDeviceId));
            log($"Viewer {viewer} connected ({(session.Control ? "control" : "view only")}).");
            await secure.SendAsync(new HelloInfo(HostMessages.ProtocolVersion, Capabilities(session)).Encode(), ct).ConfigureAwait(false);
            try
            {
                while (await secure.ReceiveAsync(ct).ConfigureAwait(false) is { } message)
                    await HandleAsync(session, message, ct).ConfigureAwait(false);
            }
            finally
            {
                session.Dispose();
            }
            log($"Viewer {viewer} left.");
        }
        catch (HandshakeException ex)
        {
            log($"Refused viewer {relay.PeerDeviceId}: {ex.Message}");
            await relay.CloseAsync().ConfigureAwait(false);
        }
        catch (CryptographicException ex)
        {
            log($"Viewer {viewer}: tampered or invalid traffic, closing ({ex.Message}).");
            await relay.CloseAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private HostCapabilities Capabilities(Session s)
    {
        var caps = HostCapabilities.None;
        if (!s.Control) return caps;
        if (services.Input is not null) caps |= HostCapabilities.Control;
        if (services.SecureAttention is not null) caps |= HostCapabilities.SecureAttention;
        if (services.Clipboard is not null) caps |= HostCapabilities.Clipboard;
        if (services.ReceiveDirectory is not null || services.ShareDirectory is not null) caps |= HostCapabilities.Files;
        return caps;
    }

    private async Task HandleAsync(Session s, byte[] message, CancellationToken ct)
    {
        if (message.Length == 0) return;
        try
        {
            switch (message[0])
            {
                case HostMessages.FrameRequest:
                    await SendFrameAsync(s, ct).ConfigureAwait(false);
                    break;
                case HostMessages.Input when s.Control && services.Input is not null:
                    if (InputCodec.TryDecode(message, out var events) && events.Count > 0)
                        await services.Input.InjectAsync(events, ct).ConfigureAwait(false);
                    else await ErrorAsync(s, "Bad input message.", ct).ConfigureAwait(false);
                    break;
                case HostMessages.Clipboard when s.Control && services.Clipboard is not null:
                    if (message.Length - 1 <= HostMessages.MaxClipboardChars * 4)
                        s.ClipboardSequence = await services.Clipboard.WriteAsync(Encoding.UTF8.GetString(message, 1, message.Length - 1), ct).ConfigureAwait(false);
                    break;
                case HostMessages.SecureAttention when s.Control && services.SecureAttention is not null:
                    await services.SecureAttention.SendAsync(ct).ConfigureAwait(false);
                    break;
                case HostMessages.FileStart or HostMessages.FileChunk or HostMessages.FileEnd when s.Control && services.ReceiveDirectory is not null:
                    await HandleUploadAsync(s, message, ct).ConfigureAwait(false);
                    break;
                case HostMessages.ShareList when s.Control && services.ShareDirectory is not null:
                    await s.Secure.SendAsync(HostMessages.Prefixed(HostMessages.ShareListReply,
                        JsonSerializer.SerializeToUtf8Bytes(ShareDirectory.List(services.ShareDirectory))), ct).ConfigureAwait(false);
                    break;
                case HostMessages.ShareGet when s.Control && services.ShareDirectory is not null:
                    StartDownload(s, message, ct);
                    break;
                default:
                    await ErrorAsync(s, "That request is not available.", ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not CryptographicException)
        {
            log($"Request {message[0]:X2} failed: {ex.Message}");
            await ErrorAsync(s, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task SendFrameAsync(Session s, CancellationToken ct)
    {
        if (s.Control && services.Clipboard is not null)
        {
            try
            {
                var (sequence, text) = await services.Clipboard.ReadAsync(ct).ConfigureAwait(false);
                if (sequence != s.ClipboardSequence)
                {
                    s.ClipboardSequence = sequence;
                    if (!string.IsNullOrEmpty(text) && text.Length <= HostMessages.MaxClipboardChars)
                        await s.Secure.SendAsync(HostMessages.Prefixed(HostMessages.ClipboardReply, Encoding.UTF8.GetBytes(text)), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log("Clipboard read failed: " + ex.Message); }
        }

        FrameInfo frame;
        try { frame = await services.Frames.CaptureAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Capture failed: {ex.Message}");
            await ErrorAsync(s, "Capture failed: " + ex.Message, ct).ConfigureAwait(false);
            return;
        }

        var fingerprint = SHA256.HashData([.. frame.Jpeg, .. Encoding.UTF8.GetBytes(frame.Desktop)]);
        if (s.LastFrame is not null && fingerprint.AsSpan().SequenceEqual(s.LastFrame))
        {
            await s.Secure.SendAsync(new[] { HostMessages.Unchanged }, ct).ConfigureAwait(false);
            return;
        }
        var body = FrameCodec.Encode(frame);
        // One relayed message is capped; leave room for the encryption overhead and the kind byte.
        if (body.Length + SecureChannel.Overhead + 1 > ProtocolInfo.MaxRelayBytes)
        {
            await ErrorAsync(s, "Frame too large to send.", ct).ConfigureAwait(false);
            return;
        }
        s.LastFrame = fingerprint;
        await s.Secure.SendAsync(HostMessages.Prefixed(HostMessages.FrameReply, body), ct).ConfigureAwait(false);
    }

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
                    s.Upload = FileSink.Create(services.ReceiveDirectory!, Encoding.UTF8.GetString(rest.Slice(42, nameLength)), size, hash);
                    s.UploadId = id;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    await AckAsync(s, id, false, ex.Message, ct).ConfigureAwait(false);
                }
                break;
            case HostMessages.FileChunk when s.Upload is not null && s.UploadId == id:
                try { s.Upload.Write(rest); }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    s.Upload.Dispose(); s.Upload = null;
                    await AckAsync(s, id, false, ex.Message, ct).ConfigureAwait(false);
                }
                break;
            case HostMessages.FileEnd when s.Upload is not null && s.UploadId == id:
                string? stored = null, failure = null;
                try { stored = Path.GetFileName(s.Upload.Complete()); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { failure = ex.Message; }
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
                await AckAsync(s, id, false, ex.Message, s.Cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private static Task AckAsync(Session s, uint id, bool ok, string message, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetBytes(message.Length > 200 ? message[..200] : message);
        var payload = new byte[1 + 2 + text.Length];
        payload[0] = ok ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)text.Length);
        text.CopyTo(payload, 3);
        return s.Secure.SendAsync(HostMessages.WithId(HostMessages.FileAck, id, payload), ct);
    }

    private static Task ErrorAsync(Session s, string message, CancellationToken ct) =>
        s.Secure.SendAsync(HostMessages.Prefixed(HostMessages.Error, Encoding.UTF8.GetBytes(message)), ct);

    private sealed class Session(SecureSession secure, string viewerId, bool control) : IDisposable
    {
        public SecureSession Secure { get; } = secure;
        public string ViewerId { get; } = viewerId;
        public bool Control { get; } = control;
        public CancellationTokenSource Cancel { get; } = new();
        public byte[]? LastFrame { get; set; }
        public long ClipboardSequence { get; set; } = -1;
        public FileSink? Upload { get; set; }
        public uint UploadId { get; set; }
        public Task? Download { get; set; }

        public void Dispose()
        {
            Cancel.Cancel();
            Upload?.Dispose();
            try { Download?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            Cancel.Dispose();
        }
    }
}
