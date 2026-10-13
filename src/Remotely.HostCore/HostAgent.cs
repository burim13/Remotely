using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;

namespace Remotely.HostCore;

/// <summary>
/// The host's side of a coordinator connection: accepts sessions that viewers open, authenticates each viewer end to end against
/// <c>isTrusted</c>, then answers frame requests from <see cref="IFrameSource"/>.
/// </summary>
public sealed class HostAgent(DeviceIdentity identity, IFrameSource source, Func<string, bool> isTrusted, Action<string> log)
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
            using var secure = await SecureSession.AcceptAsync(relay, identity, isTrusted, ct).ConfigureAwait(false);
            log($"Viewer {viewer} connected.");
            while (await secure.ReceiveAsync(ct).ConfigureAwait(false) is { } message)
            {
                if (message.Length == 0 || message[0] != HostMessages.FrameRequest)
                {
                    await secure.SendAsync(HostMessages.Prefixed(HostMessages.Error, "Unknown request."u8), ct).ConfigureAwait(false);
                    continue;
                }
                await secure.SendAsync(await FrameReplyAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
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

    private async Task<byte[]> FrameReplyAsync(CancellationToken ct)
    {
        try
        {
            var body = FrameCodec.Encode(await source.CaptureAsync(ct).ConfigureAwait(false));
            // One relayed message is capped; leave room for the encryption overhead and the reply tag.
            if (body.Length + SecureChannel.Overhead + 1 > ProtocolInfo.MaxRelayBytes)
                return HostMessages.Prefixed(HostMessages.Error, "Frame too large to send."u8);
            return HostMessages.Prefixed(HostMessages.FrameReply, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Capture failed: {ex.Message}");
            return HostMessages.Prefixed(HostMessages.Error, Encoding.UTF8.GetBytes("Capture failed: " + ex.Message));
        }
    }
}
