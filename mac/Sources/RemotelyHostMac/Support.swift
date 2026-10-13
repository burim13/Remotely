import Foundation
import Security
import RemotelyKit

enum HostError: Error, CustomStringConvertible {
    case permission(String)
    case capture(String)
    var description: String {
        switch self { case .permission(let m), .capture(let m): return m }
    }
}

/// Where the host keeps its files. REMOTELY_HOME relocates everything (used by tests).
enum HostPaths {
    static var root: URL {
        if let override = ProcessInfo.processInfo.environment["REMOTELY_HOME"] { return URL(fileURLWithPath: override) }
        return URL(fileURLWithPath: NSHomeDirectory())
    }
    static var isRelocated: Bool { ProcessInfo.processInfo.environment["REMOTELY_HOME"] != nil }
    static var dataDir: URL { root.appendingPathComponent("Library/Application Support/Remotely") }
    static var configFile: URL { dataDir.appendingPathComponent("host.json") }
    static var keyFile: URL { dataDir.appendingPathComponent("host.key") }
    static var logFile: URL { root.appendingPathComponent("Library/Logs/Remotely/host.log") }
    static var receiveDir: URL { root.appendingPathComponent("Documents/Remotely/Received") }
    static var shareDir: URL { root.appendingPathComponent("Documents/Remotely/Share") }
}

/// Host settings (host.json). Same property names as the Windows host's file. The pairing code is single use and cleared once paired.
struct HostConfig: Codable {
    var ServerUri = ""
    var ServerPin = ""
    var DeviceName = ComputerName.current
    var PairCode = ""
    /// Viewer device ids allowed to connect and control this Mac. Empty means nobody.
    var TrustedViewers: [String] = []
    /// Viewer device ids allowed to watch only.
    var ViewOnlyViewers: [String] = []
    var AllowInsecureTransport = false
    /// Ctrl on the viewer acts as Command here (so Ctrl+C from a Windows PC copies). Turn off to keep Ctrl as Control.
    var CtrlAsCommand = true

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        ServerUri = try c.decodeIfPresent(String.self, forKey: .ServerUri) ?? ""
        ServerPin = try c.decodeIfPresent(String.self, forKey: .ServerPin) ?? ""
        DeviceName = try c.decodeIfPresent(String.self, forKey: .DeviceName) ?? (ComputerName.current)
        PairCode = try c.decodeIfPresent(String.self, forKey: .PairCode) ?? ""
        TrustedViewers = try c.decodeIfPresent([String].self, forKey: .TrustedViewers) ?? []
        ViewOnlyViewers = try c.decodeIfPresent([String].self, forKey: .ViewOnlyViewers) ?? []
        AllowInsecureTransport = try c.decodeIfPresent(Bool.self, forKey: .AllowInsecureTransport) ?? false
        CtrlAsCommand = try c.decodeIfPresent(Bool.self, forKey: .CtrlAsCommand) ?? true
    }

    static func load() -> HostConfig {
        guard let data = try? Data(contentsOf: HostPaths.configFile), let config = try? JSONDecoder().decode(HostConfig.self, from: data) else { return HostConfig() }
        return config
    }

    func save() throws {
        try FileManager.default.createDirectory(at: HostPaths.dataDir, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: HostPaths.configFile, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: HostPaths.configFile.path)
    }

    func canControl(_ id: String) -> Bool { TrustedViewers.contains { $0.caseInsensitiveCompare(id) == .orderedSame } }
    func isTrusted(_ id: String) -> Bool { canControl(id) || ViewOnlyViewers.contains { $0.caseInsensitiveCompare(id) == .orderedSame } }
}

/// The device's private key: in the login Keychain when possible, otherwise in a 0600 file under Application Support.
enum KeyStore {
    private static let service = "com.remotely.host"
    private static let account = "device-key"

    static func loadOrCreate() throws -> DeviceIdentity {
        if let bytes = read() { return try DeviceIdentity(privateKey: bytes) }
        let identity = DeviceIdentity()
        try write(identity.exportPrivateKey())
        return identity
    }

    private static func read() -> [UInt8]? {
        if !HostPaths.isRelocated {
            let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account,
                                        kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
            var item: CFTypeRef?
            if SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data { return [UInt8](data) }
        }
        if let data = try? Data(contentsOf: HostPaths.keyFile) { return [UInt8](data) }
        return nil
    }

    private static func write(_ key: [UInt8]) throws {
        if !HostPaths.isRelocated {
            let attributes: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account,
                                             kSecValueData as String: Data(key), kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock]
            if SecItemAdd(attributes as CFDictionary, nil) == errSecSuccess { return }
        }
        try FileManager.default.createDirectory(at: HostPaths.dataDir, withIntermediateDirectories: true)
        try Data(key).write(to: HostPaths.keyFile, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: HostPaths.keyFile.path)
    }
}

/// Console plus a log file.
final class Logger: @unchecked Sendable {
    static let shared = Logger()
    private let lock = NSLock()
    private let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return f
    }()

    func write(_ message: String) {
        lock.lock()
        defer { lock.unlock() }
        let line = "\(formatter.string(from: Date())) [host] \(message)"
        print(line)
        fflush(stdout)
        do {
            try FileManager.default.createDirectory(at: HostPaths.logFile.deletingLastPathComponent(), withIntermediateDirectories: true)
            if let handle = try? FileHandle(forWritingTo: HostPaths.logFile) {
                try handle.seekToEnd()
                try handle.write(contentsOf: Data((line + "\n").utf8))
                try handle.close()
            } else {
                try Data((line + "\n").utf8).write(to: HostPaths.logFile)
            }
        } catch { /* console only */ }
    }
}
