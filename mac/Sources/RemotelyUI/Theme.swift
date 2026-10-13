import SwiftUI
import AppKit
import CoreText

/// Portico's design tokens (Themes/Dark.xaml and Light.xaml, App.xaml), so Remotely looks like Portico on the Mac too: same colours, type scale, radii and font.
public enum Theme {
    public static let panel = Color(light: 0xF9FAFB, dark: 0x1C2127)
    public static let surface = Color(light: 0xEFF1F3, dark: 0x2B3239)
    public static let well = Color(light: 0xFFFFFF, dark: 0x161A1E)
    public static let hairline = Color(light: 0xE1E5E8, dark: 0x303840)
    public static let controlBorder = Color(light: 0xC5CCD2, dark: 0x3F4851)
    public static let text = Color(light: 0x1F2429, dark: 0xF2F4F5)
    public static let secondary = Color(light: 0x5F6A74, dark: 0xA9B3BA)
    public static let accent = Color(light: 0xE8A93A, dark: 0xF7BE55)
    public static let accentText = Color(light: 0x8A5A00, dark: 0xF7BE55)
    public static let accentHover = Color(light: 0xF0B54F, dark: 0xF9CB74)
    public static let accentPressed = Color(light: 0xD3982F, dark: 0xE2A93B)
    public static let onAccent = Color(light: 0x1E2328, dark: 0x1E2328)
    public static let connected = Color(light: 0x1F7A4D, dark: 0x6FCF97)
    public static let error = Color(light: 0xB3261E, dark: 0xFF9B8A)
    public static let hover = Color(light: 0x000000, dark: 0xFFFFFF).opacity(0.06)

    // Type scale: 17 / 16 / 14 / 13 / 12 / 11
    public static let subject: CGFloat = 17, title: CGFloat = 16, body: CGFloat = 14, line: CGFloat = 13, secondaryText: CGFloat = 12, label: CGFloat = 11
    // Radii
    public static let radiusPanel: CGFloat = 14, radiusCard: CGFloat = 10, radiusControl: CGFloat = 8

    public static func font(_ size: CGFloat, _ weight: Weight = .regular) -> Font { .custom(weight.name, size: size) }

    public enum Weight {
        case regular, medium, semibold
        var name: String {
            switch self { case .regular: return "IBMPlexSans-Regular"; case .medium: return "IBMPlexSans-Medium"; case .semibold: return "IBMPlexSans-SemiBold" }
        }
    }

    /// Registers IBM Plex Sans from the app bundle (Contents/Resources/Fonts, put there by build.sh). If the files are not there (for example under
    /// `swift run`), SwiftUI quietly falls back to the system font.
    public static func registerFonts() {
        guard let folder = Bundle.main.resourceURL?.appendingPathComponent("Fonts"),
              let files = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil) else { return }
        for file in files where file.pathExtension.lowercased() == "ttf" {
            CTFontManagerRegisterFontsForURL(file as CFURL, .process, nil)
        }
    }
}

extension Color {
    init(light: UInt32, dark: UInt32) {
        self.init(nsColor: NSColor(name: nil) { appearance in
            let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            return NSColor(hex: isDark ? dark : light)
        })
    }
}

extension NSColor {
    public convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255, blue: CGFloat(hex & 0xff) / 255, alpha: 1)
    }
}

// MARK: controls (Portico's PrimaryButton, SecondaryButton, IconButton and TextField styles)

public struct PrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    public init() {}
    public func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(Theme.font(Theme.line, .semibold))
            .foregroundColor(Theme.onAccent)
            .padding(.horizontal, 16).frame(height: 32)
            .background(RoundedRectangle(cornerRadius: Theme.radiusControl).fill(configuration.isPressed ? Theme.accentPressed : Theme.accent))
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .opacity(enabled ? 1 : 0.4)
    }
}

public struct SecondaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    public init() {}
    public func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(Theme.font(Theme.secondaryText, .medium))
            .foregroundColor(Theme.text)
            .padding(.horizontal, 10).frame(height: 30)
            .background(RoundedRectangle(cornerRadius: Theme.radiusControl).fill(configuration.isPressed ? Theme.hover : Color.clear))
            .overlay(RoundedRectangle(cornerRadius: Theme.radiusControl).stroke(Theme.controlBorder, lineWidth: 1))
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .opacity(enabled ? 1 : 0.4)
    }
}

public struct IconButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    public init() {}
    public func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 14, weight: .medium))
            .foregroundColor(Theme.secondary)
            .frame(width: 32, height: 32)
            .background(RoundedRectangle(cornerRadius: Theme.radiusControl).fill(configuration.isPressed ? Theme.hover.opacity(2) : Color.clear))
            .opacity(enabled ? 1 : 0.4)
    }
}

public struct FieldStyle: TextFieldStyle {
    public init() {}
    public func _body(configuration: TextField<Self._Label>) -> some View {
        configuration
            .textFieldStyle(.plain)
            .font(Theme.font(Theme.line))
            .foregroundColor(Theme.text)
            .padding(.horizontal, 10).frame(height: 32)
            .background(RoundedRectangle(cornerRadius: Theme.radiusControl).fill(Theme.well))
            .overlay(RoundedRectangle(cornerRadius: Theme.radiusControl).stroke(Theme.controlBorder, lineWidth: 1))
    }
}

/// The Remotely mark (an amber monitor with a pointer), drawn as vectors like the Windows viewer's.
public struct RemotelyMark: View {
    public init() {}
    public var body: some View {
        Canvas { context, size in
            let s = min(size.width, size.height) / 130
            func rect(_ x: Double, _ y: Double, _ w: Double, _ h: Double, _ r: Double) -> Path {
                Path(roundedRect: CGRect(x: x * s, y: y * s, width: w * s, height: h * s), cornerRadius: r * s)
            }
            context.fill(rect(22, 29, 86, 57, 7), with: .linearGradient(Gradient(colors: [Color(hex: 0xFFD277), Color(hex: 0xD98A0E)]), startPoint: .zero, endPoint: CGPoint(x: 0, y: 86 * s)))
            context.fill(rect(27, 34, 76, 46, 3.5), with: .color(Color(hex: 0x161A1E)))
            context.fill(Path { p in
                p.move(to: CGPoint(x: 56 * s, y: 86 * s)); p.addLine(to: CGPoint(x: 74 * s, y: 86 * s))
                p.addLine(to: CGPoint(x: 76 * s, y: 97 * s)); p.addLine(to: CGPoint(x: 54 * s, y: 97 * s)); p.closeSubpath()
            }, with: .color(Color(hex: 0xE09A1C)))
            context.fill(rect(47, 95, 36, 7, 3.3), with: .color(Color(hex: 0xF2B134)))
            let pointer = Path { p in
                let pts: [(Double, Double)] = [(55, 42), (55, 71), (63, 64), (69, 77), (76, 74), (70, 61), (81, 61)]
                p.move(to: CGPoint(x: pts[0].0 * s, y: pts[0].1 * s))
                for q in pts.dropFirst() { p.addLine(to: CGPoint(x: q.0 * s, y: q.1 * s)) }
                p.closeSubpath()
            }
            context.fill(pointer, with: .color(Color(hex: 0xFFFBF0)))
            context.stroke(pointer, with: .color(Color(hex: 0xC9B58E)), lineWidth: 1.2 * s)
        }
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(.sRGB, red: Double((hex >> 16) & 0xff) / 255, green: Double((hex >> 8) & 0xff) / 255, blue: Double(hex & 0xff) / 255, opacity: 1)
    }
}
