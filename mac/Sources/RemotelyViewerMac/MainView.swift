import SwiftUI
import AppKit
import RemotelyKit
import RemotelyUI

struct MainView: View {
    @ObservedObject var app: AppModel

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                RemotelyMark().frame(width: 28, height: 28)
                Text("Remotely").font(Theme.font(Theme.title, .semibold)).foregroundColor(Theme.text)
                Spacer()
                Button { Task { await app.refresh() } } label: { Image(systemName: "arrow.clockwise") }.buttonStyle(IconButtonStyle()).help("Refresh")
                Button { app.showSettings = true } label: { Image(systemName: "slider.horizontal.3") }.buttonStyle(IconButtonStyle()).help("Settings")
            }
            .padding(.horizontal, 16).frame(height: 52)

            if let banner = app.banner {
                HStack {
                    Text(banner).font(Theme.font(Theme.line)).foregroundColor(Theme.error).fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Try again") { Task { await app.refresh() } }.buttonStyle(SecondaryButtonStyle())
                }
                .padding(12).background(RoundedRectangle(cornerRadius: Theme.radiusCard).fill(Theme.surface)).padding(.horizontal, 16).padding(.bottom, 8)
            }

            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    Text("YOUR COMPUTERS").font(Theme.font(Theme.label, .semibold)).foregroundColor(Theme.secondary).padding(.horizontal, 16).padding(.vertical, 8)
                    ForEach(app.devices, id: \.deviceId) { device in DeviceRow(device: device, isSelf: device.deviceId == app.identity.deviceId) { app.openSession(to: device) } }
                    if app.devices.isEmpty {
                        Text("No computers yet. Install Remotely on the computer you want to reach and paste the same invite there.")
                            .font(Theme.font(Theme.line)).foregroundColor(Theme.secondary).padding(16)
                    }
                }
            }

            Divider().overlay(Theme.hairline)
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("This device: \(app.settings.deviceName) · \(DeviceIds.short(app.identity.deviceId))").font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary)
                    Text("Connections go only through your own server.").font(Theme.font(Theme.label)).foregroundColor(Theme.secondary)
                }
                Spacer()
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(app.identity.deviceId, forType: .string)
                } label: { Image(systemName: "doc.on.doc") }.buttonStyle(IconButtonStyle()).help("Copy this device's id")
            }
            .padding(.horizontal, 16).padding(.vertical, 10)
        }
        .frame(minWidth: 360, minHeight: 420)
        .background(Theme.panel)
        .overlay { if !app.settings.isConfigured { WelcomeView(app: app).background(Theme.panel) } }
        .preferredColorScheme(app.colorScheme)
        .sheet(isPresented: $app.showSettings) { SettingsView(app: app) }
        .task { await app.refresh() }
        .onReceive(Timer.publish(every: 10, on: .main, in: .common).autoconnect()) { _ in Task { await app.refresh() } }
    }
}

/// First run: one thing to do.
struct WelcomeView: View {
    @ObservedObject var app: AppModel
    @State private var invite = ""
    @State private var message = ""
    @State private var isError = false
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Spacer()
            RemotelyMark().frame(width: 56, height: 56)
            Text("Welcome to Remotely").font(.system(size: 22, weight: .semibold)).foregroundColor(Theme.text).padding(.top, 18)
            Text("Paste the invite from your server to connect this Mac. You get it from 'Remotely Server info' on the server.")
                .font(Theme.font(Theme.line)).foregroundColor(Theme.secondary).fixedSize(horizontal: false, vertical: true).padding(.top, 6)
            TextField("", text: $invite).textFieldStyle(FieldStyle()).padding(.top, 18)
            if !message.isEmpty { Text(message).font(Theme.font(Theme.line)).foregroundColor(isError ? Theme.error : Theme.connected).padding(.top, 10) }
            HStack {
                Button(busy ? "Connecting…" : "Connect") { Task { await join() } }.buttonStyle(PrimaryButtonStyle()).disabled(busy)
                Button("Set up by hand") { app.showSettings = true }.buttonStyle(SecondaryButtonStyle())
            }.padding(.top, 16)
            Spacer()
            Spacer()
        }
        .padding(.horizontal, 28)
    }

    private func join() async {
        busy = true
        message = "Connecting…"; isError = false
        do {
            try await app.join(inviteText: invite)
            invite = ""
            await app.refresh()
        } catch { message = "\(error)"; isError = true }
        busy = false
    }
}

struct DeviceRow: View {
    let device: DeviceInfo
    let isSelf: Bool
    let open: () -> Void
    @State private var hovering = false

    var body: some View {
        let canConnect = device.online && device.canHost && !isSelf
        Button(action: open) {
            HStack(spacing: 12) {
                RoundedRectangle(cornerRadius: Theme.radiusControl).fill(Theme.surface).frame(width: 36, height: 36)
                    .overlay(Image(systemName: "display").foregroundColor(Theme.secondary))
                VStack(alignment: .leading, spacing: 1) {
                    Text(device.name).font(Theme.font(Theme.body, .medium)).foregroundColor(Theme.text).lineLimit(1)
                    Text(isSelf ? "This computer" : device.online ? "Online" : "Offline").font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary)
                }
                Spacer()
                Circle().fill(device.online ? Theme.connected : Theme.hairline).frame(width: 8, height: 8)
                if canConnect { Text("Connect").font(Theme.font(Theme.line, .semibold)).foregroundColor(Theme.accentText) }
            }
            .padding(.horizontal, 16).padding(.vertical, 10)
            .background(hovering && canConnect ? Theme.hover : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!canConnect)
        .onHover { hovering = $0 }
    }
}

struct SettingsView: View {
    @ObservedObject var app: AppModel
    @State private var draft = ViewerSettings()
    @State private var code = ""
    @State private var invite = ""
    @State private var message = ""
    @State private var isError = false
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                RemotelyMark().frame(width: 28, height: 28)
                Text("Settings").font(Theme.font(Theme.title, .semibold)).foregroundColor(Theme.text)
            }
            .padding(16)

            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    section("YOUR SERVER")
                    row("Invite", "Paste the invite from your server's 'Remotely Server info'. It holds the address, the key and a one-time code.") { TextField("", text: $invite).textFieldStyle(FieldStyle()) }
                    row("This device's name", nil) { TextField("", text: $draft.deviceName).textFieldStyle(FieldStyle()) }
                    DisclosureGroup("Set up by hand instead") {
                        row("Server address", "Looks like wss://your-name.example.com:8443/ws") { TextField("", text: $draft.serverURL).textFieldStyle(FieldStyle()) }
                        row("Server key pin", nil) { TextField("", text: $draft.serverPin).textFieldStyle(FieldStyle()) }
                        row("Pairing code", "One-time code from your server.") { TextField("", text: $code).textFieldStyle(FieldStyle()) }
                    }.font(Theme.font(Theme.line)).foregroundColor(Theme.secondary).padding(.vertical, 8)
                    section("APPEARANCE AND KEYBOARD")
                    HStack {
                        Text("Theme").font(Theme.font(Theme.line)).foregroundColor(Theme.text)
                        Spacer()
                        Picker("", selection: $draft.theme) {
                            Text("Follow macOS").tag("system"); Text("Dark").tag("dark"); Text("Light").tag("light")
                        }.labelsHidden().frame(width: 150)
                    }.padding(.vertical, 10)
                    Divider().overlay(Theme.hairline)
                    Toggle("Share the clipboard in a session", isOn: $draft.syncClipboard).font(Theme.font(Theme.line)).foregroundColor(Theme.text).padding(.vertical, 10)
                    Divider().overlay(Theme.hairline)
                    Toggle("Use Command as Ctrl on the other computer", isOn: $draft.commandAsControl).font(Theme.font(Theme.line)).foregroundColor(Theme.text).padding(.vertical, 10)
                    Divider().overlay(Theme.hairline)

                    VStack(alignment: .leading, spacing: 4) {
                        Text("This device's id").font(Theme.font(Theme.line, .semibold)).foregroundColor(Theme.text)
                        Text(app.identity.deviceId).font(.system(size: 12, design: .monospaced)).foregroundColor(Theme.text).textSelection(.enabled)
                        Text("Only needed if a computer you control trusts a list of ids instead of every device on your server.").font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary)
                    }
                    .padding(12).background(RoundedRectangle(cornerRadius: Theme.radiusCard).fill(Theme.surface)).padding(.top, 14)

                    if !message.isEmpty {
                        Text(message).font(Theme.font(Theme.line)).foregroundColor(isError ? Theme.error : Theme.connected).padding(.top, 12)
                    }
                }
                .padding(.horizontal, 16)
            }

            HStack {
                Spacer()
                Button("Close") { app.showSettings = false }.buttonStyle(SecondaryButtonStyle())
                Button(busy ? "Connecting…" : "Save and connect") { Task { await save() } }.buttonStyle(PrimaryButtonStyle()).disabled(busy)
            }
            .padding(16)
        }
        .frame(width: 460, height: 640)
        .background(Theme.panel)
        .preferredColorScheme(app.colorScheme)
        .onAppear { draft = app.settings }
    }

    private func section(_ text: String) -> some View {
        Text(text).font(Theme.font(Theme.label, .semibold)).foregroundColor(Theme.secondary).padding(.top, 14).padding(.bottom, 6)
    }

    private func row<Content: View>(_ label: String, _ help: String?, @ViewBuilder _ field: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(Theme.font(Theme.line)).foregroundColor(Theme.text)
            field()
            if let help { Text(help).font(Theme.font(Theme.secondaryText)).foregroundColor(Theme.secondary).fixedSize(horizontal: false, vertical: true) }
        }
        .padding(.vertical, 10)
        .overlay(Divider().overlay(Theme.hairline), alignment: .bottom)
    }

    private func save() async {
        if !invite.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            app.settings.theme = draft.theme; app.settings.syncClipboard = draft.syncClipboard; app.settings.commandAsControl = draft.commandAsControl
            busy = true
            show("Connecting…", error: false)
            do {
                try await app.join(inviteText: invite, deviceName: draft.deviceName)
                app.saveSettings()
                invite = ""
                show("Connected. This device is paired with your server.", error: false)
                try? await Task.sleep(nanoseconds: 700_000_000)
                app.showSettings = false
                await app.refresh()
            } catch { show("\(error)", error: true) }
            busy = false
            return
        }
        let server = draft.serverURL.trimmingCharacters(in: .whitespaces)
        guard let url = URL(string: server), let scheme = url.scheme, ["wss", "ws"].contains(scheme) else { show("Enter the server address, starting with wss://", error: true); return }
        draft.serverURL = server
        draft.serverPin = draft.serverPin.trimmingCharacters(in: .whitespaces).lowercased()
        if scheme == "wss" && draft.serverPin.count != 64 { show("The server key pin is 64 characters. Copy it from the server's start-up message.", error: true); return }
        draft.allowInsecure = scheme == "ws"
        if draft.deviceName.trimmingCharacters(in: .whitespaces).isEmpty { draft.deviceName = ComputerName.current }
        app.settings = draft
        app.saveSettings()
        busy = true
        show("Connecting…", error: false)
        do {
            try await app.pair(code: code.trimmingCharacters(in: .whitespaces))
            code = ""
            show("Connected. This device is paired with your server.", error: false)
            try? await Task.sleep(nanoseconds: 700_000_000)
            app.showSettings = false
            await app.refresh()
        } catch { show("Could not connect: \(error)", error: true) }
        busy = false
    }

    private func show(_ text: String, error: Bool) { message = text; isError = error }
}
