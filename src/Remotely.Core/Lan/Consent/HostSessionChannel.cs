using System.Text;
using Remotely.Core.Crypto;

namespace Remotely.Core.Lan.Consent;

/// <summary>
/// Wraps the encrypted channel of the one connected PC on the sharing side. "Pause control" turns the session into view only: messages that would act on this PC
/// (mouse and keyboard, clipboard, Ctrl+Alt+Del, file upload and download) are dropped before the host code sees them, while screen requests still work.
/// The byte values are the message kinds of the host protocol (<c>HostMessages</c> in Remotely.HostCore, which this assembly cannot reference); a test compares them.
/// </summary>
public sealed class HostSessionChannel(IMessageChannel inner) : IMessageChannel
{
    public const byte KindInput = 0x02, KindClipboard = 0x03, KindSecureAttention = 0x04, KindFileFirst = 0x10, KindFileLast = 0x14, KindError = 0xFF;

    private volatile bool _paused;

    public bool Paused => _paused;
    public int DroppedWhilePaused { get; private set; }

    public static bool ActsOnThisPc(byte kind) => kind is KindInput or KindClipboard or KindSecureAttention || (kind >= KindFileFirst && kind <= KindFileLast);

    /// <summary>Pauses or resumes control. Tells the other PC once (as a host message it already shows) so it does not look broken.</summary>
    public async Task SetPausedAsync(bool paused, string noticeForViewer, CancellationToken ct = default)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (!paused) return;
        var text = Encoding.UTF8.GetBytes(noticeForViewer);
        var message = new byte[1 + text.Length];
        message[0] = KindError;
        text.CopyTo(message, 1);
        try { await inner.SendAsync(message, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    public Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => inner.SendAsync(plaintext, ct);

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var message = await inner.ReceiveAsync(ct).ConfigureAwait(false);
            if (message is null) return null;
            if (_paused && message.Length > 0 && ActsOnThisPc(message[0])) { DroppedWhilePaused++; continue; }
            return message;
        }
    }

    public Task CloseAsync() => inner.CloseAsync();

    public void Dispose() => inner.Dispose();
}
