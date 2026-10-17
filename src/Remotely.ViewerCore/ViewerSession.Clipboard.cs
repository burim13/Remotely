using System.Text;
using Remotely.HostCore;

namespace Remotely.ViewerCore;

public sealed partial class ViewerSession
{
    // ---- clipboard ----

    private ClipPeer? _clip;
    private uint _hostLayout;

    public Task SendClipboardAsync(string text)
    {
        if (!HasClipboard || text.Length > HostMessages.MaxClipboardChars) return Task.CompletedTask;
        return _secure.SendAsync(HostMessages.Prefixed(HostMessages.Clipboard, Encoding.UTF8.GetBytes(text)), _cts.Token);
    }

    /// <summary>The host takes HTML, images and files besides text (it advertised <see cref="HostCapabilities.RichClipboard"/> and has a clipboard).</summary>
    public bool HasRichClipboard => HasClipboard && SupportsExtendedInput;

    /// <summary>The rich clipboard transfer logic: limits, confirmation hooks, loop prevention.</summary>
    public ClipPeer RichClipboard => _clip ??= CreateClipPeer();

    /// <summary>Raised (on a background thread) when the host sent a clipboard with more than text, after its data was verified. The app puts it on the local clipboard; the echo guard then keeps that from being sent back.</summary>
    public event Action<ClipBundle>? RichClipboardReceived;

    /// <summary>Raised when the host reported its keyboard layout handle (see <see cref="QueryHostKeyboardLayoutAsync"/>).</summary>
    public event Action<uint>? HostKeyboardLayoutChanged;

    /// <summary>The keyboard layout handle the host last reported; 0 = unknown.</summary>
    public uint HostKeyboardLayout => Volatile.Read(ref _hostLayout);

    /// <summary>Sends a copy that holds more than text. Plain text alone should keep using <see cref="SendClipboardAsync(string)"/>.</summary>
    public Task<ClipSendOutcome> SendRichClipboardAsync(ClipBundle bundle, CancellationToken ct = default)
    {
        if (!HasRichClipboard) return Task.FromResult(new ClipSendOutcome(ClipSendStatus.Refused, "The host does not take rich clipboard content."));
        return RichClipboard.SendAsync(bundle, CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token);
    }

    /// <summary>Asks the host to start (or stop) offering its rich clipboard changes. Turn on only when the person has clipboard sync enabled.</summary>
    public Task SubscribeRichClipboardAsync(bool on)
    {
        RichClipboard.AcceptIncoming = on;
        return HasRichClipboard ? _secure.SendAsync(new[] { FidelityMessages.ToHost.ClipSubscribe, on ? (byte)1 : (byte)0 }, _cts.Token) : Task.CompletedTask;
    }

    /// <summary>Asks which keyboard layout the host's focused window uses. The answer arrives as <see cref="HostKeyboardLayoutChanged"/>.</summary>
    public Task QueryHostKeyboardLayoutAsync() =>
        SupportsExtendedInput ? _secure.SendAsync(new[] { FidelityMessages.ToHost.LayoutQuery }, _cts.Token) : Task.CompletedTask;

    /// <summary>
    /// Handles one 0xB0..0xBF message from the host.
    /// INTEGRATION (ViewerSession.cs, owned by the integrator, <c>ReceiveLoopAsync</c> switch): add
    /// <c>case >= 0xB0 and <= 0xBF: await HandleFidelityAsync(message).ConfigureAwait(false); break;</c>
    /// </summary>
    private async Task HandleFidelityAsync(byte[] message)
    {
        if (FidelityMessages.TryReadLayoutReply(message, out var layout))
        {
            Volatile.Write(ref _hostLayout, layout);
            HostKeyboardLayoutChanged?.Invoke(layout);
        }
        else if (RichClipboard.Handles(message[0]))
        {
            try { await RichClipboard.HandleAsync(message, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not ObjectDisposedException) { HostError?.Invoke("Clipboard: " + ex.Message); }
        }
    }

    private ClipPeer CreateClipPeer() => new(ClipWire.ViewerToHost, ClipWire.HostToViewer,
        (m, ct) => _secure.SendAsync(m, ct),
        (bundle, _) => { RichClipboardReceived?.Invoke(bundle); return Task.CompletedTask; })
    {
        AcceptIncoming = false,
        UnavailableReason = "Clipboard sync is off.",
    };
}
