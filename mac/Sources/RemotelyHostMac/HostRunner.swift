import Foundation
import RemotelyKit

/// Connects, pairs (once), serves, and reconnects with backoff. The config is re-read on every attempt and every viewer check, so edits apply without a restart.
final class HostRunner: @unchecked Sendable {
    private let fake: Bool

    init(fake: Bool) { self.fake = fake }

    private func makeServices() throws -> HostServices {
        try? FileManager.default.createDirectory(at: HostPaths.receiveDir, withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(at: HostPaths.shareDir, withIntermediateDirectories: true)
        let ctrl: () -> Bool = { HostConfig.load().CtrlAsCommand }
        let frames: FrameSource
        if fake { frames = FakeScreen() }
        else if #available(macOS 14.0, *) { frames = ScreenCapturer() }
        else { frames = UnsupportedCapturer() }
        return HostServices(
            frames: frames,
            input: fake ? PrintingInput() : MacInput(ctrlAsCommand: ctrl),
            clipboard: fake ? FakeClipboard() : MacClipboard(),
            receiveDirectory: HostPaths.receiveDir,
            shareDirectory: HostPaths.shareDir,
            isTrusted: { HostConfig.load().isTrusted($0) },
            canControl: { HostConfig.load().canControl($0) })
    }

    func runForever() async {
        let log: @Sendable (String) -> Void = { Logger.shared.write($0) }
        let identity: DeviceIdentity
        let agent: HostAgent
        do {
            identity = try KeyStore.loadOrCreate()
            agent = HostAgent(identity: identity, services: try makeServices(), log: log)
        } catch { log("Cannot start: \(error)"); return }
        log("Remotely Host starting. Device id: \(identity.deviceId)")

        var delay: UInt64 = 2
        while !Task.isCancelled {
            var config = HostConfig.load()
            if config.ServerUri.isEmpty {
                log("Not configured: run 'remotely-host-mac setup ...'. Waiting.")
                delay = 15
            } else {
                do {
                    guard let url = URL(string: config.ServerUri) else { throw RemotelyError.io("The server address is not valid.") }
                    let options = CoordinatorOptions(serverURL: url, pinnedServerKey: config.ServerPin, allowInsecureTransport: config.AllowInsecureTransport)
                    let client = try await CoordinatorClient.connect(options: options, identity: identity)
                    defer { client.shutdown() }
                    if !client.isPaired {
                        guard !config.PairCode.isEmpty else { throw RemotelyError.coordinator("This device is not paired and no pairing code is set.") }
                        try await client.pair(code: config.PairCode, name: config.DeviceName, canHost: true)
                        config.PairCode = ""
                        try config.save()
                        log("Paired with the coordinator as '\(config.DeviceName)'. Device id: \(identity.deviceId)")
                    }
                    log("Online as \(DeviceIds.short(identity.deviceId)).")
                    delay = 2
                    await withTaskCancellationHandler { await agent.run(client: client) } onCancel: { client.shutdown() }
                    log("Disconnected from the coordinator.")
                } catch {
                    if Task.isCancelled { return }
                    log("Coordinator connection failed: \(error)")
                }
            }
            do { try await Task.sleep(nanoseconds: delay * 1_000_000_000) } catch { return }
            delay = min(delay * 2, 60)
        }
    }
}
