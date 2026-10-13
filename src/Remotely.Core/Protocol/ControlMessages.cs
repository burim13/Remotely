using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remotely.Core.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
    public const string AuthLabel = "RMT1-AUTH";
    public const int MaxControlBytes = 64 * 1024;
    public const int MaxRelayBytes = 1024 * 1024;
}

/// <summary>JSON control messages exchanged between a device and the coordinator (WebSocket text frames).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(ServerHello), "server.hello")]
[JsonDerivedType(typeof(AuthResponse), "auth.response")]
[JsonDerivedType(typeof(NotPaired), "auth.not-paired")]
[JsonDerivedType(typeof(Authenticated), "auth.ok")]
[JsonDerivedType(typeof(PairRequest), "pair.request")]
[JsonDerivedType(typeof(PairResult), "pair.result")]
[JsonDerivedType(typeof(ListDevices), "devices.list")]
[JsonDerivedType(typeof(DeviceList), "devices.result")]
[JsonDerivedType(typeof(ConnectRequest), "session.connect")]
[JsonDerivedType(typeof(SessionOpened), "session.opened")]
[JsonDerivedType(typeof(ConnectFailed), "session.failed")]
[JsonDerivedType(typeof(CloseSession), "session.close")]
[JsonDerivedType(typeof(SessionClosed), "session.closed")]
public abstract record ControlMessage;

public sealed record ServerHello(byte[] Challenge, int ProtocolVersion) : ControlMessage;
public sealed record AuthResponse(byte[] PublicKey, byte[] Signature) : ControlMessage;
public sealed record NotPaired : ControlMessage;
public sealed record Authenticated(string DeviceId, string Name) : ControlMessage;
public sealed record PairRequest(string Code, string Name, bool CanHost) : ControlMessage;
public sealed record PairResult(bool Ok, string? Error) : ControlMessage;
public sealed record ListDevices : ControlMessage;
public sealed record DeviceInfo(string DeviceId, string Name, bool CanHost, bool Online);
public sealed record DeviceList(DeviceInfo[] Devices) : ControlMessage;
public sealed record ConnectRequest(string TargetDeviceId) : ControlMessage;
public sealed record SessionOpened(Guid SessionId, string PeerDeviceId, bool IsInitiator) : ControlMessage;
public sealed record ConnectFailed(string Reason) : ControlMessage;
public sealed record CloseSession(Guid SessionId) : ControlMessage;
public sealed record SessionClosed(Guid SessionId, string Reason) : ControlMessage;

[JsonSerializable(typeof(ControlMessage))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;

public static class ControlCodec
{
    public static byte[] Serialize(ControlMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ControlJsonContext.Default.ControlMessage);

    /// <exception cref="FormatException">Malformed or unknown message.</exception>
    public static ControlMessage Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, ControlJsonContext.Default.ControlMessage)
                   ?? throw new FormatException("Empty message.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Malformed control message.", ex);
        }
    }
}

/// <summary>Binary relay frame: sessionId (16 bytes, big-endian GUID) followed by opaque payload.</summary>
public static class RelayFrame
{
    public const int HeaderLength = 16;

    public static byte[] Build(Guid sessionId, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderLength + payload.Length];
        sessionId.TryWriteBytes(frame, bigEndian: true, out _);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> frame, out Guid sessionId)
    {
        if (frame.Length < HeaderLength) { sessionId = default; return false; }
        sessionId = new Guid(frame[..HeaderLength], bigEndian: true);
        return true;
    }
}
