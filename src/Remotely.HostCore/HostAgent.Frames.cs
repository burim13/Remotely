using System.Buffers.Binary;
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
    /// <summary>What one viewer asked for with <see cref="FrameMessages.StreamConfig"/>, and what the host has already told it.</summary>
    private sealed class FrameStream
    {
        public readonly int Id = FrameSessions.NewId();
        public bool Tiles, Cursor;
        public int Quality, Monitor = -1;
        public bool NeedKey = true;
        public ulong SentShapeId;
        public CursorState? LastCursor;
        public IReadOnlyList<MonitorInfo>? KnownMonitors;
    }

    private static readonly ConditionalWeakTable<Session, FrameStream> FrameStreams = new();

    private static FrameStream StreamOf(Session s) => FrameStreams.GetValue(s, _ => new FrameStream());

    /// <summary>The introduction for a new viewer: capabilities plus, when the capture source can say, the display list (protocol version 2).</summary>
    private async Task<HelloInfo> HelloForAsync(Session s, CancellationToken ct)
    {
        var caps = Capabilities(s);
        if (services.Frames is not ITiledFrameSource tiled) return new HelloInfo(1, caps);
        IReadOnlyList<MonitorInfo> monitors = [];
        try
        {
            monitors = (await tiled.CaptureTilesAsync(new TileRequest(StreamOf(s).Id, -1, 0, false, true, 0, false), ct).ConfigureAwait(false)).Monitors;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log("Could not list the displays for the hello: " + ex.Message); }
        StreamOf(s).KnownMonitors = monitors;
        return new HelloInfo(HostMessages.ProtocolVersion, caps | HostCapabilities.Monitors | HostCapabilities.CursorShape, monitors);
    }

    private async Task HandleFrameMessageAsync(Session s, byte[] message, CancellationToken ct)
    {
        var stream = StreamOf(s);
        switch (message[0])
        {
            case FrameMessages.StreamConfig when message.Length >= 4 && services.Frames is ITiledFrameSource:
                var tiles = (message[1] & FrameMessages.FlagTiles) != 0;
                var monitor = (sbyte)message[3];
                if (tiles != stream.Tiles || monitor != stream.Monitor) stream.NeedKey = true;
                stream.Tiles = tiles;
                stream.Cursor = (message[1] & FrameMessages.FlagCursor) != 0;
                stream.Quality = message[2];
                stream.Monitor = monitor;
                break;
            case FrameMessages.KeyFrameRequest when services.Frames is ITiledFrameSource:
                stream.NeedKey = true;
                break;
            default:
                await ErrorAsync(s, "That request is not available.", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendTilesAsync(Session s, FrameStream stream, ITiledFrameSource source, CancellationToken ct)
    {
        TileCapture capture;
        try
        {
            capture = await source.CaptureTilesAsync(new TileRequest(stream.Id, stream.Monitor, stream.Quality, stream.NeedKey, false, stream.SentShapeId, stream.Cursor), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Capture failed: {ex.Message}");
            stream.NeedKey = true;
            await ErrorAsync(s, "Capture failed: " + ex.Message, ct).ConfigureAwait(false);
            return;
        }

        if (stream.KnownMonitors is { } known && !known.SequenceEqual(capture.Monitors) && capture.Monitors.Count > 0)
        {
            stream.KnownMonitors = capture.Monitors;
            await s.Secure.SendAsync(HostMessages.Prefixed(FrameMessages.MonitorList, FrameWire.EncodeMonitors(capture.Monitors)), ct).ConfigureAwait(false);
        }
        if (capture.NewShape is { } shape)
        {
            stream.SentShapeId = shape.Id;
            await s.Secure.SendAsync(HostMessages.Prefixed(FrameMessages.CursorShapeReply, FrameWire.EncodeShape(shape)), ct).ConfigureAwait(false);
        }
        if (capture.Cursor is { } cursor && cursor != stream.LastCursor)
        {
            stream.LastCursor = cursor;
            await s.Secure.SendAsync(HostMessages.Prefixed(FrameMessages.CursorStateReply, FrameWire.EncodeState(cursor)), ct).ConfigureAwait(false);
        }

        if (capture.Frame is not { } frame)
        {
            await s.Secure.SendAsync(new[] { HostMessages.Unchanged }, ct).ConfigureAwait(false);
            return;
        }
        var message = HostMessages.Prefixed(FrameMessages.TileFrame, FrameWire.EncodeTileFrame(frame));
        if (message.Length + SecureChannel.Overhead > ProtocolInfo.MaxRelayBytes)
        {
            stream.NeedKey = true; // the source already counted these tiles as delivered: ask it for a complete picture next time
            await ErrorAsync(s, "Frame too large to send.", ct).ConfigureAwait(false);
            return;
        }
        stream.NeedKey = true;
        await s.Secure.SendAsync(message, ct).ConfigureAwait(false);
        stream.NeedKey = false;
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

        if (StreamOf(s) is { Tiles: true } stream && services.Frames is ITiledFrameSource tiledSource)
        {
            await SendTilesAsync(s, stream, tiledSource, ct).ConfigureAwait(false);
            return;
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
}
