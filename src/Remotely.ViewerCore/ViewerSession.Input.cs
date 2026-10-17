using Remotely.HostCore;

namespace Remotely.ViewerCore;

public sealed partial class ViewerSession
{
    // ---- input ----

    private readonly HeldInputTracker _held = new();
    private int _heartbeatStarted;

    /// <summary>
    /// The host understands typed text, touch, pen and heartbeats (it advertised <see cref="HostCapabilities.RichClipboard"/>). Events of those kinds are dropped
    /// silently for any other host, because an older host rejects the whole message that contains one.
    /// </summary>
    public bool SupportsExtendedInput => Hello.Capabilities.HasFlag(HostCapabilities.RichClipboard);

    /// <summary>Queues an event. Mouse moves are coalesced so a fast mouse cannot flood the link; everything else keeps its order.</summary>
    public void SendInput(InputEvent e)
    {
        if (!CanControl) return;
        if (e.IsExtension && !SupportsExtendedInput) return;
        if (SupportsExtendedInput && Interlocked.Exchange(ref _heartbeatStarted, 1) == 0)
        {
            SendInput(InputEvent.Heartbeat()); // first, so the host knows to release everything if this viewer ever goes silent while holding something
            _ = Task.Run(HeartbeatLoopAsync);
        }
        lock (_inputGate)
        {
            _held.Apply(e);
            if (_pendingInput.Count > 0 && Coalesces(_pendingInput[^1], e)) _pendingInput[^1] = e;
            else if (_pendingInput.Count < 4096)
                _pendingInput.Add(e);
        }
        _inputSignal.Release();
    }

    public void SendInput(IEnumerable<InputEvent> events)
    {
        foreach (var e in events) SendInput(e);
    }

    private static bool Coalesces(InputEvent last, InputEvent next) =>
        (last.Kind, next.Kind) switch
        {
            (InputKind.MouseMove, InputKind.MouseMove) => true,
            (InputKind.Touch, InputKind.Touch) => last.Phase == (byte)TouchPhase.Move && next.Phase == (byte)TouchPhase.Move && last.Arg1 == next.Arg1,
            (InputKind.Pen, InputKind.Pen) => last.Flags == next.Flags && last.Phase is (byte)PenPhase.Move or (byte)PenPhase.Hover,
            _ => false,
        };

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                bool holding;
                lock (_inputGate) holding = _held.AnyHeld;
                if (holding) SendInput(InputEvent.Heartbeat());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
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
