import AppKit
import RemotelyKit

/// Draws the remote screen scaled to fit and turns local mouse and keyboard input into InputEvents. Positions are sent normalised (0..65535)
/// across the remote desktop, so the viewer needs no knowledge of the host's resolution.
final class RemoteScreenView: NSView {
    var image: CGImage? { didSet { needsDisplay = true } }
    var canControl = false
    var commandAsControl = true
    var onInput: ((InputEvent) -> Void)?
    var onFocusLost: (() -> Void)?
    var onDropFiles: (([URL]) -> Void)?
    private var imageRect = CGRect.zero
    private var trackingArea: NSTrackingArea?

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { true }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        registerForDraggedTypes([.fileURL])
        wantsLayer = true
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect, .mouseEnteredAndExited], owner: self, userInfo: nil)
        addTrackingArea(area)
        trackingArea = area
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(name: nil) { $0.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? NSColor(hex: 0x161A1E) : NSColor(hex: 0xFFFFFF) }.setFill()
        dirtyRect.fill()
        guard let image, let context = NSGraphicsContext.current?.cgContext, bounds.width > 0, bounds.height > 0 else { return }
        let scale = min(bounds.width / CGFloat(image.width), bounds.height / CGFloat(image.height))
        let w = CGFloat(image.width) * scale, h = CGFloat(image.height) * scale
        imageRect = CGRect(x: (bounds.width - w) / 2, y: (bounds.height - h) / 2, width: w, height: h)
        context.saveGState()
        context.interpolationQuality = scale < 1 ? .high : .none
        // The view is flipped; CGContext.draw expects an unflipped image, so flip around the image rectangle.
        context.translateBy(x: imageRect.minX, y: imageRect.maxY)
        context.scaleBy(x: 1, y: -1)
        context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        context.restoreGState()
    }

    // MARK: mouse

    private func normalised(_ event: NSEvent) -> (UInt16, UInt16)? {
        guard imageRect.width > 0, imageRect.height > 0 else { return nil }
        let p = convert(event.locationInWindow, from: nil)
        let nx = min(max((p.x - imageRect.minX) / imageRect.width, 0), 1)
        let ny = min(max((p.y - imageRect.minY) / imageRect.height, 0), 1)
        return (UInt16((nx * 65535).rounded()), UInt16((ny * 65535).rounded()))
    }

    override func mouseMoved(with event: NSEvent) { if canControl, let (x, y) = normalised(event) { onInput?(.move(x, y)) } }
    override func mouseDragged(with event: NSEvent) { mouseMoved(with: event) }
    override func rightMouseDragged(with event: NSEvent) { mouseMoved(with: event) }
    override func otherMouseDragged(with event: NSEvent) { mouseMoved(with: event) }

    private func button(_ event: NSEvent) -> MouseButton {
        switch event.buttonNumber { case 0: return .left; case 1: return .right; case 2: return .middle; case 3: return .x1; default: return .x2 }
    }

    private func press(_ event: NSEvent, down: Bool) {
        window?.makeFirstResponder(self)
        guard canControl, let (x, y) = normalised(event) else { return }
        onInput?(down ? .down(button(event), x, y) : .up(button(event), x, y))
    }

    override func mouseDown(with event: NSEvent) { press(event, down: true) }
    override func mouseUp(with event: NSEvent) { press(event, down: false) }
    override func rightMouseDown(with event: NSEvent) { press(event, down: true) }
    override func rightMouseUp(with event: NSEvent) { press(event, down: false) }
    override func otherMouseDown(with event: NSEvent) { press(event, down: true) }
    override func otherMouseUp(with event: NSEvent) { press(event, down: false) }

    override func scrollWheel(with event: NSEvent) {
        guard canControl, let (x, y) = normalised(event) else { return }
        // Trackpads report fine-grained pixels; mice report lines. Windows expects 120 units per notch.
        let unit: CGFloat = event.hasPreciseScrollingDeltas ? 3 : 40
        let vertical = Int16(max(-32000, min(32000, (event.scrollingDeltaY * unit).rounded())))
        let horizontal = Int16(max(-32000, min(32000, (event.scrollingDeltaX * -unit).rounded())))
        if vertical != 0 { onInput?(.scroll(vertical, x, y)) }
        if horizontal != 0 { onInput?(.scroll(horizontal, x, y, horizontal: true)) }
    }

    // MARK: keyboard

    override func keyDown(with event: NSEvent) { sendKey(event, down: true) }
    override func keyUp(with event: NSEvent) { sendKey(event, down: false) }

    /// Command shortcuts normally go to the menu bar; while this view has focus they belong to the remote computer.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard canControl, window?.firstResponder === self else { return false }
        if event.type == .keyDown { sendKey(event, down: true) }
        return true
    }

    override func flagsChanged(with event: NSEvent) {
        guard canControl, let vk = KeyMap.virtualKey(forMacModifierKeyCode: event.keyCode, commandAsControl: commandAsControl) else { return }
        let flag: NSEvent.ModifierFlags
        switch event.keyCode {
        case 0x38, 0x3C: flag = .shift
        case 0x37, 0x36: flag = .command
        case 0x3A, 0x3D: flag = .option
        case 0x3B, 0x3E: flag = .control
        default: return
        }
        onInput?(.key(down: event.modifierFlags.contains(flag), virtualKey: vk, scanCode: 0, extended: KeyMap.isExtended(virtualKey: vk)))
    }

    private func sendKey(_ event: NSEvent, down: Bool) {
        guard canControl, let vk = KeyMap.virtualKey(forMacKeyCode: event.keyCode) else { return }
        onInput?(.key(down: down, virtualKey: vk, scanCode: 0, extended: KeyMap.isExtended(virtualKey: vk)))
    }

    override func resignFirstResponder() -> Bool {
        onFocusLost?() // let go of everything so no key or button is left stuck down on the remote computer
        return super.resignFirstResponder()
    }

    // MARK: drag and drop

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { canControl ? .copy : [] }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty else { return false }
        onDropFiles?(urls)
        return true
    }
}
