import Foundation
import CryptoKit

/// A viewer's live session with one host: pulls frames as fast as the host can produce them, sends input, syncs the clipboard and moves files.
/// UI-agnostic: a window (or the CLI) sets the callbacks, then calls `start()`. Callbacks run on background tasks. Port of the .NET ViewerSession.
public final class ViewerSession: @unchecked Sendable {
    private static let minFrameInterval: TimeInterval = 0.033
    private static let unchangedBackoff: TimeInterval = 0.060

    private let secure: SecureSession
    public let hello: HelloInfo
    public let hostDeviceId: String
    private let lock = NSLock()
    private var pendingInput: [InputEvent] = []
    private let inputSignal: AsyncStream<Void>
    private let inputSignalContinuation: AsyncStream<Void>.Continuation
    private var frameWaiter = Promise<Bool>()
    private var acks: [UInt32: Promise<String>] = [:]
    private var downloads: [UInt32: Download] = [:]
    private var listWaiter: Promise<[ShareEntry]>?
    private var nextId: UInt32 = 0
    private var tasks: [Task<Void, Never>] = []
    private var ended = false

    public var downloadDirectory = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Downloads")
    public var onFrame: (@Sendable (FrameInfo) -> Void)?
    public var onClipboard: (@Sendable (String) -> Void)?
    public var onHostError: (@Sendable (String) -> Void)?
    /// Called once when the session ends; the argument is a reason, or nil for a normal close.
    public var onClosed: (@Sendable (String?) -> Void)?

    public var canControl: Bool { hello.capabilities.contains(.control) }
    public var canSendSecureAttention: Bool { hello.capabilities.contains(.secureAttention) }
    public var hasClipboard: Bool { hello.capabilities.contains(.clipboard) }
    public var hasFiles: Bool { hello.capabilities.contains(.files) }

    private final class Download {
        let promise = Promise<String>()
        var sink: FileSink?
        var requestedName = ""
    }

    private init(secure: SecureSession, hello: HelloInfo, hostDeviceId: String) {
        self.secure = secure
        self.hello = hello
        self.hostDeviceId = hostDeviceId
        var c: AsyncStream<Void>.Continuation!
        inputSignal = AsyncStream(bufferingPolicy: .bufferingNewest(1)) { c = $0 }
        inputSignalContinuation = c
    }

    public static func open(client: CoordinatorClient, identity: DeviceIdentity, hostDeviceId: String) async throws -> ViewerSession {
        let relay = try await client.connectTo(hostDeviceId: hostDeviceId)
        do {
            let secure = try await SecureSession.initiate(relay: relay, identity: identity)
            guard let first = try await secure.receive(), let hello = HelloInfo.decode(first) else { throw RemotelyError.handshake("Host did not introduce itself.") }
            guard hello.version == HostMessages.protocolVersion else { throw RemotelyError.handshake("Host speaks a different protocol version.") }
            return ViewerSession(secure: secure, hello: hello, hostDeviceId: hostDeviceId)
        } catch {
            await relay.close()
            throw error
        }
    }

    /// Starts the receive, frame and input loops. Set the callbacks first.
    public func start() {
        tasks = [
            Task { [self] in await receiveLoop() },
            Task { [self] in await frameLoop() },
            Task { [self] in await inputLoop() },
        ]
    }

    // MARK: input

    /// Queues an event. Mouse moves are coalesced so a fast mouse cannot flood the link; everything else keeps its order.
    public func sendInput(_ e: InputEvent) {
        guard canControl else { return }
        lock.lock()
        if e.kind == .mouseMove, let last = pendingInput.last, last.kind == .mouseMove { pendingInput[pendingInput.count - 1] = e }
        else if pendingInput.count < 4096 { pendingInput.append(e) }
        lock.unlock()
        inputSignalContinuation.yield()
    }

    public func releaseAllInput() { sendInput(InputEvent(kind: .releaseAll)) }

    private func inputLoop() async {
        for await _ in inputSignal {
            while true {
                lock.lock()
                let take = min(pendingInput.count, InputCodec.maxEventsPerMessage)
                let batch = Array(pendingInput.prefix(take))
                pendingInput.removeFirst(take)
                lock.unlock()
                if batch.isEmpty { break }
                do { try await secure.send(InputCodec.encode(batch)) }
                catch { await end("\(error)"); return }
                try? await Task.sleep(nanoseconds: 8_000_000)
            }
        }
    }

    public func sendSecureAttention() async throws {
        if canSendSecureAttention { try await secure.send([HostMessages.secureAttention]) }
    }

    // MARK: clipboard

    public func sendClipboard(_ text: String) async throws {
        guard hasClipboard, text.count <= HostMessages.maxClipboardChars else { return }
        try await secure.send(HostMessages.prefixed(HostMessages.clipboard, Array(text.utf8)))
    }

    // MARK: files

    private func newId() -> UInt32 { lock.lock(); defer { lock.unlock() }; nextId += 1; return nextId }

    /// Uploads a file into the host's receive folder. Returns the name it was saved under.
    public func uploadFile(at url: URL, progress: (@Sendable (Double) -> Void)? = nil) async throws -> String {
        guard hasFiles else { throw RemotelyError.io("The host does not accept files.") }
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        let size = (attributes[.size] as? NSNumber)?.int64Value ?? 0
        guard size <= HostMessages.maxFileBytes else { throw RemotelyError.io("That file is too large to send.") }
        var hasher = SHA256()
        let reader = try FileHandle(forReadingFrom: url)
        defer { try? reader.close() }
        while let chunk = try reader.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        try reader.seek(toOffset: 0)

        let id = newId()
        let ack = Promise<String>()
        lock.lock(); acks[id] = ack; lock.unlock()
        defer { lock.lock(); acks[id] = nil; lock.unlock() }

        let name = Array(url.lastPathComponent.utf8)
        var header = [UInt8]()
        header.appendUInt64BE(UInt64(size))
        header.append(contentsOf: hasher.finalize())
        header.appendUInt16BE(UInt16(name.count))
        header.append(contentsOf: name)
        try await secure.send(HostMessages.withId(HostMessages.fileStart, id, header))

        var sent: Int64 = 0
        while let chunk = try reader.read(upToCount: HostMessages.fileChunkBytes), !chunk.isEmpty {
            if ack.isCompleted { break } // host refused or failed: stop sending
            try await secure.send(HostMessages.withId(HostMessages.fileChunk, id, [UInt8](chunk)))
            sent += Int64(chunk.count)
            progress?(size == 0 ? 1 : Double(sent) / Double(size))
        }
        if !ack.isCompleted { try await secure.send(HostMessages.withId(HostMessages.fileEnd, id, [])) }
        return try await ack.value(timeout: 30)
    }

    public func listSharedFiles() async throws -> [ShareEntry] {
        guard hasFiles else { return [] }
        let waiter = Promise<[ShareEntry]>()
        lock.lock(); listWaiter = waiter; lock.unlock()
        try await secure.send([HostMessages.shareList])
        return try await waiter.value(timeout: 15)
    }

    /// Downloads a file from the host's shared folder into `downloadDirectory`. Returns the saved path.
    public func downloadFile(named name: String) async throws -> String {
        guard hasFiles else { throw RemotelyError.io("The host does not share files.") }
        let id = newId()
        let download = Download()
        download.requestedName = name
        lock.lock(); downloads[id] = download; lock.unlock()
        defer { lock.lock(); downloads[id] = nil; lock.unlock(); download.sink?.abort() }
        let nameBytes = Array(name.utf8)
        var payload = [UInt8]()
        payload.appendUInt16BE(UInt16(nameBytes.count))
        payload.append(contentsOf: nameBytes)
        try await secure.send(HostMessages.withId(HostMessages.shareGet, id, payload))
        return try await download.promise.value(timeout: 1800)
    }

    // MARK: loops

    private func frameLoop() async {
        while !Task.isCancelled {
            let started = Date()
            let waiter = Promise<Bool>()
            lock.lock(); frameWaiter = waiter; lock.unlock()
            do {
                try await secure.send([HostMessages.frameRequest])
                let changed = try await waiter.value(timeout: 30)
                let wait = (changed ? Self.minFrameInterval : Self.unchangedBackoff) - Date().timeIntervalSince(started)
                if wait > 0 { try await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
            } catch is CancellationError { return }
            catch { await end("The host stopped responding."); return }
        }
    }

    private func currentFrameWaiter() -> Promise<Bool> { lock.lock(); defer { lock.unlock() }; return frameWaiter }

    private func receiveLoop() async {
        do {
            while let message = try await secure.receive() {
                guard let kind = message.first else { continue }
                switch kind {
                case HostMessages.frameReply:
                    if let frame = try? FrameCodec.decode(Array(message.dropFirst())) { onFrame?(frame) }
                    else { onHostError?("Bad frame.") }
                    currentFrameWaiter().succeed(true)
                case HostMessages.unchanged:
                    currentFrameWaiter().succeed(false)
                case HostMessages.clipboardReply:
                    onClipboard?(String(decoding: message.dropFirst(), as: UTF8.self))
                case HostMessages.error:
                    onHostError?(String(decoding: message.dropFirst(), as: UTF8.self))
                    currentFrameWaiter().succeed(false) // an error also answers a frame request
                case HostMessages.fileAck:
                    handleAck(message)
                case HostMessages.shareListReply:
                    let entries = (try? JSONDecoder().decode([ShareEntry].self, from: Data(message.dropFirst()))) ?? []
                    lock.lock(); let waiter = listWaiter; lock.unlock()
                    waiter?.succeed(entries)
                case HostMessages.shareFileStart, HostMessages.shareFileChunk, HostMessages.shareFileEnd:
                    handleDownload(message)
                default: break
                }
            }
            await end(nil)
        } catch is CancellationError {
        } catch {
            await end("Secure connection problem: \(error)")
        }
    }

    private func handleAck(_ message: [UInt8]) {
        guard let (id, rest) = HostMessages.readId(message), rest.count >= 3 else { return }
        let ok = rest[0] == 1
        let length = Int(rest.readUInt16BE(at: 1))
        let text = rest.count >= 3 + length ? String(decoding: rest[3..<(3 + length)], as: UTF8.self) : ""
        lock.lock(); let ack = acks[id]; let download = downloads[id]; lock.unlock()
        if let ack { if ok { ack.succeed(text) } else { ack.fail(RemotelyError.io(text)) } }
        else if let download, !ok { download.promise.fail(RemotelyError.io(text)) }
    }

    private func handleDownload(_ message: [UInt8]) {
        guard let (id, rest) = HostMessages.readId(message) else { return }
        lock.lock(); let download = downloads[id]; lock.unlock()
        guard let download else { return }
        do {
            switch message[0] {
            case HostMessages.shareFileStart where rest.count == 8 + 32:
                download.sink?.abort()
                download.sink = try FileSink.create(directory: downloadDirectory, requestedName: download.requestedName,
                                                    size: Int64(bitPattern: rest.readUInt64BE(at: 0)), sha256: Array(rest[8..<40]))
            case HostMessages.shareFileChunk where download.sink != nil:
                try download.sink!.write(rest)
            case HostMessages.shareFileEnd where download.sink != nil:
                download.promise.succeed(try download.sink!.complete())
            default: break
            }
        } catch { download.promise.fail(RemotelyError.io("\(error)")) }
    }

    private func end(_ reason: String?) async {
        lock.lock()
        let already = ended
        ended = true
        let waiters = Array(acks.values)
        let downloadList = Array(downloads.values)
        let list = listWaiter
        lock.unlock()
        guard !already else { return }
        for t in tasks { t.cancel() }
        inputSignalContinuation.finish()
        for a in waiters { a.fail(RemotelyError.io(reason ?? "Session ended.")) }
        for d in downloadList { d.promise.fail(RemotelyError.io(reason ?? "Session ended.")) }
        list?.fail(RemotelyError.io(reason ?? "Session ended."))
        currentFrameWaiter().succeed(false)
        await secure.close()
        onClosed?(reason)
    }

    public func close() async { await end(nil) }
}
