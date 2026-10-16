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
    // ---- input ----

    /// <summary>Queues an event. Mouse moves are coalesced so a fast mouse cannot flood the link; everything else keeps its order.</summary>
    public void SendInput(InputEvent e)
    {
        if (!CanControl) return;
        lock (_inputGate)
        {
            if (e.Kind == InputKind.MouseMove && _pendingInput.Count > 0 && _pendingInput[^1].Kind == InputKind.MouseMove)
                _pendingInput[^1] = e;
            else if (_pendingInput.Count < 4096)
                _pendingInput.Add(e);
        }
        _inputSignal.Release();
    }

    public void ReleaseAllInput() => SendInput(new InputEvent(InputKind.ReleaseAll));

    private async Task InputLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _inputSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                while (true)
                {
                    List<InputEvent> batch;
                    lock (_inputGate)
                    {
                        if (_pendingInput.Count == 0) break;
                        var take = Math.Min(_pendingInput.Count, InputCodec.MaxEventsPerMessage);
                        batch = _pendingInput.GetRange(0, take);
                        _pendingInput.RemoveRange(0, take);
                    }
                    await _secure.SendAsync(InputCodec.Encode(batch), _cts.Token).ConfigureAwait(false);
                    await Task.Delay(8, _cts.Token).ConfigureAwait(false); // ~120 batches/s ceiling
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { _ = EndAsync(ex.Message); }
    }

    public Task SendSecureAttentionAsync() =>
        CanSendSecureAttention ? _secure.SendAsync(new[] { HostMessages.SecureAttention }, _cts.Token) : Task.CompletedTask;
}
