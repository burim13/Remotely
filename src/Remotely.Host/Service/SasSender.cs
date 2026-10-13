using Remotely.HostCore;

namespace Remotely.Host.Service;

/// <summary>Ctrl+Alt+Del. Windows only lets a service generate the Secure Attention Sequence, and only when the policy "Disable or enable software Secure Attention Sequence" allows services.</summary>
internal sealed class SasSender : ISecureAttention
{
    public Task SendAsync(CancellationToken ct)
    {
        try
        {
            Interop.Native.SendSAS(false);
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("This Windows edition cannot send Ctrl+Alt+Del programmatically.", ex);
        }
    }
}
