import AppKit
import SwiftUI
import RemotelyKit
import RemotelyUI

/// State of one live session: owns the ViewerSession, turns frames into images and routes input, clipboard and files.
@MainActor
final class SessionModel: ObservableObject {
    @Published var image: CGImage?
    @Published var title: String
    @Published var mode = "Connecting"
    @Published var status = ""
    @Published var stats = ""
    @Published var overlayTitle: String? = "Connecting…"
    @Published var overlayText = "Setting up an encrypted connection through your server."
    @Published var canRetry = false
    @Published var canControl = false
    @Published var hasFiles = false
    @Published var hasClipboard = false
    @Published var clipboardOn: Bool
    @Published var sharedFiles: [ShareEntry] = []
    @Published var showFiles = false

    private let app: AppModel
    private let host: DeviceInfo
    private var session: ViewerSession?
    private var clipboardTimer: Timer?
    private var lastClipboard: String?
    private var lastChangeCount = NSPasteboard.general.changeCount
    private let latest = LatestFrame()
    private let rate = Rate()
    private var statsTimer: Timer?
    private var fps = 0
    private var lastSize = (0, 0), lastDesktop = ""
    private var closing = false

    init(app: AppModel, host: DeviceInfo) {
        self.app = app
        self.host = host
        title = host.name
        clipboardOn = app.settings.syncClipboard
    }

    func connect() async {
        overlayTitle = "Connecting…"
        overlayText = "Setting up an encrypted connection through your server."
        canRetry = false
        do {
            let client = try await app.ensureClient()
            let session = try await ViewerSession.open(client: client, identity: app.identity, hostDeviceId: host.deviceId)
            self.session = session
            session.onFrame = { [weak self] frame in self?.receive(frame) }
            session.onClipboard = { [weak self] text in Task { @MainActor in self?.hostClipboard(text) } }
            session.onHostError = { [weak self] message in Task { @MainActor in self?.status = "Host: \(message)" } }
            session.onClosed = { [weak self] reason in Task { @MainActor in self?.ended(reason) } }
            canControl = session.canControl
            hasFiles = session.hasFiles
            hasClipboard = session.hasClipboard
            mode = session.canControl ? "Control" : "View only"
            status = session.canControl ? "Connected. Your keyboard and mouse control this computer while this window has focus."
                                        : "Connected in view-only mode. Ask the owner of that computer to allow control."
            overlayTitle = nil
            session.start()
            startTimers()
        } catch {
            show("Could not connect", friendly(error), retry: true)
            mode = "Offline"
        }
    }

    private func friendly(_ error: Error) -> String {
        let text = "\(error)"
        if text.lowercased().contains("not trusted") { return "That computer does not trust this device yet. On it, run:  trust \(app.identity.deviceId)" }
        return text
    }

    private func show(_ title: String, _ text: String, retry: Bool) { overlayTitle = title; overlayText = text; canRetry = retry }

    private func ended(_ reason: String?) {
        guard !closing else { return }
        session = nil
        canControl = false; hasFiles = false; hasClipboard = false
        mode = "Disconnected"
        show("Disconnected", reason ?? "The other computer ended the session.", retry: true)
        stopTimers()
    }

    func retry() async {
        if let session { await session.close() }
        session = nil
        await connect()
    }

    func close() {
        closing = true
        stopTimers()
        let s = session
        session = nil
        Task { await s?.close() }
    }

    // MARK: frames (decoded off the main thread, newest wins)

    private nonisolated func receive(_ frame: FrameInfo) {
        guard let source = CGImageSourceCreateWithData(Data(frame.jpeg) as CFData, nil), let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return }
        rate.tick()
        if latest.set(image, frame.width, frame.height, frame.desktop) {
            Task { @MainActor in self.present() }
        }
    }

    private func present() {
        guard let (frame, width, height, desktop) = latest.take() else { return }
        image = frame
        if lastSize != (width, height) || lastDesktop != desktop { lastSize = (width, height); lastDesktop = desktop; updateStats() }
    }

    private func updateStats() {
        let screen = lastDesktop.caseInsensitiveCompare("Winlogon") == .orderedSame ? "Lock / sign-in screen" : "Desktop"
        stats = "\(screen) · \(lastSize.0)×\(lastSize.1) · \(fps) fps"
    }

    // MARK: input, clipboard, files

    func send(_ event: InputEvent) { session?.sendInput(event) }
    func releaseAll() { session?.releaseAllInput() }
    func sendSecureAttention() { Task { try? await session?.sendSecureAttention(); status = "Sent Ctrl+Alt+Del." } }
    var canSendSecureAttention: Bool { session?.canSendSecureAttention ?? false }

    private func startTimers() {
        clipboardTimer = Timer.scheduledTimer(withTimeInterval: 0.6, repeats: true) { [weak self] _ in Task { @MainActor in self?.pollClipboard() } }
        statsTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in Task { @MainActor in self?.fps = self?.rate.reset() ?? 0; self?.updateStats() } }
    }

    private func stopTimers() { clipboardTimer?.invalidate(); statsTimer?.invalidate(); clipboardTimer = nil; statsTimer = nil }

    private func pollClipboard() {
        let board = NSPasteboard.general
        guard clipboardOn, hasClipboard, board.changeCount != lastChangeCount else { return }
        lastChangeCount = board.changeCount
        guard let text = board.string(forType: .string), !text.isEmpty, text != lastClipboard else { return }
        lastClipboard = text
        Task { try? await session?.sendClipboard(text) }
    }

    private func hostClipboard(_ text: String) {
        guard clipboardOn, text != lastClipboard else { return }
        lastClipboard = text // remember first so our own poll does not send it straight back
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(text, forType: .string)
        lastChangeCount = board.changeCount
    }

    func upload(_ urls: [URL]) {
        guard let session, session.hasFiles else { return }
        Task {
            for url in urls {
                let name = url.lastPathComponent
                do {
                    let saved = try await session.uploadFile(at: url) { [weak self] p in Task { @MainActor in self?.status = "Sending \(name)… \(Int(p * 100))%" } }
                    status = "Sent \(name). It is in the Received folder on \(host.name) as \(saved)."
                } catch { status = "Could not send \(name): \(error)" }
            }
        }
    }

    func chooseAndUpload() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.message = "Send files to \(host.name)"
        if panel.runModal() == .OK { upload(panel.urls) }
    }

    func openSharedFiles() {
        guard let session else { return }
        Task {
            do {
                sharedFiles = try await session.listSharedFiles()
                if sharedFiles.isEmpty { status = "\(host.name) has nothing in its shared folder. Put files in Documents/Remotely/Share on that computer." }
                else { showFiles = true }
            } catch { status = "Could not list shared files: \(error)" }
        }
    }

    func download(_ entry: ShareEntry) {
        guard let session else { return }
        showFiles = false
        status = "Downloading \(entry.Name)…"
        Task {
            do {
                let path = try await session.downloadFile(named: entry.Name)
                status = "Saved to \(path)"
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
            } catch { status = "Download failed: \(error)" }
        }
    }

    func toggleClipboard() { clipboardOn.toggle() }
}

/// Holds only the newest frame so a slow UI never queues stale ones.
private final class LatestFrame: @unchecked Sendable {
    private let lock = NSLock()
    private var value: (CGImage, Int, Int, String)?
    private var queued = false

    /// Stores a frame; returns true if the caller should schedule a UI update.
    func set(_ image: CGImage, _ w: Int, _ h: Int, _ desktop: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        value = (image, w, h, desktop)
        if queued { return false }
        queued = true
        return true
    }

    func take() -> (CGImage, Int, Int, String)? {
        lock.lock(); defer { lock.unlock() }
        queued = false
        defer { value = nil }
        return value
    }
}

private final class Rate: @unchecked Sendable {
    private let lock = NSLock()
    private var n = 0
    func tick() { lock.lock(); n += 1; lock.unlock() }
    func reset() -> Int { lock.lock(); defer { lock.unlock() }; let c = n; n = 0; return c }
}

struct RemoteScreen: NSViewRepresentable {
    @ObservedObject var model: SessionModel
    let commandAsControl: Bool

    func makeNSView(context: Context) -> RemoteScreenView {
        let view = RemoteScreenView()
        view.onInput = { [weak model] e in Task { @MainActor in model?.send(e) } }
        view.onFocusLost = { [weak model] in Task { @MainActor in model?.releaseAll() } }
        view.onDropFiles = { [weak model] urls in Task { @MainActor in model?.upload(urls) } }
        return view
    }

    func updateNSView(_ view: RemoteScreenView, context: Context) {
        view.image = model.image
        view.canControl = model.canControl
        view.commandAsControl = commandAsControl
    }
}

struct SessionView: View {
    @ObservedObject var model: SessionModel
    let commandAsControl: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                RemotelyMark().frame(width: 24, height: 24)
                Text(model.title).font(Theme.font(Theme.body, .semibold)).foregroundColor(Theme.text)
                Text(model.mode).font(Theme.font(Theme.label, .semibold)).foregroundColor(Theme.secondary)
                    .padding(.horizontal, 8).padding(.vertical, 2)
                    .background(Capsule().fill(Theme.surface))
                Spacer()
                if model.canSendSecureAttention { Button { model.sendSecureAttention() } label: { Image(systemName: "keyboard") }.buttonStyle(IconButtonStyle()).help("Send Ctrl+Alt+Del") }
                Button { model.chooseAndUpload() } label: { Image(systemName: "square.and.arrow.up") }.buttonStyle(IconButtonStyle()).disabled(!model.hasFiles).help("Send a file to this computer")
                Button { model.openSharedFiles() } label: { Image(systemName: "arrow.down.to.line") }.buttonStyle(IconButtonStyle()).disabled(!model.hasFiles).help("Get a file from this computer's shared folder")
                Button { model.toggleClipboard() } label: { Image(systemName: "doc.on.clipboard") }.buttonStyle(IconButtonStyle())
                    .opacity(model.clipboardOn ? 1 : 0.5).disabled(!model.hasClipboard).help(model.clipboardOn ? "Clipboard sharing is on" : "Clipboard sharing is off")
            }
            .padding(.horizontal, 14).frame(height: 44)
            Divider().overlay(Theme.hairline)

            ZStack {
                RemoteScreen(model: model, commandAsControl: commandAsControl)
                if let title = model.overlayTitle {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(title).font(Theme.font(Theme.subject, .semibold)).foregroundColor(Theme.text)
                        Text(model.overlayText).font(Theme.font(Theme.line)).foregroundColor(Theme.secondary).fixedSize(horizontal: false, vertical: true)
                        if model.canRetry { Button("Reconnect") { Task { await model.retry() } }.buttonStyle(PrimaryButtonStyle()).padding(.top, 8) }
                    }
                    .padding(.horizontal, 24).padding(.vertical, 18).frame(maxWidth: 460)
                    .background(RoundedRectangle(cornerRadius: Theme.radiusCard).fill(Theme.surface))
                }
            }

            Divider().overlay(Theme.hairline)
            HStack {
                Text(model.status).font(Theme.font(Theme.label)).foregroundColor(Theme.secondary).lineLimit(1).truncationMode(.tail)
                Spacer()
                Text(model.stats).font(Theme.font(Theme.label)).foregroundColor(Theme.secondary)
            }
            .padding(.horizontal, 14).padding(.vertical, 6)
        }
        .background(Theme.panel)
        .sheet(isPresented: $model.showFiles) {
            VStack(alignment: .leading, spacing: 0) {
                Text("Shared files").font(Theme.font(Theme.title, .semibold)).foregroundColor(Theme.text).padding(16)
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(model.sharedFiles, id: \.Name) { entry in
                            Button { model.download(entry) } label: {
                                HStack {
                                    Text(entry.Name).lineLimit(1).truncationMode(.middle)
                                    Spacer()
                                    Text(ByteCountFormatter.string(fromByteCount: entry.Size, countStyle: .file)).foregroundColor(Theme.secondary)
                                }
                                .font(Theme.font(Theme.line)).foregroundColor(Theme.text).padding(.horizontal, 16).padding(.vertical, 9)
                            }.buttonStyle(.plain)
                            Divider().overlay(Theme.hairline)
                        }
                    }
                }
                Text("Only files in the shared folder on that computer are listed.").font(Theme.font(Theme.label)).foregroundColor(Theme.secondary).padding(16)
                HStack { Spacer(); Button("Cancel") { model.showFiles = false }.buttonStyle(SecondaryButtonStyle()) }.padding([.horizontal, .bottom], 16)
            }
            .frame(width: 420, height: 400).background(Theme.panel)
        }
    }
}

/// One AppKit window per session. The remote screen is an NSView so it gets raw keyboard and mouse events.
@MainActor
final class SessionWindowController: NSObject, NSWindowDelegate {
    private static var controllers = Set<SessionWindowController>()
    private let window: NSWindow
    private let model: SessionModel

    static func open(model app: AppModel, host: DeviceInfo) {
        let controller = SessionWindowController(app: app, host: host)
        controllers.insert(controller)
        controller.window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        Task { await controller.model.connect() }
    }

    private init(app: AppModel, host: DeviceInfo) {
        model = SessionModel(app: app, host: host)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "Remotely – \(host.name)"
        window.minSize = NSSize(width: 640, height: 420)
        window.center()
        window.contentView = NSHostingView(rootView: SessionView(model: model, commandAsControl: app.settings.commandAsControl).preferredColorScheme(app.colorScheme))
        window.isReleasedWhenClosed = false
        window.delegate = self
    }

    func windowWillClose(_ notification: Notification) {
        model.close()
        Self.controllers.remove(self)
    }
}
