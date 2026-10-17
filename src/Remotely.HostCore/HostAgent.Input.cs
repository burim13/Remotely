using System.Runtime.CompilerServices;
using System.Text;

namespace Remotely.HostCore;

public sealed partial class HostAgent
{
    private readonly ConditionalWeakTable<Session, FidelityHostSession> _fidelity = new();

    private FidelityHostSession FidelityFor(Session s) =>
        _fidelity.GetValue(s, x => new FidelityHostSession(services, (m, ct) => x.Secure.SendAsync(m, ct), x.Cancel.Token, log));

    /// <summary>
    /// Capability bits that belong to input fidelity and the rich clipboard; <c>Capabilities</c> adds them for a controlling viewer.
    /// INTEGRATION (HostAgent.cs, owned by the integrator): in <c>Capabilities(Session s)</c> add, after the Control line,
    /// <c>caps |= FidelityHostSession.Advertise(services);</c>
    /// </summary>
    private HostCapabilities FidelityCapabilities() => FidelityHostSession.Advertise(services);

    /// <summary>
    /// Handles one 0x30..0x3F message (clipboard transfer, layout query, clipboard subscription).
    /// INTEGRATION (HostAgent.cs, <c>HandleAsync</c> switch): add, before <c>default</c>,
    /// <c>case >= 0x30 and <= 0x3F when s.Control: await HandleFidelityAsync(s, message, ct).ConfigureAwait(false); break;</c>
    /// </summary>
    private Task HandleFidelityAsync(Session s, byte[] message, CancellationToken ct) => FidelityFor(s).HandleAsync(message, ct);

    private async Task HandleInputAsync(Session s, byte[] message, CancellationToken ct)
    {
        if (InputCodec.TryDecode(message, out var events) && events.Count > 0)
        {
            var inject = FidelityFor(s).Observe(events); // remembers what this viewer holds, so it can be released if the viewer leaves or vanishes
            if (inject.Count > 0) await services.Input!.InjectAsync(inject, ct).ConfigureAwait(false);
        }
        else await ErrorAsync(s, "Bad input message.", ct).ConfigureAwait(false);
    }

    private async Task HandleClipboardAsync(Session s, byte[] message, CancellationToken ct)
    {
        if (message.Length - 1 <= HostMessages.MaxClipboardChars * 4)
            s.ClipboardSequence = await services.Clipboard!.WriteAsync(Encoding.UTF8.GetString(message, 1, message.Length - 1), ct).ConfigureAwait(false);
    }
}
