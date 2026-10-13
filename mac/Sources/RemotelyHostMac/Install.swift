import Foundation
import AppKit
import RemotelyUI

/// Start at login through a per-user LaunchAgent (screen capture needs the signed-in user's session, so this is not a system daemon).
enum LaunchAgent {
    static let label = "com.remotely.host"
    static var plistURL: URL { URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/LaunchAgents/\(label).plist") }

    static func install() throws {
        guard let executable = Bundle.main.executablePath ?? CommandLine.arguments.first else { throw HostError.capture("Cannot find this program.") }
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": [executable, "run"],
            "RunAtLoad": true,
            "KeepAlive": true,
            "ProcessType": "Interactive",
            "StandardOutPath": HostPaths.logFile.deletingLastPathComponent().appendingPathComponent("launchd.out").path,
            "StandardErrorPath": HostPaths.logFile.deletingLastPathComponent().appendingPathComponent("launchd.err").path,
        ]
        try FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: HostPaths.logFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0).write(to: plistURL, options: .atomic)
        _ = launchctl(["bootout", "gui/\(getuid())/\(label)"])           // replace a previous copy
        let result = launchctl(["bootstrap", "gui/\(getuid())", plistURL.path])
        print(result == 0 ? "Installed. Remotely Host now starts at login, and is running." : "Installed; it will start at your next login (launchctl exit \(result)).")
    }

    static func uninstall() throws {
        _ = launchctl(["bootout", "gui/\(getuid())/\(label)"])
        try? FileManager.default.removeItem(at: plistURL)
        print("Removed. Your settings, device key and logs were kept.")
    }

    @discardableResult
    private static func launchctl(_ arguments: [String]) -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run(); process.waitUntilExit(); return process.terminationStatus } catch { return -1 }
    }
}

/// A small menu bar item so the host is visible and can be quit.
enum StatusMenu {
    static func run() {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.title = "Remotely"
        let menu = NSMenu()
        menu.addItem(withTitle: "Remotely Host is running", action: nil, keyEquivalent: "")
        menu.addItem(NSMenuItem.separator())
        let settings = NSMenuItem(title: "Settings…", action: #selector(Handler.showSettings), keyEquivalent: ",")
        settings.target = Handler.shared
        menu.addItem(settings)
        let open = NSMenuItem(title: "Show log", action: #selector(Handler.showLog), keyEquivalent: "")
        open.target = Handler.shared
        menu.addItem(open)
        let quit = NSMenuItem(title: "Quit Remotely Host", action: #selector(Handler.quit), keyEquivalent: "q")
        quit.target = Handler.shared
        menu.addItem(quit)
        item.menu = menu
        Theme.registerFonts()
        if HostConfig.load().ServerUri.isEmpty { Task { @MainActor in SettingsWindow.show() } } // first start: ask for the server details
        withExtendedLifetime(item) { app.run() }
    }

    final class Handler: NSObject {
        static let shared = Handler()
        @MainActor @objc func showSettings() { SettingsWindow.show() }
        @objc func showLog() { NSWorkspace.shared.activateFileViewerSelecting([HostPaths.logFile]) }
        @objc func quit() { NSApplication.shared.terminate(nil) }
    }
}
