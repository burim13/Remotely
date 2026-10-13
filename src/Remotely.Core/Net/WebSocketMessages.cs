using System.Buffers;
using System.Net.WebSockets;

namespace Remotely.Core.Net;

public readonly record struct WsMessage(WebSocketMessageType Type, byte[] Data);

public static class WebSocketMessages
{
    /// <summary>Reads one whole message. Returns null when the peer closed. Throws <see cref="InvalidDataException"/> if it exceeds <paramref name="maxBytes"/>.</summary>
    public static async Task<WsMessage?> ReceiveAsync(WebSocket ws, int maxBytes, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var assembled = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(rented, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (assembled.Length + result.Count > maxBytes)
                    throw new InvalidDataException("Message too large.");
                assembled.Write(rented, 0, result.Count);
                if (result.EndOfMessage) return new WsMessage(result.MessageType, assembled.ToArray());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
