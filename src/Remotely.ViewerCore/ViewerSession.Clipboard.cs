using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.HostCore;

namespace Remotely.ViewerCore;

public sealed partial class ViewerSession
{
    // ---- clipboard ----

    public Task SendClipboardAsync(string text)
    {
        if (!HasClipboard || text.Length > HostMessages.MaxClipboardChars) return Task.CompletedTask;
        return _secure.SendAsync(HostMessages.Prefixed(HostMessages.Clipboard, Encoding.UTF8.GetBytes(text)), _cts.Token);
    }
}
