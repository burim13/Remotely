import Foundation

/// Keyboard translation between the protocol's currency (Windows virtual-key codes) and macOS key codes (US layout, kVK_* values).
/// Modifier roles follow positions people expect on each platform: Command <-> Ctrl (so Cmd+C on a Mac is Ctrl+C on Windows), Option <-> Alt, Control <-> Windows key.
public enum KeyMap {
    // (windows virtual key, macOS key code)
    private static let pairs: [(UInt16, UInt16)] = [
        // letters
        (0x41, 0x00), (0x53, 0x01), (0x44, 0x02), (0x46, 0x03), (0x48, 0x04), (0x47, 0x05), (0x5A, 0x06), (0x58, 0x07), (0x43, 0x08), (0x56, 0x09),
        (0x42, 0x0B), (0x51, 0x0C), (0x57, 0x0D), (0x45, 0x0E), (0x52, 0x0F), (0x59, 0x10), (0x54, 0x11), (0x4F, 0x1F), (0x55, 0x20), (0x49, 0x22),
        (0x50, 0x23), (0x4C, 0x25), (0x4A, 0x26), (0x4B, 0x28), (0x4E, 0x2D), (0x4D, 0x2E),
        // digits
        (0x31, 0x12), (0x32, 0x13), (0x33, 0x14), (0x34, 0x15), (0x36, 0x16), (0x35, 0x17), (0x39, 0x19), (0x37, 0x1A), (0x38, 0x1C), (0x30, 0x1D),
        // punctuation: = - ] [ ' ; \ , / . `
        (0xBB, 0x18), (0xBD, 0x1B), (0xDD, 0x1E), (0xDB, 0x21), (0xDE, 0x27), (0xBA, 0x29), (0xDC, 0x2A), (0xBC, 0x2B), (0xBF, 0x2C), (0xBE, 0x2F), (0xC0, 0x32),
        // editing and navigation
        (0x0D, 0x24), (0x09, 0x30), (0x20, 0x31), (0x08, 0x33), (0x1B, 0x35), (0x2E, 0x75), (0x2D, 0x72), (0x24, 0x73), (0x23, 0x77), (0x21, 0x74), (0x22, 0x79),
        (0x25, 0x7B), (0x27, 0x7C), (0x28, 0x7D), (0x26, 0x7E), (0x14, 0x39),
        // function keys
        (0x70, 0x7A), (0x71, 0x78), (0x72, 0x63), (0x73, 0x76), (0x74, 0x60), (0x75, 0x61), (0x76, 0x62), (0x77, 0x64), (0x78, 0x65), (0x79, 0x6D), (0x7A, 0x67), (0x7B, 0x6F),
        // keypad
        (0x60, 0x52), (0x61, 0x53), (0x62, 0x54), (0x63, 0x55), (0x64, 0x56), (0x65, 0x57), (0x66, 0x58), (0x67, 0x59), (0x68, 0x5B), (0x69, 0x5C),
        (0x6A, 0x43), (0x6B, 0x45), (0x6D, 0x4E), (0x6E, 0x41), (0x6F, 0x4B), (0x90, 0x47),
    ]

    private static let vkToMac: [UInt16: UInt16] = Dictionary(uniqueKeysWithValues: pairs.map { ($0.0, $0.1) })
    private static let macToVk: [UInt16: UInt16] = Dictionary(uniqueKeysWithValues: pairs.map { ($0.1, $0.0) })

    /// macOS key code for a non-modifier Windows virtual key.
    public static func macKeyCode(forVirtualKey vk: UInt16) -> UInt16? { vkToMac[vk] }

    /// Windows virtual key for a non-modifier macOS key code.
    public static func virtualKey(forMacKeyCode code: UInt16) -> UInt16? { macToVk[code] }

    public enum Modifier: Sendable { case shift, control, option, command }

    /// Which modifier a Windows virtual key stands for on a Mac host. `ctrlAsCommand` swaps the roles as described above.
    public static func modifier(forVirtualKey vk: UInt16, ctrlAsCommand: Bool = true) -> Modifier? {
        switch vk {
        case 0x10, 0xA0, 0xA1: return .shift
        case 0x11, 0xA2, 0xA3: return ctrlAsCommand ? .command : .control
        case 0x12, 0xA4, 0xA5: return .option
        case 0x5B, 0x5C: return ctrlAsCommand ? .control : .command
        default: return nil
        }
    }

    /// macOS key code of a modifier's physical key (used when a Mac host presses the key itself).
    public static func macKeyCode(for modifier: Modifier) -> UInt16 {
        switch modifier {
        case .shift: return 0x38
        case .control: return 0x3B
        case .option: return 0x3A
        case .command: return 0x37
        }
    }

    /// For a Mac viewer: the Windows virtual key a modifier key code should send, or nil if it is not a modifier.
    public static func virtualKey(forMacModifierKeyCode code: UInt16, commandAsControl: Bool = true) -> UInt16? {
        switch code {
        case 0x38: return 0xA0                                  // left shift
        case 0x3C: return 0xA1                                  // right shift
        case 0x37: return commandAsControl ? 0xA2 : 0x5B        // left command
        case 0x36: return commandAsControl ? 0xA3 : 0x5C        // right command
        case 0x3A: return 0xA4                                  // left option -> Alt
        case 0x3D: return 0xA5                                  // right option -> Alt
        case 0x3B: return commandAsControl ? 0x5B : 0xA2        // left control -> Windows key
        case 0x3E: return commandAsControl ? 0x5C : 0xA3        // right control -> Windows key
        default: return nil
        }
    }

    /// Windows "extended key" flag for a virtual key.
    public static func isExtended(virtualKey vk: UInt16) -> Bool {
        switch vk {
        case 0x21...0x28, 0x2D, 0x2E, 0xA3, 0xA5, 0x6F, 0x90, 0x2C, 0x5B, 0x5C, 0x5D: return true // PgUp..Down, Ins, Del, RCtrl, RAlt, numpad /, NumLock, PrtSc, Win, Apps
        default: return false
        }
    }
}
