import Foundation
import CryptoKit

/// Everything a new device needs to join your server, as one pasteable string: address, server key pin and a pairing code.
/// Layout before base64url: version byte, 32-byte pin (zeros when TLS is off), 8 code characters, the address (UTF-8), then the first 3 bytes of
/// SHA-256 over everything before it. Byte-for-byte the same as the .NET `Invite` class (a known string is tested on both sides).
public struct Invite: Equatable, Sendable {
    public static let prefix = "RMT1-"
    public var serverURI: String
    public var pinHex: String      // "" when TLS is off
    public var code: String        // "ABCD-EFGH"

    public init(serverURI: String, pinHex: String, code: String) {
        self.serverURI = serverURI; self.pinHex = pinHex; self.code = code
    }

    public func encode() throws -> String {
        let normalized = String(code.filter { $0.isLetter || $0.isNumber }).uppercased()
        guard normalized.utf8.count == 8 else { throw RemotelyError.malformed("The pairing code must have 8 characters.") }
        let pin: [UInt8] = pinHex.isEmpty ? [UInt8](repeating: 0, count: 32) : ([UInt8](hex: pinHex) ?? [])
        guard pin.count == 32 else { throw RemotelyError.malformed("The key pin must be 64 hex characters.") }
        var body: [UInt8] = [1]
        body += pin
        body += Array(normalized.utf8)
        body += Array(serverURI.utf8)
        let checksum = Array(SHA256.hash(data: body).prefix(3))
        let text = Data(body + checksum).base64EncodedString().replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
        return Invite.prefix + text
    }

    /// Reads a pasted invite; whitespace and line breaks are ignored. Throws an error whose description is a plain-language message.
    public static func parse(_ text: String) throws -> Invite {
        let compact = String(text.filter { !$0.isWhitespace })
        if compact.isEmpty { throw RemotelyError.malformed("Paste the invite from your server.") }
        guard compact.uppercased().hasPrefix(prefix) else { throw RemotelyError.malformed("That is not a Remotely invite. It should start with RMT1-.") }
        var b64 = String(compact.dropFirst(prefix.count)).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        b64 += String(repeating: "=", count: (4 - b64.count % 4) % 4)
        guard let data = Data(base64Encoded: b64), data.count >= 1 + 32 + 8 + 1 + 3 else { throw RemotelyError.malformed("That invite is not complete. Copy all of it.") }
        let all = [UInt8](data)
        let body = Array(all.dropLast(3))
        guard Array(SHA256.hash(data: body).prefix(3)) == Array(all.suffix(3)) else {
            throw RemotelyError.malformed("That invite has a typing mistake or is cut short. Copy it again.")
        }
        guard body[0] == 1 else { throw RemotelyError.malformed("This invite was made by a newer Remotely. Update this app.") }
        let pin = Array(body[1..<33])
        guard let code = String(bytes: body[33..<41], encoding: .ascii), let uri = String(bytes: body[41...], encoding: .utf8),
              let url = URL(string: uri), let scheme = url.scheme, ["wss", "ws"].contains(scheme) else { throw RemotelyError.malformed("That invite is damaged.") }
        let pinHex = pin.allSatisfy { $0 == 0 } ? "" : pin.hex
        return Invite(serverURI: uri, pinHex: pinHex, code: "\(code.prefix(4))-\(code.suffix(4))")
    }
}
