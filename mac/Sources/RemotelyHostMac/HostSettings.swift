import SwiftUI
import AppKit
import ApplicationServices
import RemotelyKit
import RemotelyUI

/// The host's settings window (so a family member does not need a Terminal): server details, who may connect, and the two macOS permissions.
struct HostSettingsView: View {
    @State private var config = HostConfig.load()
    @State private var controlIds = HostConfig.load().TrustedViewers.joined(separator: "\n")
    @State private var viewOnlyIds = HostConfig.load().ViewOnlyViewers.joined(separator: "\n")
    @State private var message = ""
    @State private var isError = false
    @State private var screenAllowed = CGPreflightScreenCaptureAccess()
    @State private var accessibilityAllowed = AXIsProcessTrusted()
    private let deviceId = (try? KeyStore.loadOrCreate().deviceId) ?? "unavailable"
    private let timer = Timer.publish(every: 2, on: .main, in: .common).autoconnect()
    var onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                RemotelyMark().frame(width: 28, height: 28)
                Text("Remotely Host").font(Theme.font(Theme.title, .semibold)).foregroundColor(Theme.text)
            }.padding(16)

            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    section("YOUR SERVER")
                    field("Server address", "Looks like wss://your-name.example.com:8443/ws", $config.ServerUri)
                    field("Server key pin", "Printed by the server when it starts.", $config.ServerPin)
                    field("This Mac's name", nil, $config.DeviceName)
                    field("Pairing code", "One-time code from your server. Only needed the first time.", $config.PairCode)

                    section("WHO MAY CONNECT")
                    multiline("Can control this Mac (one device id per line)", "Copy a viewer's id from its Settings screen.", $controlIds)
                    multiline("Can only watch (one per line)", nil, $viewOnlyIds)
                    Toggle("Ctrl on the other computer acts as Command here", isOn: $config.CtrlAsCommand)
                        .font(Theme.font(Theme.line)).foregroundColor(Theme.text).padding(.vertical, 10)

                    section("PERMISSIONS (macOS asks once)")
                    permission("Screen Recording", screenAllowed, "Privacy_ScreenCapture")
                    permission("Accessibility (mouse and keyboard)", accessibilityAllowed, "Privacy_Accessibility")
                    Text("After allowing a permission, quit and reopen Remotely Host once.").font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary).padding(.top, 6)

                    VStack(alignment: .leading, spacing: 4) {
                        Text("This Mac's device id").font(Theme.font(Theme.line, .semibold)).foregroundColor(Theme.text)
                        Text(deviceId).font(.system(size: 12, design: .monospaced)).foregroundColor(Theme.text).textSelection(.enabled)
                    }
                    .padding(12).background(RoundedRectangle(cornerRadius: Theme.radiusCard).fill(Theme.surface)).padding(.top, 14)

                    if !message.isEmpty { Text(message).font(Theme.font(Theme.line)).foregroundColor(isError ? Theme.error : Theme.connected).padding(.top, 12) }
                }.padding(.horizontal, 16)
            }

            HStack {
                Spacer()
                Button("Close", action: onClose).buttonStyle(SecondaryButtonStyle())
                Button("Save") { save() }.buttonStyle(PrimaryButtonStyle())
            }.padding(16)
        }
        .frame(width: 480, height: 700)
        .background(Theme.panel)
        .onReceive(timer) { _ in screenAllowed = CGPreflightScreenCaptureAccess(); accessibilityAllowed = AXIsProcessTrusted() }
    }

    private func section(_ text: String) -> some View {
        Text(text).font(Theme.font(Theme.label, .semibold)).foregroundColor(Theme.secondary).padding(.top, 14).padding(.bottom, 6)
    }

    private func field(_ label: String, _ help: String?, _ value: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(Theme.font(Theme.line)).foregroundColor(Theme.text)
            TextField("", text: value).textFieldStyle(FieldStyle())
            if let help { Text(help).font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary) }
        }.padding(.vertical, 8)
    }

    private func multiline(_ label: String, _ help: String?, _ value: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(Theme.font(Theme.line)).foregroundColor(Theme.text)
            TextEditor(text: value).font(.system(size: 12, design: .monospaced)).scrollContentBackground(.hidden).padding(6).frame(height: 64)
                .background(RoundedRectangle(cornerRadius: Theme.radiusControl).fill(Theme.well))
                .overlay(RoundedRectangle(cornerRadius: Theme.radiusControl).stroke(Theme.controlBorder, lineWidth: 1))
            if let help { Text(help).font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary) }
        }.padding(.vertical, 8)
    }

    private func permission(_ name: String, _ allowed: Bool, _ pane: String) -> some View {
        HStack {
            Circle().fill(allowed ? Theme.connected : Theme.error).frame(width: 8, height: 8)
            Text(name).font(Theme.font(Theme.line)).foregroundColor(Theme.text)
            Spacer()
            Text(allowed ? "Allowed" : "Not allowed").font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary)
            if !allowed {
                Button("Open Settings") {
                    if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") { NSWorkspace.shared.open(url) }
                }.buttonStyle(SecondaryButtonStyle())
            }
        }.padding(.vertical, 6)
    }

    private func ids(_ text: String) -> [String] {
        text.split(whereSeparator: { $0.isNewline || $0 == "," || $0 == " " }).map { String($0).trimmingCharacters(in: .whitespaces).lowercased() }.filter { $0.count == 64 }
    }

    private func save() {
        let server = config.ServerUri.trimmingCharacters(in: .whitespaces)
        if !server.isEmpty {
            guard let url = URL(string: server), let scheme = url.scheme, ["wss", "ws"].contains(scheme) else { message = "The server address should start with wss://"; isError = true; return }
            if scheme == "wss" && config.ServerPin.trimmingCharacters(in: .whitespaces).count != 64 { message = "The server key pin is 64 characters."; isError = true; return }
            config.AllowInsecureTransport = scheme == "ws"
        }
        config.ServerUri = server
        config.ServerPin = config.ServerPin.trimmingCharacters(in: .whitespaces).lowercased()
        config.TrustedViewers = ids(controlIds)
        config.ViewOnlyViewers = ids(viewOnlyIds).filter { !config.TrustedViewers.contains($0) }
        do {
            try config.save()
            message = "Saved. The host picks this up within a few seconds."
            isError = false
        } catch { message = "Could not save: \(error)"; isError = true }
    }
}

@MainActor
final class SettingsWindow {
    static var window: NSWindow?

    static func show() {
        if let window { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); return }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 480, height: 700), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "Remotely Host"
        window.isReleasedWhenClosed = false
        window.center()
        window.contentView = NSHostingView(rootView: HostSettingsView(onClose: { window.close() }))
        self.window = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
}
