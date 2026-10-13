import Foundation
import CryptoKit

public protocol FrameSource: Sendable { func capture() async throws -> FrameInfo }
public protocol InputSink: Sendable { func inject(_ events: [InputEvent]) async throws }
public protocol ClipboardSync: Sendable {
    func read() async throws -> (sequence: Int64, text: String?)
    /// Replaces the clipboard text; returns the sequence number after the write so the caller does not echo it back.
    func write(_ text: String) async throws -> Int64
}

/// Everything a host offers. Anything left nil is simply not offered to viewers.
public struct HostServices: @unchecked Sendable {
    public var frames: FrameSource
    public var input: InputSink?
    public var clipboard: ClipboardSync?
    public var receiveDirectory: URL?
    public var shareDirectory: URL?
    public var isTrusted: (String) -> Bool
    public var canControl: (String) -> Bool

    public init(frames: FrameSource, input: InputSink? = nil, clipboard: ClipboardSync? = nil, receiveDirectory: URL? = nil, shareDirectory: URL? = nil,
                isTrusted: @escaping (String) -> Bool, canControl: @escaping (String) -> Bool = { _ in false }) {
        self.frames = frames; self.input = input; self.clipboard = clipboard
        self.receiveDirectory = receiveDirectory; self.shareDirectory = shareDirectory
        self.isTrusted = isTrusted; self.canControl = canControl
    }
}

/// The host's side of a coordinator connection: accepts sessions that viewers open, authenticates each viewer end to end, then serves frames,
/// input, clipboard and files. Viewers that are trusted but not allowed to control get frames only. Port of the .NET HostAgent.
public final class HostAgent: @unchecked Sendable {
    private static let maxConcurrentSessions = 4
    private let identity: DeviceIdentity
    private let services: HostServices
    private let log: @Sendable (String) -> Void
    private let lock = NSLock()
    private var active = 0

    public init(identity: DeviceIdentity, services: HostServices, log: @escaping @Sendable (String) -> Void) {
        self.identity = identity
        self.services = services
        self.log = log
    }

    /// Runs until the coordinator connection ends.
    public func run(client: CoordinatorClient) async {
        await withTaskGroup(of: Void.self) { group in
            for await relay in client.incomingSessions {
                lock.lock()
                active += 1
                let tooMany = active > Self.maxConcurrentSessions
                if tooMany { active -= 1 }
                lock.unlock()
                if tooMany {
                    log("Refused a session from \(DeviceIds.short(relay.peerDeviceId)): too many sessions.")
                    await relay.close()
                    continue
                }
                group.addTask { await self.serve(relay) }
            }
        }
    }

    private final class Session: @unchecked Sendable {
        let secure: SecureSession
        let viewerId: String
        let control: Bool
        var lastFrame: [UInt8]?
        var clipboardSequence: Int64 = -1
        var upload: FileSink?
        var uploadId: UInt32 = 0
        var download: Task<Void, Never>?
        var downloadActive = false
        init(secure: SecureSession, viewerId: String, control: Bool) { self.secure = secure; self.viewerId = viewerId; self.control = control }
    }

    private func serve(_ relay: RelaySession) async {
        defer { lock.lock(); active -= 1; lock.unlock() }
        let viewer = DeviceIds.short(relay.peerDeviceId)
        do {
            let secure = try await SecureSession.accept(relay: relay, identity: identity, isTrusted: services.isTrusted)
            let session = Session(secure: secure, viewerId: relay.peerDeviceId, control: services.canControl(relay.peerDeviceId))
            log("Viewer \(viewer) connected (\(session.control ? "control" : "view only")).")
            defer { session.download?.cancel(); session.upload?.abort() }
            try await secure.send(HelloInfo(version: HostMessages.protocolVersion, capabilities: capabilities(session)).encode())
            while let message = try await secure.receive() {
                await handle(session, message)
            }
            log("Viewer \(viewer) left.")
        } catch let error as RemotelyError {
            switch error {
            case .handshake(let m): log("Refused viewer \(relay.peerDeviceId): \(m)")
            default: log("Viewer \(viewer): \(error)")
            }
            await relay.close()
        } catch {
            log("Viewer \(viewer): \(error)")
            await relay.close()
        }
    }

    private func capabilities(_ s: Session) -> HostCapabilities {
        guard s.control else { return [] }
        var caps: HostCapabilities = []
        if services.input != nil { caps.insert(.control) }
        if services.clipboard != nil { caps.insert(.clipboard) }
        if services.receiveDirectory != nil || services.shareDirectory != nil { caps.insert(.files) }
        return caps
    }

    private func handle(_ s: Session, _ message: [UInt8]) async {
        guard let kind = message.first else { return }
        do {
            switch kind {
            case HostMessages.frameRequest:
                try await sendFrame(s)
            case HostMessages.input where s.control && services.input != nil:
                if let events = InputCodec.decode(message), !events.isEmpty { try await services.input!.inject(events) }
                else { try await sendError(s, "Bad input message.") }
            case HostMessages.clipboard where s.control && services.clipboard != nil:
                if message.count - 1 <= HostMessages.maxClipboardChars * 4 {
                    s.clipboardSequence = try await services.clipboard!.write(String(decoding: message.dropFirst(), as: UTF8.self))
                }
            case HostMessages.fileStart, HostMessages.fileChunk, HostMessages.fileEnd:
                if s.control, let dir = services.receiveDirectory { try await handleUpload(s, message, dir) }
                else { try await sendError(s, "That request is not available.") }
            case HostMessages.shareList where s.control && services.shareDirectory != nil:
                let list = ShareDirectory.list(services.shareDirectory!)
                let json = (try? JSONEncoder().encode(list)) ?? Data("[]".utf8)
                try await s.secure.send(HostMessages.prefixed(HostMessages.shareListReply, [UInt8](json)))
            case HostMessages.shareGet where s.control && services.shareDirectory != nil:
                startDownload(s, message)
            default:
                try await sendError(s, "That request is not available.")
            }
        } catch {
            log("Request \(String(format: "%02X", kind)) failed: \(error)")
            try? await sendError(s, "\(error)")
        }
    }

    private func sendFrame(_ s: Session) async throws {
        if s.control, let clipboard = services.clipboard {
            do {
                let (sequence, text) = try await clipboard.read()
                if sequence != s.clipboardSequence {
                    s.clipboardSequence = sequence
                    if let text, !text.isEmpty, text.count <= HostMessages.maxClipboardChars {
                        try await s.secure.send(HostMessages.prefixed(HostMessages.clipboardReply, Array(text.utf8)))
                    }
                }
            } catch { log("Clipboard read failed: \(error)") }
        }

        let frame: FrameInfo
        do { frame = try await services.frames.capture() }
        catch {
            log("Capture failed: \(error)")
            try await sendError(s, "Capture failed: \(error)")
            return
        }
        let fingerprint = Array(SHA256.hash(data: frame.jpeg + Array(frame.desktop.utf8)))
        if let last = s.lastFrame, last == fingerprint {
            try await s.secure.send([HostMessages.unchanged])
            return
        }
        let body = FrameCodec.encode(frame)
        // One relayed message is capped; leave room for the encryption overhead and the kind byte.
        guard body.count + SecureChannel.overhead + 1 <= ProtocolInfo.maxRelayBytes else {
            try await sendError(s, "Frame too large to send.")
            return
        }
        s.lastFrame = fingerprint
        try await s.secure.send(HostMessages.prefixed(HostMessages.frameReply, body))
    }

    private func handleUpload(_ s: Session, _ message: [UInt8], _ directory: URL) async throws {
        guard let (id, rest) = HostMessages.readId(message) else { try await sendError(s, "Bad file message."); return }
        switch message[0] {
        case HostMessages.fileStart:
            s.upload?.abort()
            s.upload = nil
            do {
                guard rest.count >= 8 + 32 + 2 else { throw RemotelyError.io("Bad file header.") }
                let size = Int64(bitPattern: rest.readUInt64BE(at: 0))
                let hash = Array(rest[8..<40])
                let nameLength = Int(rest.readUInt16BE(at: 40))
                guard rest.count == 42 + nameLength else { throw RemotelyError.io("Bad file header.") }
                s.upload = try FileSink.create(directory: directory, requestedName: String(decoding: rest[42..<(42 + nameLength)], as: UTF8.self), size: size, sha256: hash)
                s.uploadId = id
            } catch { try await ack(s, id, ok: false, "\(error)") }
        case HostMessages.fileChunk where s.upload != nil && s.uploadId == id:
            do { try s.upload!.write(rest) }
            catch { s.upload?.abort(); s.upload = nil; try await ack(s, id, ok: false, "\(error)") }
        case HostMessages.fileEnd where s.upload != nil && s.uploadId == id:
            let sink = s.upload!
            s.upload = nil
            do {
                let path = try sink.complete()
                log("Received file '\((path as NSString).lastPathComponent)' from \(DeviceIds.short(s.viewerId)).")
                try await ack(s, id, ok: true, (path as NSString).lastPathComponent)
            } catch { sink.abort(); try await ack(s, id, ok: false, "\(error)") }
        default: break
        }
    }

    private func startDownload(_ s: Session, _ message: [UInt8]) {
        if s.downloadActive {
            Task { try? await self.sendError(s, "A download is already running.") }
            return
        }
        s.downloadActive = true
        s.download = Task { [self] in
            defer { s.downloadActive = false }
            var id: UInt32 = 0
            do {
                guard let (rid, rest) = HostMessages.readId(message), rest.count >= 2 else { throw RemotelyError.io("Bad request.") }
                id = rid
                let nameLength = Int(rest.readUInt16BE(at: 0))
                guard rest.count == 2 + nameLength else { throw RemotelyError.io("Bad request.") }
                guard let url = ShareDirectory.resolve(services.shareDirectory!, name: String(decoding: rest[2..<(2 + nameLength)], as: UTF8.self)) else {
                    throw RemotelyError.io("That file is not shared.")
                }
                let handle = try FileHandle(forReadingFrom: url)
                defer { try? handle.close() }
                let size = try handle.seekToEnd()
                try handle.seek(toOffset: 0)
                var hasher = SHA256()
                while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
                try handle.seek(toOffset: 0)
                var header = [UInt8]()
                header.appendUInt64BE(size)
                header.append(contentsOf: hasher.finalize())
                try await s.secure.send(HostMessages.withId(HostMessages.shareFileStart, id, header))
                while let chunk = try handle.read(upToCount: HostMessages.fileChunkBytes), !chunk.isEmpty {
                    try Task.checkCancellation()
                    try await s.secure.send(HostMessages.withId(HostMessages.shareFileChunk, id, [UInt8](chunk)))
                }
                try await s.secure.send(HostMessages.withId(HostMessages.shareFileEnd, id, []))
            } catch is CancellationError {
            } catch { try? await ack(s, id, ok: false, "\(error)") }
        }
    }

    private func ack(_ s: Session, _ id: UInt32, ok: Bool, _ message: String) async throws {
        let text = Array(message.utf8.prefix(200))
        var payload: [UInt8] = [ok ? 1 : 0]
        payload.appendUInt16BE(UInt16(text.count))
        payload.append(contentsOf: text)
        try await s.secure.send(HostMessages.withId(HostMessages.fileAck, id, payload))
    }

    private func sendError(_ s: Session, _ message: String) async throws {
        try await s.secure.send(HostMessages.prefixed(HostMessages.error, Array(message.utf8)))
    }
}
