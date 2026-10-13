import Foundation
import CryptoKit
import Security

public struct CoordinatorOptions {
    public var serverURL: URL
    /// Lowercase hex SHA-256 of the server certificate's public key (SubjectPublicKeyInfo), printed by the server at start-up. Required for wss.
    public var pinnedServerKey: String
    public var allowInsecureTransport: Bool
    public var requestTimeout: TimeInterval = 15

    public init(serverURL: URL, pinnedServerKey: String, allowInsecureTransport: Bool = false) {
        self.serverURL = serverURL
        self.pinnedServerKey = pinnedServerKey
        self.allowInsecureTransport = allowInsecureTransport
    }
}

/// A relayed byte pipe to a peer device. The coordinator sees sizes and timing but, with SecureSession on top, not content.
public final class RelaySession: @unchecked Sendable {
    public let id: UUID
    public let peerDeviceId: String
    public let isInitiator: Bool
    private let stream: AsyncStream<[UInt8]>
    private let continuation: AsyncStream<[UInt8]>.Continuation
    private var iterator: AsyncStream<[UInt8]>.Iterator
    private let iteratorLock = NSLock()
    private weak var owner: CoordinatorClient?

    init(id: UUID, peer: String, isInitiator: Bool, owner: CoordinatorClient) {
        self.id = id
        self.peerDeviceId = peer
        self.isInitiator = isInitiator
        self.owner = owner
        var c: AsyncStream<[UInt8]>.Continuation!
        let s = AsyncStream<[UInt8]>(bufferingPolicy: .unbounded) { c = $0 }
        stream = s
        continuation = c
        iterator = s.makeAsyncIterator()
    }

    public func send(_ payload: [UInt8]) async throws {
        guard let owner else { throw RemotelyError.coordinator("The connection to the server is closed.") }
        try await owner.sendRelay(id, payload)
    }

    /// Next payload from the peer, or nil once the session has ended. One consumer at a time.
    public func receive() async -> [UInt8]? {
        iteratorLock.lock()
        var local = iterator
        iteratorLock.unlock()
        let value = await local.next()
        iteratorLock.lock()
        iterator = local
        iteratorLock.unlock()
        return value
    }

    public func close() async { await owner?.closeSession(id) }

    func deliver(_ payload: [UInt8]) { continuation.yield(payload) }
    func finish() { continuation.finish() }
}

private actor Mailbox {
    private struct Waiter {
        let id: UUID
        let check: (ControlMessage) -> Bool
        let continuation: CheckedContinuation<ControlMessage, Error>
    }

    private var queue: [ControlMessage] = []
    private var waiters: [Waiter] = []
    private var failure: Error?

    func push(_ m: ControlMessage) {
        if let i = waiters.firstIndex(where: { $0.check(m) }) {
            waiters.remove(at: i).continuation.resume(returning: m)
        } else {
            queue.append(m)
        }
    }

    func fail(_ error: Error) {
        failure = error
        for w in waiters { w.continuation.resume(throwing: error) }
        waiters.removeAll()
    }

    /// Waits for a matching message. Cancellation-aware: a timed-out request must not leave a waiter that never returns.
    func next(where check: @escaping (ControlMessage) -> Bool) async throws -> ControlMessage {
        if let i = queue.firstIndex(where: check) { return queue.remove(at: i) }
        if let failure { throw failure }
        let id = UUID()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<ControlMessage, Error>) in
                waiters.append(Waiter(id: id, check: check, continuation: continuation))
            }
        } onCancel: {
            Task { await self.cancelWaiter(id) }
        }
    }

    private func cancelWaiter(_ id: UUID) {
        if let i = waiters.firstIndex(where: { $0.id == id }) {
            waiters.remove(at: i).continuation.resume(throwing: CancellationError())
        }
    }
}

/// Connection from this device to the coordinator: authenticates with the device key, pairs, lists devices, opens relayed sessions.
public final class CoordinatorClient: NSObject, URLSessionDelegate, @unchecked Sendable {
    private let identity: DeviceIdentity
    private let options: CoordinatorOptions
    private var urlSession: URLSession!
    private var task: URLSessionWebSocketTask!
    private let mailbox = Mailbox()
    private let lock = NSLock()
    private var sessions: [UUID: RelaySession] = [:]
    private var incomingContinuation: AsyncStream<RelaySession>.Continuation!
    private var receiveTask: Task<Void, Never>?
    public private(set) var isPaired = false
    private var closed = false

    /// Sessions other devices have opened to this device (host side).
    public let incomingSessions: AsyncStream<RelaySession>

    private init(identity: DeviceIdentity, options: CoordinatorOptions) {
        self.identity = identity
        self.options = options
        var c: AsyncStream<RelaySession>.Continuation!
        incomingSessions = AsyncStream(bufferingPolicy: .unbounded) { c = $0 }
        incomingContinuation = c
        super.init()
    }

    public var isConnected: Bool { isPaired && !closed && task.state == .running }

    public static func connect(options: CoordinatorOptions, identity: DeviceIdentity) async throws -> CoordinatorClient {
        let secure = options.serverURL.scheme == "wss"
        if !secure && !(options.allowInsecureTransport && options.serverURL.scheme == "ws") {
            throw RemotelyError.coordinator("Refusing an unencrypted connection to the coordinator.")
        }
        if secure && options.pinnedServerKey.trimmingCharacters(in: .whitespaces).isEmpty {
            throw RemotelyError.coordinator("A pinned server key is required for wss.")
        }
        let client = CoordinatorClient(identity: identity, options: options)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        client.urlSession = URLSession(configuration: configuration, delegate: client, delegateQueue: nil)
        client.task = client.urlSession.webSocketTask(with: options.serverURL)
        client.task.maximumMessageSize = ProtocolInfo.maxRelayBytes * 2
        client.task.resume()
        do {
            try await client.handshake()
        } catch {
            client.shutdown()
            throw error
        }
        if client.isPaired { client.startReceiving() }   // an unpaired client reads the pairing replies itself (pair() starts the pump afterwards)
        return client
    }

    // Server key pinning: accept the TLS connection only if the SHA-256 of the server certificate's public key (SubjectPublicKeyInfo) matches.
    public func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                           completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust, let trust = challenge.protectionSpace.serverTrust else {
            completionHandler(.performDefaultHandling, nil)
            return
        }
        guard let pin = Self.pin(of: trust), pin == options.pinnedServerKey.lowercased().trimmingCharacters(in: .whitespaces) else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }

    /// The server uses an ECDSA P-256 certificate. CryptoKit/Security give the raw EC point; the pin is over the DER SubjectPublicKeyInfo, so the fixed P-256 header is added.
    static func pin(of trust: SecTrust) -> String? {
        guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate], let leaf = chain.first,
              let key = SecCertificateCopyKey(leaf), let raw = SecKeyCopyExternalRepresentation(key, nil) as Data? else { return nil }
        let p256Header: [UInt8] = [0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00]
        guard raw.count == 65 else { return nil } // uncompressed P-256 point
        return SHA256.hash(data: p256Header + [UInt8](raw)).map { String(format: "%02x", $0) }.joined()
    }

    private func handshake() async throws {
        let first = try await receiveOne()
        guard case .serverHello(let challenge, let version) = first else { throw RemotelyError.coordinator("Unexpected reply from server (expected the hello, got \(first)).") }
        guard version == ProtocolInfo.version else { throw RemotelyError.coordinator("Server speaks a different protocol version.") }
        var signed = FieldWriter()
        signed.field(Array(ProtocolInfo.authLabel.utf8))
        signed.field(challenge)
        try await sendControl(ControlBuilder.authResponse(publicKey: identity.publicKey, signature: try identity.sign(signed.bytes)))
        let second = try await receiveOne()
        switch second {
        case .authenticated: isPaired = true
        case .notPaired: isPaired = false
        default: throw RemotelyError.coordinator("Unexpected reply from server (expected the sign-in result, got \(second)).")
        }
    }

    private func receiveOne() async throws -> ControlMessage {
        do {
            switch try await task.receive() {
            case .string(let s): return try ControlMessage.parse(Data(s.utf8))
            case .data(let d): return try ControlMessage.parse(d)
            @unknown default: throw RemotelyError.coordinator("Unexpected frame from server.")
            }
        } catch let error as RemotelyError { throw error }
        catch { throw RemotelyError.coordinator("Could not reach the server: \(error.localizedDescription)") }
    }

    /// Registers this device using a one-time code from the server's admin API.
    public func pair(code: String, name: String, canHost: Bool) async throws {
        if isPaired { return }
        try await sendControl(ControlBuilder.pairRequest(code: code, name: name, canHost: canHost))
        let result = try await receiveOne()
        guard case .pairResult(let ok, let error) = result else { throw RemotelyError.coordinator("Unexpected reply from server (expected the pairing result, got \(result)).") }
        guard ok else { throw RemotelyError.coordinator(error ?? "Pairing refused.") }
        let confirm = try await receiveOne()
        guard case .authenticated = confirm else { throw RemotelyError.coordinator("Unexpected reply from server (expected the sign-in, got \(confirm)).") }
        isPaired = true
        startReceiving()
    }

    private func startReceiving() {
        guard receiveTask == nil else { return }
        receiveTask = Task { [weak self] in await self?.pump() }
    }

    private func pump() async {
        do {
            while !Task.isCancelled {
                switch try await task.receive() {
                case .data(let d): handleBinary([UInt8](d))
                case .string(let s): try await handleControl(try ControlMessage.parse(Data(s.utf8)))
                @unknown default: break
                }
            }
        } catch {
            await mailbox.fail(RemotelyError.coordinator("The connection to the server ended."))
        }
        closed = true
        lock.lock()
        let all = Array(sessions.values)
        sessions.removeAll()
        lock.unlock()
        for s in all { s.finish() }
        incomingContinuation.finish()
    }

    private func handleBinary(_ frame: [UInt8]) {
        guard let (id, payload) = RelayFrame.parse(frame) else { return }
        lock.lock()
        let session = sessions[id]
        lock.unlock()
        session?.deliver(payload)
    }

    private func handleControl(_ message: ControlMessage) async throws {
        switch message {
        case .sessionOpened(let id, let peer, let isInitiator):
            let session = RelaySession(id: id, peer: peer, isInitiator: isInitiator, owner: self)
            lock.lock()
            sessions[id] = session   // registered before anything else can arrive on this socket
            lock.unlock()
            if isInitiator { await mailbox.push(message) } else { incomingContinuation.yield(session) }
        case .sessionClosed(let id, _):
            lock.lock()
            let ended = sessions.removeValue(forKey: id)
            lock.unlock()
            ended?.finish()
        default:
            await mailbox.push(message)
        }
    }

    private func request(_ text: String, expecting check: @escaping (ControlMessage) -> Bool) async throws -> ControlMessage {
        guard isPaired else { throw RemotelyError.coordinator("This device is not paired with the server.") }
        try await sendControl(text)
        return try await withThrowingTaskGroup(of: ControlMessage.self) { group in
            group.addTask { try await self.mailbox.next(where: check) }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(self.options.requestTimeout * 1_000_000_000))
                throw RemotelyError.coordinator("The server did not answer in time.")
            }
            defer { group.cancelAll() }
            return try await group.next()!
        }
    }

    public func listDevices() async throws -> [DeviceInfo] {
        let reply = try await request(ControlBuilder.listDevices) { if case .deviceList = $0 { return true } else { return false } }
        if case .deviceList(let list) = reply { return list }
        return []
    }

    /// Opens a relayed session to a host. Run SecureSession.initiate on it next.
    public func connectTo(hostDeviceId: String) async throws -> RelaySession {
        let reply = try await request(ControlBuilder.connect(target: hostDeviceId)) {
            switch $0 {
            case .sessionOpened(_, _, let initiator): return initiator
            case .connectFailed: return true
            default: return false
            }
        }
        switch reply {
        case .connectFailed(let reason): throw RemotelyError.coordinator(reason)
        case .sessionOpened(let id, _, _):
            lock.lock()
            let session = sessions[id]
            lock.unlock()
            guard let session else { throw RemotelyError.coordinator("Session closed before it started.") }
            return session
        default: throw RemotelyError.coordinator("Unexpected reply from server.")
        }
    }

    func sendRelay(_ id: UUID, _ payload: [UInt8]) async throws {
        guard payload.count <= ProtocolInfo.maxRelayBytes else { throw RemotelyError.io("Payload too large; split it.") }
        do { try await task.send(.data(Data(RelayFrame.build(id, payload)))) }
        catch { throw RemotelyError.coordinator("Could not send to the server: \(error.localizedDescription)") }
    }

    func closeSession(_ id: UUID) async {
        lock.lock()
        let session = sessions.removeValue(forKey: id)
        lock.unlock()
        session?.finish()
        try? await sendControl(ControlBuilder.closeSession(id))
    }

    private func sendControl(_ text: String) async throws {
        do { try await task.send(.string(text)) }
        catch { throw RemotelyError.coordinator("Could not send to the server: \(error.localizedDescription)") }
    }

    public func shutdown() {
        closed = true
        receiveTask?.cancel()
        task?.cancel(with: .normalClosure, reason: nil)
        urlSession?.invalidateAndCancel()
        incomingContinuation.finish()
    }
}

/// An end-to-end encrypted, mutually authenticated session layered over a relay.
public final class SecureSession: @unchecked Sendable {
    private let relay: RelaySession
    private let channel: SecureChannel
    public let peerDeviceId: String
    private let sendLock = AsyncLock()

    private init(relay: RelaySession, channel: SecureChannel, peer: String) {
        self.relay = relay
        self.channel = channel
        self.peerDeviceId = peer
    }

    /// Viewer side: proves its identity and verifies the host is the device it asked for.
    public static func initiate(relay: RelaySession, identity: DeviceIdentity) async throws -> SecureSession {
        let handshake = InitiatorHandshake(me: identity, expectedResponderId: relay.peerDeviceId)
        try await relay.send(try handshake.createHello1())
        guard let hello2 = await relay.receive() else { throw RemotelyError.handshake("Session ended during handshake.") }
        return SecureSession(relay: relay, channel: try handshake.processHello2(hello2), peer: relay.peerDeviceId)
    }

    /// Host side: `isTrusted` decides whether this viewer may connect.
    public static func accept(relay: RelaySession, identity: DeviceIdentity, isTrusted: (String) -> Bool) async throws -> SecureSession {
        guard let hello1 = await relay.receive() else { throw RemotelyError.handshake("Session ended during handshake.") }
        let accepted = try ResponderHandshake(me: identity).processHello1(hello1, isTrusted: isTrusted)
        guard accepted.initiatorId == relay.peerDeviceId else { throw RemotelyError.handshake("Initiator identity does not match the session.") }
        try await relay.send(accepted.hello2)
        return SecureSession(relay: relay, channel: accepted.channel, peer: accepted.initiatorId)
    }

    /// Seal and send are one step under a lock: frames must reach the wire in counter order or the peer rejects them.
    public func send(_ plaintext: [UInt8]) async throws {
        await sendLock.lock()
        do {
            try await relay.send(try channel.seal(plaintext))
            await sendLock.unlock()
        } catch {
            await sendLock.unlock()
            throw error
        }
    }

    /// Next decrypted message, or nil if the session ended. Throws if the relay tampers with traffic.
    public func receive() async throws -> [UInt8]? {
        guard let frame = await relay.receive() else { return nil }
        return try channel.open(frame)
    }

    public func close() async { await relay.close() }
}

/// A minimal async mutex (FIFO) so concurrent senders keep their order.
actor AsyncLock {
    private var locked = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func lock() async {
        if !locked { locked = true; return }
        await withCheckedContinuation { waiters.append($0) }
    }

    func unlock() {
        if waiters.isEmpty { locked = false } else { waiters.removeFirst().resume() }
    }
}
