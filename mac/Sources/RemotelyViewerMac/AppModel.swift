import Foundation
import SwiftUI
import AppKit
import Security
import RemotelyKit

struct ViewerSettings: Codable {
    var serverURL = ""
    var serverPin = ""
    var deviceName = ComputerName.current
    /// "system", "dark" or "light"
    var theme = "system"
    var syncClipboard = true
    var commandAsControl = true
    var allowInsecure = false

    var isConfigured: Bool { !serverURL.trimmingCharacters(in: .whitespaces).isEmpty }
}

enum ViewerStore {
    static var directory: URL { URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support/Remotely") }
    static var settingsFile: URL { directory.appendingPathComponent("viewer.json") }
    static var keyFile: URL { directory.appendingPathComponent("viewer.key") }
    private static let service = "com.remotely.viewer", account = "device-key"

    static func loadSettings() -> ViewerSettings {
        guard let data = try? Data(contentsOf: settingsFile), let s = try? JSONDecoder().decode(ViewerSettings.self, from: data) else { return ViewerSettings() }
        return s
    }

    static func save(_ s: ViewerSettings) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? JSONEncoder().encode(s).write(to: settingsFile, options: .atomic)
    }

    /// The device key lives in the login Keychain, or in a 0600 file if the Keychain refuses.
    static func loadOrCreateIdentity() -> DeviceIdentity {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account,
                                    kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var item: CFTypeRef?
        if SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data, let id = try? DeviceIdentity(privateKey: [UInt8](data)) { return id }
        if let data = try? Data(contentsOf: keyFile), let id = try? DeviceIdentity(privateKey: [UInt8](data)) { return id }
        let identity = DeviceIdentity()
        let key = Data(identity.exportPrivateKey())
        let add: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account,
                                  kSecValueData as String: key, kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock]
        if SecItemAdd(add as CFDictionary, nil) != errSecSuccess {
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try? key.write(to: keyFile, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: keyFile.path)
        }
        return identity
    }
}

@MainActor
final class AppModel: ObservableObject {
    @Published var settings = ViewerStore.loadSettings()
    @Published var devices: [DeviceInfo] = []
    @Published var banner: String?
    @Published var showSettings = false
    let identity = ViewerStore.loadOrCreateIdentity()
    private var client: CoordinatorClient?
    private var refreshing = false

    init() {}

    var colorScheme: ColorScheme? { settings.theme == "dark" ? .dark : settings.theme == "light" ? .light : nil }

    private var options: CoordinatorOptions? {
        guard let url = URL(string: settings.serverURL) else { return nil }
        return CoordinatorOptions(serverURL: url, pinnedServerKey: settings.serverPin, allowInsecureTransport: settings.allowInsecure)
    }

    /// A live, paired connection, reconnecting if needed.
    func ensureClient() async throws -> CoordinatorClient {
        if let client, client.isConnected { return client }
        client?.shutdown()
        client = nil
        guard let options else { throw RemotelyError.coordinator("The server address is not valid.") }
        let connected = try await CoordinatorClient.connect(options: options, identity: identity)
        guard connected.isPaired else {
            connected.shutdown()
            throw RemotelyError.coordinator("This device is not paired with the server yet. Open Settings and enter a pairing code.")
        }
        client = connected
        return connected
    }

    func pair(code: String) async throws {
        client?.shutdown()
        client = nil
        guard let options else { throw RemotelyError.coordinator("The server address is not valid.") }
        let connected = try await CoordinatorClient.connect(options: options, identity: identity)
        if !connected.isPaired {
            do { try await connected.pair(code: code, name: settings.deviceName, canHost: false) }
            catch { connected.shutdown(); throw error }
        }
        client = connected
    }

    /// "Join with an invite": read it, remember the server, pair this device. If it fails nothing is kept, so the welcome screen comes back.
    func join(inviteText: String, deviceName: String? = nil) async throws {
        let invite = try Invite.parse(inviteText)
        let previous = settings
        settings.serverURL = invite.serverURI
        settings.serverPin = invite.pinHex
        settings.allowInsecure = invite.serverURI.hasPrefix("ws://")
        if let name = deviceName?.trimmingCharacters(in: .whitespaces), !name.isEmpty { settings.deviceName = name }
        saveSettings()
        do { try await pair(code: invite.code) }
        catch {
            settings = previous
            saveSettings()
            throw error
        }
    }

    func saveSettings() {
        ViewerStore.save(settings)
    }

    func refresh() async {
        guard !refreshing, settings.isConfigured else { return }
        refreshing = true
        defer { refreshing = false }
        do {
            let all = try await ensureClient().listDevices()
            devices = all.filter { $0.canHost || $0.deviceId == identity.deviceId }
                .sorted { ($0.online ? 0 : 1, $0.name.lowercased()) < ($1.online ? 0 : 1, $1.name.lowercased()) }
            banner = nil
        } catch {
            banner = "\(error)".hasPrefix("This device") ? "\(error)" : "Can't reach your server: \(error)"
        }
    }

    func openSession(to device: DeviceInfo) {
        SessionWindowController.open(model: self, host: device)
    }
}
