using System.Net.WebSockets;
using Remotely.Core.Protocol;

namespace Remotely.Server;

/// <summary>One device's WebSocket. Writers are serialised because relay and control traffic share the socket.</summary>
public sealed class DeviceConnection(WebSocket socket, string remote)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public WebSocket Socket { get; } = socket;
    public string Remote { get; } = remote;
    public string DeviceId { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];

    public Task SendAsync(ControlMessage message, CancellationToken ct = default) =>
        SendRawAsync(ControlCodec.Serialize(message), WebSocketMessageType.Text, ct);

    public Task SendRelayAsync(byte[] frame, CancellationToken ct = default) =>
        SendRawAsync(frame, WebSocketMessageType.Binary, ct);

    private async Task SendRawAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.SendAsync(data, type, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
