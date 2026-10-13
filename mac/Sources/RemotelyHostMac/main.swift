import Foundation
import AppKit
import ApplicationServices
import RemotelyKit

let usage = """
Remotely Host for macOS

  remotely-host-mac setup --invite RMT1-... [--name "My Mac"] [--trusted-only]   join your server with the invite its 'info' command prints
  remotely-host-mac trust-all on|off                    on: any device paired with your server may connect (the invite default); off: only listed viewers
  remotely-host-mac setup --server wss://host:8443/ws --pin <server key pin> --code XXXX-XXXX --name "My Mac" [--trust <viewer id>] [--view-only <viewer id>] [--insecure]
  remotely-host-mac trust <viewer id> [--view-only]     allow a viewer (control, or watch only)
  remotely-host-mac untrust <viewer id>                 remove a viewer
  remotely-host-mac id                                  print this Mac's device id
  remotely-host-mac diag                                show permissions and settings
  remotely-host-mac install | uninstall                 start at login (LaunchAgent) / stop doing that
  remotely-host-mac run [--fake-screen] [--seconds N]   run in this window (the LaunchAgent does this at login)

The host needs two permissions, granted once in System Settings > Privacy & Security: Screen Recording and Accessibility.
macOS cannot show the login screen to a remote viewer, so the host works while a user is signed in (turn on automatic login for an unattended Mac).
"""

func option(_ name: String, _ args: [String]) -> String? {
    guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
    return args[i + 1]
}

func values(of name: String, in args: [String]) -> [String] {
    args.indices.filter { args[$0] == name && $0 + 1 < args.count }.map { args[$0 + 1] }
}

func setup(_ args: [String]) throws {
    var config = HostConfig.load()
    if let text = option("--invite", args) {
        let invite = try Invite.parse(text)
        config.ServerUri = invite.serverURI
        config.ServerPin = invite.pinHex
        config.PairCode = invite.code
        config.AllowInsecureTransport = invite.serverURI.hasPrefix("ws://")
        // Joining with an invite is the family set-up: any device paired with your server may connect. --trusted-only keeps a per-Mac list.
        config.TrustAllPaired = !args.contains("--trusted-only")
    }
    config.ServerUri = option("--server", args) ?? config.ServerUri
    config.ServerPin = option("--pin", args) ?? config.ServerPin
    config.PairCode = option("--code", args) ?? config.PairCode
    config.DeviceName = option("--name", args) ?? config.DeviceName
    if args.contains("--insecure") { config.AllowInsecureTransport = true }
    if args.contains("--no-ctrl-as-command") { config.CtrlAsCommand = false }
    for id in values(of: "--trust", in: args) where !config.TrustedViewers.contains(where: { $0.caseInsensitiveCompare(id) == .orderedSame }) { config.TrustedViewers.append(id) }
    for id in values(of: "--view-only", in: args) where !config.ViewOnlyViewers.contains(where: { $0.caseInsensitiveCompare(id) == .orderedSame }) { config.ViewOnlyViewers.append(id) }
    try config.save()
    let identity = try KeyStore.loadOrCreate()
    print("Saved \(HostPaths.configFile.path)\nThis host's device id: \(identity.deviceId)")
    print("Any device paired with your server may connect: \(config.TrustAllPaired ? "yes" : "no, only the lists below")")
    print("Trusted viewers (control): \(config.TrustedViewers.isEmpty ? "none" : config.TrustedViewers.joined(separator: ", "))")
    print("View-only viewers: \(config.ViewOnlyViewers.isEmpty ? "none" : config.ViewOnlyViewers.joined(separator: ", "))")
}

func trust(_ id: String, viewOnly: Bool) throws {
    var config = HostConfig.load()
    config.TrustedViewers.removeAll { $0.caseInsensitiveCompare(id) == .orderedSame }
    config.ViewOnlyViewers.removeAll { $0.caseInsensitiveCompare(id) == .orderedSame }
    if viewOnly { config.ViewOnlyViewers.append(id) } else { config.TrustedViewers.append(id) }
    try config.save()
    print("Trusted viewers (control): \(config.TrustedViewers.joined(separator: ", "))\nView-only viewers: \(config.ViewOnlyViewers.joined(separator: ", "))")
}

func diag() throws {
    let os = ProcessInfo.processInfo.operatingSystemVersion
    var arch = "unknown"
    #if arch(arm64)
    arch = "arm64 (Apple silicon)"
    #elseif arch(x86_64)
    arch = "x86_64 (Intel)"
    #endif
    print("macOS:            \(os.majorVersion).\(os.minorVersion).\(os.patchVersion)")
    print("Process arch:     \(arch)")
    print("Screen Recording: \(CGPreflightScreenCaptureAccess() ? "allowed" : "NOT allowed")")
    print("Accessibility:    \(AXIsProcessTrusted() ? "allowed" : "NOT allowed")")
    let config = HostConfig.load()
    print("Server:           \(config.ServerUri.isEmpty ? "not set" : config.ServerUri)")
    print("Device name:      \(config.DeviceName)")
    print("Device id:        \((try? KeyStore.loadOrCreate().deviceId) ?? "unavailable")")
    print("Trusted viewers:  \(config.TrustedViewers.count) control, \(config.ViewOnlyViewers.count) view only")
    print("Data directory:   \(HostPaths.dataDir.path)")
    print("Log:              \(HostPaths.logFile.path)")
}

var args = Array(CommandLine.arguments.dropFirst())
// Double-clicked from Finder or started at login: no arguments, run as the menu bar host.
if args.isEmpty && Bundle.main.bundlePath.hasSuffix(".app") { args = ["run"] }

do {
    switch args.first {
    case "setup": try setup(Array(args.dropFirst()))
    case "trust" where args.count >= 2: try trust(args[1], viewOnly: args.contains("--view-only"))
    case "untrust" where args.count >= 2:
        var config = HostConfig.load()
        config.TrustedViewers.removeAll { $0.caseInsensitiveCompare(args[1]) == .orderedSame }
        config.ViewOnlyViewers.removeAll { $0.caseInsensitiveCompare(args[1]) == .orderedSame }
        try config.save()
        print("Removed. That viewer can no longer connect.")
    case "trust-all" where args.count >= 2 && ["on", "off"].contains(args[1]):
        var config = HostConfig.load()
        config.TrustAllPaired = args[1] == "on"
        try config.save()
        print(config.TrustAllPaired ? "Any device paired with your server may now connect to this Mac." : "Only the viewers on this Mac's trust list may connect now.")
    case "id": print(try KeyStore.loadOrCreate().deviceId)
    case "diag": try diag()
    case "install": try LaunchAgent.install()
    case "uninstall": try LaunchAgent.uninstall()
    case "run":
        let fake = args.contains("--fake-screen")
        let seconds = option("--seconds", args).flatMap(Double.init)
        let runner = HostRunner(fake: fake)
        if fake || seconds != nil {
            // Headless (tests): no menu bar item, optionally stop after N seconds.
            let task = Task { await runner.runForever() }
            if let seconds { try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000)); task.cancel(); try await Task.sleep(nanoseconds: 500_000_000) }
            else { await task.value }
        } else {
            Task { await runner.runForever() }
            StatusMenu.run()
        }
    default: print(usage); exit(1)
    }
} catch {
    FileHandle.standardError.write(Data("Error: \(error)\n".utf8))
    exit(2)
}
