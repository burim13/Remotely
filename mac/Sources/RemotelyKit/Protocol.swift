import Foundation

public enum ProtocolInfo {
    public static let version = 1
    public static let authLabel = "RMT1-AUTH"
    public static let maxControlBytes = 64 * 1024
    public static let maxRelayBytes = 1024 * 1024
}

public struct DeviceInfo: Sendable {
    public let deviceId: String
    public let name: String
    public let canHost: Bool
    public let online: Bool
}

/// Messages from the coordinator. The wire format is the .NET JSON (PascalCase property names, discriminator "t").
public enum ControlMessage {
    case serverHello(challenge: [UInt8], version: Int)
    case notPaired
    case authenticated(deviceId: String, name: String)
    case pairResult(ok: Bool, error: String?)
    case deviceList([DeviceInfo])
    case sessionOpened(id: UUID, peer: String, isInitiator: Bool)
    case connectFailed(String)
    case sessionClosed(id: UUID, reason: String)

    public static func parse(_ data: Data) throws -> ControlMessage {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let type = object["t"] as? String else {
            throw RemotelyError.malformed("Malformed control message.")
        }
        func string(_ key: String) throws -> String {
            guard let v = object[key] as? String else { throw RemotelyError.malformed("Missing \(key).") }
            return v
        }
        switch type {
        case "server.hello":
            guard let challenge = Data(base64Encoded: try string("Challenge")), let version = object["ProtocolVersion"] as? Int else { throw RemotelyError.malformed("Bad server hello.") }
            return .serverHello(challenge: [UInt8](challenge), version: version)
        case "auth.not-paired": return .notPaired
        case "auth.ok": return .authenticated(deviceId: try string("DeviceId"), name: try string("Name"))
        case "pair.result": return .pairResult(ok: (object["Ok"] as? Bool) ?? false, error: object["Error"] as? String)
        case "devices.result":
            let list = (object["Devices"] as? [[String: Any]]) ?? []
            return .deviceList(list.compactMap { d in
                guard let id = d["DeviceId"] as? String, let name = d["Name"] as? String else { return nil }
                return DeviceInfo(deviceId: id, name: name, canHost: (d["CanHost"] as? Bool) ?? false, online: (d["Online"] as? Bool) ?? false)
            })
        case "session.opened":
            guard let id = UUID(uuidString: try string("SessionId")) else { throw RemotelyError.malformed("Bad session id.") }
            return .sessionOpened(id: id, peer: try string("PeerDeviceId"), isInitiator: (object["IsInitiator"] as? Bool) ?? false)
        case "session.failed": return .connectFailed(try string("Reason"))
        case "session.closed":
            guard let id = UUID(uuidString: try string("SessionId")) else { throw RemotelyError.malformed("Bad session id.") }
            return .sessionClosed(id: id, reason: (object["Reason"] as? String) ?? "")
        default: throw RemotelyError.malformed("Unknown message type.")
        }
    }
}

/// Builds the messages the client sends. Written by hand, not with JSONEncoder: the .NET deserializer requires "t" to be the FIRST property,
/// and JSONEncoder does not control key order.
enum ControlBuilder {
    private static func quote(_ s: String) -> String {
        let data = (try? JSONEncoder().encode(s)) ?? Data("\"\"".utf8)
        return String(decoding: data, as: UTF8.self)
    }

    static func authResponse(publicKey: [UInt8], signature: [UInt8]) -> String {
        "{\"t\":\"auth.response\",\"PublicKey\":\(quote(Data(publicKey).base64EncodedString())),\"Signature\":\(quote(Data(signature).base64EncodedString()))}"
    }
    static func pairRequest(code: String, name: String, canHost: Bool) -> String {
        "{\"t\":\"pair.request\",\"Code\":\(quote(code)),\"Name\":\(quote(name)),\"CanHost\":\(canHost ? "true" : "false")}"
    }
    static let listDevices = "{\"t\":\"devices.list\"}"
    static func connect(target: String) -> String { "{\"t\":\"session.connect\",\"TargetDeviceId\":\(quote(target))}" }
    static func closeSession(_ id: UUID) -> String { "{\"t\":\"session.close\",\"SessionId\":\(quote(id.uuidString.lowercased()))}" }
}

/// Binary relay frame: sessionId (16 bytes, big-endian GUID bytes, i.e. the RFC 4122 order UUID uses) followed by the opaque payload.
enum RelayFrame {
    static let headerLength = 16

    static func build(_ id: UUID, _ payload: [UInt8]) -> [UInt8] {
        var frame = withUnsafeBytes(of: id.uuid) { Array($0) }
        frame.append(contentsOf: payload)
        return frame
    }

    static func parse(_ frame: [UInt8]) -> (id: UUID, payload: [UInt8])? {
        guard frame.count >= headerLength else { return nil }
        let u = frame
        let uuid = UUID(uuid: (u[0], u[1], u[2], u[3], u[4], u[5], u[6], u[7], u[8], u[9], u[10], u[11], u[12], u[13], u[14], u[15]))
        return (uuid, Array(frame[headerLength...]))
    }
}
