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
public sealed partial class HostAgent(DeviceIdentity identity, HostServices services, Action<string> log)
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
            await ServeChannelAsync(secure, viewer, services.CanControl(relay.PeerDeviceId), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Serves one already-authenticated viewer over any encrypted channel (a relay through the server, or a direct connection on the local network):
    /// introduces the host, then answers frame requests, input, clipboard and file messages until the viewer leaves.
    /// </summary>
    public async Task ServeChannelAsync(IMessageChannel secure, string viewerLabel, bool control, CancellationToken ct)
    {
        var session = new Session(secure, viewerLabel, control);
        log($"Viewer {viewerLabel} connected ({(session.Control ? "control" : "view only")}).");
        await secure.SendAsync((await HelloForAsync(session, ct).ConfigureAwait(false)).Encode(), ct).ConfigureAwait(false);
        try
        {
            while (await secure.ReceiveAsync(ct).ConfigureAwait(false) is { } message)
                await HandleAsync(session, message, ct).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
        log($"Viewer {viewerLabel} left.");
    }

    private HostCapabilities Capabilities(Session s)
    {
        var caps = HostCapabilities.None;
        if (!s.Control) return caps;
        if (services.Input is not null) caps |= HostCapabilities.Control;
        if (services.SecureAttention is not null) caps |= HostCapabilities.SecureAttention;
        if (services.Clipboard is not null) caps |= HostCapabilities.Clipboard;
        if (services.ReceiveDirectory is not null || services.ShareDirectory is not null) caps |= HostCapabilities.Files;
        caps |= FidelityHostSession.Advertise(services);   // A7: rich clipboard
        caps |= TransferCapabilities(s);                    // A8: resumable transfers
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
                    await HandleInputAsync(s, message, ct).ConfigureAwait(false);
                    break;
                case HostMessages.Clipboard when s.Control && services.Clipboard is not null:
                    await HandleClipboardAsync(s, message, ct).ConfigureAwait(false);
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
                case >= 0x20 and <= 0x2F:
                    await HandleFrameMessageAsync(s, message, ct).ConfigureAwait(false);
                    break;
                case >= 0x30 and <= 0x3F when s.Control:
                    await HandleFidelityAsync(s, message, ct).ConfigureAwait(false);
                    break;
                case >= TransferMessages.FirstViewerToHost and <= TransferMessages.LastViewerToHost when s.Control:
                    await HandleTransferAsync(s, message, ct).ConfigureAwait(false);
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

    private sealed class Session(IMessageChannel secure, string viewerId, bool control) : IDisposable
    {
        public IMessageChannel Secure { get; } = secure;
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
