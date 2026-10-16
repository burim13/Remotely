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
    private async Task FrameLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                var waiter = _frameWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _secure.SendAsync(new[] { HostMessages.FrameRequest }, _cts.Token).ConfigureAwait(false);
                var changed = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                var wait = (changed ? MinFrameInterval : UnchangedBackoff) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (TimeoutException) { _ = EndAsync("The host stopped responding."); }
        catch (Exception ex) { _ = EndAsync(ex.Message); }
    }
}
