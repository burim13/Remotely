using System.Buffers.Binary;
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
}
