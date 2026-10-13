import Foundation
import AppKit
import ApplicationServices
import ImageIO
import UniformTypeIdentifiers
import ScreenCaptureKit
import RemotelyKit

// MARK: JPEG

func encodeJPEG(_ image: CGImage) throws -> [UInt8] {
    for quality in [0.6, 0.45, 0.3, 0.15] {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil) else { throw HostError.capture("Could not create a JPEG encoder.") }
        CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { throw HostError.capture("JPEG encoding failed.") }
        if data.length <= 900_000 { return [UInt8](data as Data) }
    }
    throw HostError.capture("The screen is too detailed to send as one frame yet.")
}

// MARK: screen

/// Captures the main display with ScreenCaptureKit (macOS 14+). Needs the Screen Recording permission.
@available(macOS 14.0, *)
final class ScreenCapturer: FrameSource {
    func capture() async throws -> FrameInfo {
        guard CGPreflightScreenCaptureAccess() else {
            _ = CGRequestScreenCaptureAccess()
            throw HostError.permission("Screen Recording is not allowed yet. Open System Settings > Privacy & Security > Screen Recording, turn on Remotely Host, then restart it.")
        }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first else {
            throw HostError.capture("No display found.")
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let scale = Double(max(1, filter.pointPixelScale))
        var width = Int(Double(display.width) * scale)
        var height = Int(Double(display.height) * scale)
        if width > 2560 { // keep frames small enough for one relayed message
            height = Int(Double(height) * 2560.0 / Double(width))
            width = 2560
        }
        let configuration = SCStreamConfiguration()
        configuration.width = width
        configuration.height = height
        configuration.showsCursor = true
        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
        return FrameInfo(width: image.width, height: image.height, desktop: "Default", jpeg: try encodeJPEG(image))
    }
}

struct UnsupportedCapturer: FrameSource {
    func capture() async throws -> FrameInfo { throw HostError.capture("Screen capture needs macOS 14 (Sonoma) or newer on this Mac.") }
}

/// A synthetic screen for tests: a gradient with a counter that changes every few requests, so "unchanged" detection is exercised too.
final class FakeScreen: FrameSource, @unchecked Sendable {
    private let lock = NSLock()
    private var requests = 0

    func capture() async throws -> FrameInfo {
        lock.lock(); requests += 1; let tick = requests / 3; lock.unlock()
        let width = 640, height = 480
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { throw HostError.capture("No drawing context.") }
        context.setFillColor(CGColor(red: Double(tick % 10) / 10, green: 0.5, blue: 0.8, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        guard let image = context.makeImage() else { throw HostError.capture("No image.") }
        return FrameInfo(width: width, height: height, desktop: "Default", jpeg: try encodeJPEG(image))
    }
}

// MARK: input

/// Injects viewer input with CGEvent. Needs the Accessibility permission. Mouse positions are normalised over the main display.
final class MacInput: InputSink, @unchecked Sendable {
    private let lock = NSLock()
    private let ctrlAsCommand: () -> Bool
    private var modifiersDown: Set<KeyMap.Modifier> = []
    private var keysDown: Set<UInt16> = []        // macOS key codes of non-modifier keys held
    private var buttonsDown: Set<MouseButton> = []
    private var lastClick = (time: Date.distantPast, x: CGFloat(0), y: CGFloat(0), count: 0)
    private let source = CGEventSource(stateID: .hidSystemState)

    init(ctrlAsCommand: @escaping () -> Bool) { self.ctrlAsCommand = ctrlAsCommand }

    func inject(_ events: [InputEvent]) async throws {
        guard AXIsProcessTrusted() else {
            _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
            throw HostError.permission("Accessibility is not allowed yet. Open System Settings > Privacy & Security > Accessibility, turn on Remotely Host, then restart it.")
        }
        lock.lock()
        defer { lock.unlock() }
        for e in events { apply(e) }
    }

    private func position(_ e: InputEvent) -> CGPoint {
        let b = CGDisplayBounds(CGMainDisplayID())
        return CGPoint(x: b.minX + CGFloat(e.x) / 65535 * b.width, y: b.minY + CGFloat(e.y) / 65535 * b.height)
    }

    private var flags: CGEventFlags {
        var f = CGEventFlags()
        if modifiersDown.contains(.shift) { f.insert(.maskShift) }
        if modifiersDown.contains(.control) { f.insert(.maskControl) }
        if modifiersDown.contains(.option) { f.insert(.maskAlternate) }
        if modifiersDown.contains(.command) { f.insert(.maskCommand) }
        return f
    }

    private func post(_ event: CGEvent?) { event?.post(tap: .cghidEventTap) }

    private func apply(_ e: InputEvent) {
        switch e.kind {
        case .mouseMove:
            let type: CGEventType = buttonsDown.contains(.left) ? .leftMouseDragged : buttonsDown.contains(.right) ? .rightMouseDragged : buttonsDown.isEmpty ? .mouseMoved : .otherMouseDragged
            let button: CGMouseButton = buttonsDown.contains(.left) ? .left : buttonsDown.contains(.right) ? .right : .center
            post(CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: position(e), mouseButton: button))
        case .mouseDown, .mouseUp:
            guard let button = MouseButton(rawValue: e.arg1) else { return }
            let down = e.kind == .mouseDown
            let p = position(e)
            let type: CGEventType
            let cgButton: CGMouseButton
            switch button {
            case .left: type = down ? .leftMouseDown : .leftMouseUp; cgButton = .left
            case .right: type = down ? .rightMouseDown : .rightMouseUp; cgButton = .right
            default: type = down ? .otherMouseDown : .otherMouseUp; cgButton = .center
            }
            let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: cgButton)
            if button == .x1 || button == .x2 { event?.setIntegerValueField(.mouseEventButtonNumber, value: button == .x1 ? 3 : 4) }
            if down {
                let now = Date()
                if now.timeIntervalSince(lastClick.time) < 0.4, abs(p.x - lastClick.x) < 5, abs(p.y - lastClick.y) < 5 { lastClick.count += 1 } else { lastClick.count = 1 }
                lastClick.time = now; lastClick.x = p.x; lastClick.y = p.y
                buttonsDown.insert(button)
            } else { buttonsDown.remove(button) }
            event?.setIntegerValueField(.mouseEventClickState, value: Int64(lastClick.count))
            event?.flags = flags
            post(event)
        case .wheel:
            // Windows sends multiples of 120 per notch; one notch scrolls three lines.
            let lines = Int32((Double(e.wheelDelta) / 120.0 * 3.0).rounded(.awayFromZero))
            guard lines != 0 else { return }
            let horizontal = (e.flags & InputEvent.flagHorizontal) != 0
            post(CGEvent(scrollWheelEvent2Source: source, units: .line, wheelCount: 2, wheel1: horizontal ? 0 : lines, wheel2: horizontal ? -lines : 0, wheel3: 0))
        case .keyDown, .keyUp:
            let down = e.kind == .keyDown
            if let modifier = KeyMap.modifier(forVirtualKey: e.arg1, ctrlAsCommand: ctrlAsCommand()) {
                if down { modifiersDown.insert(modifier) } else { modifiersDown.remove(modifier) }
                let event = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(KeyMap.macKeyCode(for: modifier)), keyDown: down)
                event?.flags = flags
                post(event)
            } else if let code = KeyMap.macKeyCode(forVirtualKey: e.arg1) {
                if down { keysDown.insert(code) } else { keysDown.remove(code) }
                let event = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(code), keyDown: down)
                event?.flags = flags
                post(event)
            }
        case .releaseAll:
            for code in keysDown { let ev = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(code), keyDown: false); ev?.flags = []; post(ev) }
            for m in modifiersDown { post(CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(KeyMap.macKeyCode(for: m)), keyDown: false)) }
            for b in buttonsDown {
                let p = CGEvent(source: nil)?.location ?? .zero
                let type: CGEventType = b == .left ? .leftMouseUp : b == .right ? .rightMouseUp : .otherMouseUp
                post(CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: b == .left ? .left : b == .right ? .right : .center))
            }
            keysDown.removeAll(); modifiersDown.removeAll(); buttonsDown.removeAll()
        }
    }
}

/// Prints what it receives instead of acting on it (tests).
final class PrintingInput: InputSink, @unchecked Sendable {
    func inject(_ events: [InputEvent]) async throws {
        for e in events { print("INPUT \(e.kind) x=\(e.x) y=\(e.y) a1=\(e.arg1) a2=\(e.arg2) flags=\(e.flags)"); fflush(stdout) }
    }
}

// MARK: clipboard

final class MacClipboard: ClipboardSync, @unchecked Sendable {
    func read() async throws -> (sequence: Int64, text: String?) {
        let board = NSPasteboard.general
        return (Int64(board.changeCount), board.string(forType: .string))
    }

    func write(_ text: String) async throws -> Int64 {
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(text, forType: .string)
        return Int64(board.changeCount)
    }
}

final class FakeClipboard: ClipboardSync, @unchecked Sendable {
    private let lock = NSLock()
    private var sequence: Int64 = 1
    private var text = "fake host clipboard"

    func read() async throws -> (sequence: Int64, text: String?) { lock.lock(); defer { lock.unlock() }; return (sequence, text) }

    func write(_ newText: String) async throws -> Int64 {
        lock.lock(); text = newText; sequence += 1; let s = sequence; lock.unlock()
        print("CLIPBOARD set: \(newText)"); fflush(stdout)
        return s
    }
}
