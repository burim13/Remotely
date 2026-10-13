import Foundation
import RemotelyKit

// Scriptable test viewer for macOS. Not the real viewer: the identity key is stored unprotected in ~/.remotely-cli.
let usage = """
Remotely CLI (macOS)

  remotely-cli pair --server wss://host:8443/ws --pin <server key pin> --code XXXX-XXXX --name "My Mac" [--insecure]
  remotely-cli list
  remotely-cli watch <host id or prefix> [--out frames] [--count 5] [--seconds 10]
  remotely-cli control <host id or prefix> [--move X,Y] [--click left|right] [--key VK] [--clip TEXT] [--sas]
                                           [--upload FILE] [--list-share] [--download NAME] [--wait 3]
"""

let configDirectory = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".remotely-cli")
let identityPath = configDirectory.appendingPathComponent("identity.key")
let settingsPath = configDirectory.appendingPathComponent("cli.json")

func option(_ name: String, _ args: [String]) -> String? {
    guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
    return args[i + 1]
}

func loadIdentity() throws -> DeviceIdentity {
    try FileManager.default.createDirectory(at: configDirectory, withIntermediateDirectories: true)
    if let data = try? Data(contentsOf: identityPath) { return try DeviceIdentity(privateKey: [UInt8](data)) }
    let identity = DeviceIdentity()
    try Data(identity.exportPrivateKey()).write(to: identityPath, options: .atomic)
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: identityPath.path)
    return identity
}

func loadOptions() throws -> CoordinatorOptions {
    guard let data = try? Data(contentsOf: settingsPath), let s = try? JSONSerialization.jsonObject(with: data) as? [String: String],
          let server = s["server"], let url = URL(string: server) else { throw RemotelyError.io("Run 'pair' first.") }
    return CoordinatorOptions(serverURL: url, pinnedServerKey: s["pin"] ?? "", allowInsecureTransport: s["insecure"] == "true")
}

func pair(_ args: [String]) async throws -> Int32 {
    guard let server = option("--server", args), let url = URL(string: server) else { throw RemotelyError.io("--server is required") }
    guard let code = option("--code", args) else { throw RemotelyError.io("--code is required") }
    let identity = try loadIdentity()
    let insecure = args.contains("--insecure")
    let options = CoordinatorOptions(serverURL: url, pinnedServerKey: option("--pin", args) ?? "", allowInsecureTransport: insecure)
    let client = try await CoordinatorClient.connect(options: options, identity: identity)
    defer { client.shutdown() }
    if !client.isPaired { try await client.pair(code: code, name: option("--name", args) ?? Host.current().localizedName ?? "Mac", canHost: false) }
    let settings = ["server": server, "pin": options.pinnedServerKey, "insecure": insecure ? "true" : "false"]
    try JSONSerialization.data(withJSONObject: settings).write(to: settingsPath, options: .atomic)
    print("Paired. This viewer's device id (give it to the host's 'trust' command):\n\(identity.deviceId)")
    return 0
}

func connect() async throws -> (CoordinatorClient, DeviceIdentity) {
    let identity = try loadIdentity()
    return (try await CoordinatorClient.connect(options: try loadOptions(), identity: identity), identity)
}

func list() async throws -> Int32 {
    let (client, _) = try await connect()
    defer { client.shutdown() }
    for d in try await client.listDevices() {
        print("\(d.online ? "online " : "offline")  \(d.canHost ? "host  " : "viewer")  \(d.name.padding(toLength: 24, withPad: " ", startingAt: 0)) \(d.deviceId)")
    }
    return 0
}

func openSession(_ client: CoordinatorClient, _ identity: DeviceIdentity, _ prefix: String) async throws -> ViewerSession {
    let wanted = prefix.replacingOccurrences(of: "-", with: "").lowercased()
    let matches = try await client.listDevices().filter { $0.canHost && $0.deviceId.hasPrefix(wanted) }
    guard matches.count == 1 else { throw RemotelyError.io(matches.isEmpty ? "No such host." : "Prefix is ambiguous.") }
    let session = try await ViewerSession.open(client: client, identity: identity, hostDeviceId: matches[0].deviceId)
    print("Secure session with \(matches[0].name) (\(DeviceIds.short(matches[0].deviceId))). Capabilities: \(describe(session.hello.capabilities))")
    session.onHostError = { print("host error: \($0)") }
    return session
}

func describe(_ c: HostCapabilities) -> String {
    var parts: [String] = []
    if c.contains(.control) { parts.append("Control") }
    if c.contains(.secureAttention) { parts.append("SecureAttention") }
    if c.contains(.clipboard) { parts.append("Clipboard") }
    if c.contains(.files) { parts.append("Files") }
    return parts.isEmpty ? "None" : parts.joined(separator: ", ")
}

final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var n = 0
    func next() -> Int { lock.lock(); defer { lock.unlock() }; n += 1; return n }
    var value: Int { lock.lock(); defer { lock.unlock() }; return n }
}

func watch(_ prefix: String, _ args: [String]) async throws -> Int32 {
    let out = URL(fileURLWithPath: option("--out", args) ?? "frames")
    let count = Int(option("--count", args) ?? "5") ?? 5
    let seconds = Double(option("--seconds", args) ?? "10") ?? 10
    try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
    let (client, identity) = try await connect()
    defer { client.shutdown() }
    let session = try await openSession(client, identity, prefix)
    let counter = Counter()
    session.onFrame = { frame in
        let n = counter.next()
        guard n <= count else { return }
        let file = out.appendingPathComponent(String(format: "frame-%04d.jpg", n))
        try? Data(frame.jpeg).write(to: file)
        print("#\(n): \(frame.width)x\(frame.height) desktop=\(frame.desktop) \(frame.jpeg.count / 1024) KB -> \(file.path)")
    }
    session.start()
    let deadline = Date().addingTimeInterval(seconds)
    while Date() < deadline && counter.value < count { try await Task.sleep(nanoseconds: 100_000_000) }
    print("Received \(min(counter.value, count)) frame(s).")
    await session.close()
    return counter.value > 0 ? 0 : 3
}

func control(_ prefix: String, _ args: [String]) async throws -> Int32 {
    let (client, identity) = try await connect()
    defer { client.shutdown() }
    let session = try await openSession(client, identity, prefix)
    session.onClipboard = { print("clipboard from host: \($0)") }
    session.start()
    var failures: Int32 = 0

    if let move = option("--move", args) {
        let p = move.split(separator: ",").compactMap { UInt16($0) }
        if p.count == 2 { session.sendInput(.move(p[0], p[1])); print("sent mouse move \(move) (can control: \(session.canControl))") }
    }
    if let click = option("--click", args) {
        let b: MouseButton = click == "right" ? .right : .left
        session.sendInput(.down(b, 32768, 32768)); session.sendInput(.up(b, 32768, 32768))
        print("sent \(click) click")
    }
    if let key = option("--key", args), let vk = UInt16(key) {
        session.sendInput(.key(down: true, virtualKey: vk)); session.sendInput(.key(down: false, virtualKey: vk))
        print("sent key \(vk)")
    }
    if let clip = option("--clip", args) { try await session.sendClipboard(clip); print("sent clipboard text") }
    if args.contains("--sas") {
        print("Ctrl+Alt+Del available: \(session.canSendSecureAttention)")
        try await session.sendSecureAttention()
    }
    if let upload = option("--upload", args) {
        do { print("uploaded as: \(try await session.uploadFile(at: URL(fileURLWithPath: upload)))") }
        catch { print("upload failed: \(error)"); failures += 1 }
    }
    if args.contains("--list-share") {
        let list = try await session.listSharedFiles()
        print("shared files: \(list.count)")
        for e in list { print("  \(e.Name)  \(e.Size) bytes") }
    }
    if let name = option("--download", args) {
        session.downloadDirectory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("downloads")
        do { print("downloaded to: \(try await session.downloadFile(named: name))") }
        catch { print("download failed: \(error)"); failures += 1 }
    }
    try await Task.sleep(nanoseconds: UInt64((Double(option("--wait", args) ?? "3") ?? 3) * 1_000_000_000))
    await session.close()
    return failures == 0 ? 0 : 4
}

func run() async -> Int32 {
    let args = Array(CommandLine.arguments.dropFirst())
    do {
        switch args.first {
        case "pair": return try await pair(Array(args.dropFirst()))
        case "list": return try await list()
        case "watch" where args.count >= 2: return try await watch(args[1], Array(args.dropFirst(2)))
        case "control" where args.count >= 2: return try await control(args[1], Array(args.dropFirst(2)))
        default: print(usage); return 1
        }
    } catch {
        FileHandle.standardError.write(Data("Error: \(error)\n".utf8))
        return 2
    }
}

let exitCode = await run()
exit(exitCode)
