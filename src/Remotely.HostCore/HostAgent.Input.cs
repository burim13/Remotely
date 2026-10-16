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
    private async Task HandleInputAsync(Session s, byte[] message, CancellationToken ct)
    {
        if (InputCodec.TryDecode(message, out var events) && events.Count > 0)
            await services.Input!.InjectAsync(events, ct).ConfigureAwait(false);
        else await ErrorAsync(s, "Bad input message.", ct).ConfigureAwait(false);
    }

    private async Task HandleClipboardAsync(Session s, byte[] message, CancellationToken ct)
    {
        if (message.Length - 1 <= HostMessages.MaxClipboardChars * 4)
            s.ClipboardSequence = await services.Clipboard!.WriteAsync(Encoding.UTF8.GetString(message, 1, message.Length - 1), ct).ConfigureAwait(false);
    }
}
