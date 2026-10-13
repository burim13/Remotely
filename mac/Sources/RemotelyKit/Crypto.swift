import Foundation
import CryptoKit

public enum DeviceIds {
    public static func fromPublicKey(_ spki: [UInt8]) -> String {
        SHA256.hash(data: spki).map { String(format: "%02x", $0) }.joined()
    }

    public static func toBytes(_ id: String) throws -> [UInt8] {
        guard id.count == 64, let bytes = [UInt8](hex: id) else { throw RemotelyError.malformed("Device id must be 64 hex characters.") }
        return bytes
    }

    /// First 12 hex characters in groups of four, for people to compare by eye.
    public static func short(_ id: String) -> String {
        id.count < 12 ? id : "\(id.prefix(4))-\(id.dropFirst(4).prefix(4))-\(id.dropFirst(8).prefix(4))"
    }
}

/// A device's long-term ECDSA P-256 identity. The device id is the SHA-256 of the public key (SubjectPublicKeyInfo DER), exactly as on Windows.
public final class DeviceIdentity {
    let key: P256.Signing.PrivateKey
    public let publicKey: [UInt8]
    public let deviceId: String

    public init() {
        let k = P256.Signing.PrivateKey()
        let pub = Array(k.publicKey.derRepresentation)
        key = k
        publicKey = pub
        deviceId = DeviceIds.fromPublicKey(pub)
    }

    public init(privateKey: [UInt8]) throws {
        let k: P256.Signing.PrivateKey
        do { k = try P256.Signing.PrivateKey(derRepresentation: privateKey) }
        catch { throw RemotelyError.crypto("The stored device key is not valid.") }
        let pub = Array(k.publicKey.derRepresentation)
        key = k
        publicKey = pub
        deviceId = DeviceIds.fromPublicKey(pub)
    }

    public func exportPrivateKey() -> [UInt8] { Array(key.derRepresentation) }

    /// ECDSA over SHA-256, 64-byte r||s (IEEE P1363), the format the .NET side uses.
    public func sign(_ data: [UInt8]) throws -> [UInt8] {
        do { return Array(try key.signature(for: data).rawRepresentation) }
        catch { throw RemotelyError.crypto("Signing failed.") }
    }

    public static func verify(publicKey: [UInt8], data: [UInt8], signature: [UInt8]) -> Bool {
        guard let pk = try? P256.Signing.PublicKey(derRepresentation: publicKey),
              let sig = try? P256.Signing.ECDSASignature(rawRepresentation: signature) else { return false }
        return pk.isValidSignature(sig, for: data)
    }
}

/// AES-256-GCM with strictly increasing counters. Frame = counter(8, big-endian) || ciphertext || tag(16).
public final class SecureChannel {
    public static let overhead = 8 + 16
    private static let aad = Array("RMT1-data".utf8)

    private let sendKey: SymmetricKey
    private let receiveKey: SymmetricKey
    private var sendCounter: UInt64 = 0
    private var receiveCounter: UInt64 = 0
    private let sendLock = NSLock()
    private let receiveLock = NSLock()

    init(sendKey: [UInt8], receiveKey: [UInt8]) {
        self.sendKey = SymmetricKey(data: sendKey)
        self.receiveKey = SymmetricKey(data: receiveKey)
    }

    /// For tests that check against known frames.
    public static func forTesting(sendKey: [UInt8], receiveKey: [UInt8]) -> SecureChannel { SecureChannel(sendKey: sendKey, receiveKey: receiveKey) }

    private static func nonce(for counter: UInt64) throws -> AES.GCM.Nonce {
        var bytes = [UInt8](repeating: 0, count: 4)
        bytes.appendUInt64BE(counter)
        return try AES.GCM.Nonce(data: bytes)
    }

    public func seal(_ plaintext: [UInt8]) throws -> [UInt8] {
        sendLock.lock()
        defer { sendLock.unlock() }
        let counter = sendCounter
        sendCounter += 1
        do {
            let box = try AES.GCM.seal(plaintext, using: sendKey, nonce: try Self.nonce(for: counter), authenticating: Self.aad)
            var frame = [UInt8]()
            frame.reserveCapacity(Self.overhead + plaintext.count)
            frame.appendUInt64BE(counter)
            frame.append(contentsOf: box.ciphertext)
            frame.append(contentsOf: box.tag)
            return frame
        } catch { throw RemotelyError.crypto("Encryption failed.") }
    }

    /// Throws on a tampered, replayed, reordered or wrong-key frame.
    public func open(_ frame: [UInt8]) throws -> [UInt8] {
        guard frame.count >= Self.overhead else { throw RemotelyError.crypto("Frame too short.") }
        receiveLock.lock()
        defer { receiveLock.unlock() }
        let counter = frame.readUInt64BE(at: 0)
        guard counter == receiveCounter else { throw RemotelyError.crypto("Unexpected counter (replay, drop or reorder).") }
        let ciphertext = Array(frame[8..<(frame.count - 16)])
        let tag = Array(frame[(frame.count - 16)...])
        do {
            let box = try AES.GCM.SealedBox(nonce: try Self.nonce(for: counter), ciphertext: ciphertext, tag: tag)
            let plain = try AES.GCM.open(box, using: receiveKey, authenticating: Self.aad)
            receiveCounter += 1
            return Array(plain)
        } catch { throw RemotelyError.crypto("Authentication failed (tampered or wrong key).") }
    }
}

enum HandshakeCommon {
    static let magic = Array("RMT1".utf8)
    static let typeHello1: UInt8 = 1
    static let typeHello2: UInt8 = 2
    static let nonceLength = 32

    static func signatureInput(_ label: String, _ fields: [[UInt8]]) -> [UInt8] {
        var w = FieldWriter()
        w.field(Array(label.utf8))
        for f in fields { w.field(f) }
        return w.bytes
    }

    static func sha256(_ parts: [[UInt8]]) -> [UInt8] {
        var h = SHA256()
        for p in parts { h.update(data: p) }
        return Array(h.finalize())
    }

    static func agree(_ mine: P256.KeyAgreement.PrivateKey, peerSpki: [UInt8]) throws -> [UInt8] {
        do {
            let peer = try P256.KeyAgreement.PublicKey(derRepresentation: peerSpki)
            let secret = try mine.sharedSecretFromKeyAgreement(with: peer)
            return secret.withUnsafeBytes { Array($0) }
        } catch { throw RemotelyError.handshake("Key agreement failed.") }
    }

    /// Returns (initiator-to-responder key, responder-to-initiator key). Same HKDF-SHA256 parameters as the .NET side.
    static func deriveKeys(secret: [UInt8], nonceI: [UInt8], nonceR: [UInt8], transcript: [UInt8]) -> (i2r: [UInt8], r2i: [UInt8]) {
        let salt = sha256([nonceI, nonceR])
        var info = FieldWriter()
        info.field(Array("RMT1-keys".utf8))
        info.field(transcript)
        let okm = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: secret), salt: salt, info: info.bytes, outputByteCount: 64)
        let all = okm.withUnsafeBytes { Array($0) }
        return (Array(all[0..<32]), Array(all[32..<64]))
    }

    static func randomNonce() -> [UInt8] { (0..<nonceLength).map { _ in UInt8.random(in: 0...255) } }
}

/// Mutually authenticated ephemeral ECDH (P-256) over any byte transport, including an untrusted relay. See the .NET Handshake.cs for the full description.
public final class InitiatorHandshake {
    private let me: DeviceIdentity
    private let expectedResponderId: String
    private var ephemeral: P256.KeyAgreement.PrivateKey?
    private var nonce: [UInt8] = []
    private var hello1: [UInt8] = []

    public init(me: DeviceIdentity, expectedResponderId: String) {
        self.me = me
        self.expectedResponderId = expectedResponderId
    }

    public func createHello1() throws -> [UInt8] {
        let e = P256.KeyAgreement.PrivateKey()
        ephemeral = e
        let ephPub = Array(e.publicKey.derRepresentation)
        nonce = HandshakeCommon.randomNonce()
        let expected = try DeviceIds.toBytes(expectedResponderId)
        let signature = try me.sign(HandshakeCommon.signatureInput("RMT1-I", [ephPub, nonce, expected]))
        var w = FieldWriter()
        w.raw(HandshakeCommon.magic)
        w.byte(HandshakeCommon.typeHello1)
        w.field(me.publicKey)
        w.field(ephPub)
        w.field(nonce)
        w.field(expected)
        w.field(signature)
        hello1 = w.bytes
        return hello1
    }

    public func processHello2(_ hello2: [UInt8]) throws -> SecureChannel {
        guard let eph = ephemeral else { throw RemotelyError.handshake("Call createHello1 first.") }
        var r = FieldReader(hello2)
        let magic2 = try r.raw(4)
        let type2 = try r.byte()
        guard magic2 == HandshakeCommon.magic, type2 == HandshakeCommon.typeHello2 else { throw RemotelyError.handshake("Not a Hello2 message.") }
        let identity = try r.field()
        let ephemeralPeer = try r.field()
        let nonceR = try r.field()
        let signature = try r.field()
        guard r.atEnd else { throw RemotelyError.handshake("Trailing data.") }
        let body = Array(hello2[0..<(hello2.count - (4 + signature.count))])

        guard DeviceIds.fromPublicKey(identity) == expectedResponderId else { throw RemotelyError.handshake("The device answering is not the one expected.") }
        let signed = HandshakeCommon.signatureInput("RMT1-R", [ephemeralPeer, nonceR, HandshakeCommon.sha256([hello1])])
        guard DeviceIdentity.verify(publicKey: identity, data: signed, signature: signature) else { throw RemotelyError.handshake("Responder signature is invalid.") }

        let secret = try HandshakeCommon.agree(eph, peerSpki: ephemeralPeer)
        let transcript = HandshakeCommon.sha256([hello1, body])
        let keys = HandshakeCommon.deriveKeys(secret: secret, nonceI: nonce, nonceR: nonceR, transcript: transcript)
        return SecureChannel(sendKey: keys.i2r, receiveKey: keys.r2i)
    }
}

public final class ResponderHandshake {
    private let me: DeviceIdentity
    public init(me: DeviceIdentity) { self.me = me }

    public struct Accepted {
        public let hello2: [UInt8]
        public let initiatorId: String
        public let channel: SecureChannel
    }

    /// Verifies Hello1 and returns Hello2 plus the keyed channel. `isTrusted` decides whether the initiator may connect.
    public func processHello1(_ hello1: [UInt8], isTrusted: (String) -> Bool) throws -> Accepted {
        var r = FieldReader(hello1)
        let magic1 = try r.raw(4)
        let type1 = try r.byte()
        guard magic1 == HandshakeCommon.magic, type1 == HandshakeCommon.typeHello1 else { throw RemotelyError.handshake("Not a Hello1 message.") }
        let identity = try r.field()
        let ephemeralPeer = try r.field()
        let nonceI = try r.field()
        let expected = try r.field()
        let signature = try r.field()
        guard r.atEnd else { throw RemotelyError.handshake("Trailing data.") }
        guard nonceI.count == HandshakeCommon.nonceLength else { throw RemotelyError.handshake("Bad nonce.") }
        let myIdBytes = try DeviceIds.toBytes(me.deviceId)
        guard expected == myIdBytes else { throw RemotelyError.handshake("This hello was meant for a different device.") }
        let signed = HandshakeCommon.signatureInput("RMT1-I", [ephemeralPeer, nonceI, expected])
        guard DeviceIdentity.verify(publicKey: identity, data: signed, signature: signature) else { throw RemotelyError.handshake("Initiator signature is invalid.") }
        let initiatorId = DeviceIds.fromPublicKey(identity)
        guard isTrusted(initiatorId) else { throw RemotelyError.handshake("Initiator is not trusted by this device.") }

        let eph = P256.KeyAgreement.PrivateKey()
        let ephPub = Array(eph.publicKey.derRepresentation)
        let nonceR = HandshakeCommon.randomNonce()
        let sig = try me.sign(HandshakeCommon.signatureInput("RMT1-R", [ephPub, nonceR, HandshakeCommon.sha256([hello1])]))
        var body = FieldWriter()
        body.raw(HandshakeCommon.magic)
        body.byte(HandshakeCommon.typeHello2)
        body.field(me.publicKey)
        body.field(ephPub)
        body.field(nonceR)

        let secret = try HandshakeCommon.agree(eph, peerSpki: ephemeralPeer)
        let transcript = HandshakeCommon.sha256([hello1, body.bytes])
        let keys = HandshakeCommon.deriveKeys(secret: secret, nonceI: nonceI, nonceR: nonceR, transcript: transcript)
        var hello2 = body
        hello2.field(sig)
        return Accepted(hello2: hello2.bytes, initiatorId: initiatorId, channel: SecureChannel(sendKey: keys.r2i, receiveKey: keys.i2r))
    }
}
