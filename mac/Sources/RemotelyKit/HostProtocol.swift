import Foundation
import CryptoKit

/// Message kinds inside the encrypted viewer/host session. First byte of every message. Same values as the .NET HostMessages.
public enum HostMessages {
    // viewer -> host
    public static let frameRequest: UInt8 = 0x01
    public static let input: UInt8 = 0x02
    public static let clipboard: UInt8 = 0x03
    public static let secureAttention: UInt8 = 0x04
    public static let fileStart: UInt8 = 0x10
    public static let fileChunk: UInt8 = 0x11
    public static let fileEnd: UInt8 = 0x12
    public static let shareList: UInt8 = 0x13
    public static let shareGet: UInt8 = 0x14
    // host -> viewer
    public static let hello: UInt8 = 0x80
    public static let frameReply: UInt8 = 0x81
    public static let unchanged: UInt8 = 0x82
    public static let clipboardReply: UInt8 = 0x83
    public static let fileAck: UInt8 = 0x90
    public static let shareListReply: UInt8 = 0x91
    public static let shareFileStart: UInt8 = 0x92
    public static let shareFileChunk: UInt8 = 0x93
    public static let shareFileEnd: UInt8 = 0x94
    public static let error: UInt8 = 0xFF

    public static let protocolVersion = 1
    public static let maxClipboardChars = 256 * 1024
    public static let fileChunkBytes = 512 * 1024
    public static let maxFileBytes: Int64 = 512 * 1024 * 1024

    public static func prefixed(_ kind: UInt8, _ body: [UInt8]) -> [UInt8] { [kind] + body }

    public static func withId(_ kind: UInt8, _ id: UInt32, _ payload: [UInt8]) -> [UInt8] {
        var m: [UInt8] = [kind]
        m.appendUInt32BE(id)
        m.append(contentsOf: payload)
        return m
    }

    public static func readId(_ message: [UInt8]) -> (id: UInt32, rest: [UInt8])? {
        guard message.count >= 5 else { return nil }
        return (message.readUInt32BE(at: 1), Array(message[5...]))
    }
}

public struct HostCapabilities: OptionSet, Sendable {
    public let rawValue: UInt8
    public init(rawValue: UInt8) { self.rawValue = rawValue }
    public static let control = HostCapabilities(rawValue: 1)
    public static let secureAttention = HostCapabilities(rawValue: 2)
    public static let clipboard = HostCapabilities(rawValue: 4)
    public static let files = HostCapabilities(rawValue: 8)
}

public struct HelloInfo: Sendable {
    public let version: Int
    public let capabilities: HostCapabilities
    public init(version: Int, capabilities: HostCapabilities) { self.version = version; self.capabilities = capabilities }
    public func encode() -> [UInt8] { [HostMessages.hello, UInt8(version), capabilities.rawValue] }
    public static func decode(_ m: [UInt8]) -> HelloInfo? {
        m.count >= 3 && m[0] == HostMessages.hello ? HelloInfo(version: Int(m[1]), capabilities: HostCapabilities(rawValue: m[2])) : nil
    }
}

/// One captured screen image. `desktop` is "Default" for a normal screen ("Winlogon" is the Windows lock screen).
public struct FrameInfo: Sendable {
    public let width: Int
    public let height: Int
    public let desktop: String
    public let jpeg: [UInt8]
    public init(width: Int, height: Int, desktop: String, jpeg: [UInt8]) { self.width = width; self.height = height; self.desktop = desktop; self.jpeg = jpeg }
}

/// ver(1) w(i32 BE) h(i32 BE) desktopLen(1) desktop jpegLen(u32 BE) jpeg. Identical to the .NET FrameCodec.
public enum FrameCodec {
    public static func encode(_ f: FrameInfo) -> [UInt8] {
        let desktop = Array(f.desktop.utf8.prefix(64))
        var out: [UInt8] = [1]
        out.appendUInt32BE(UInt32(f.width))
        out.appendUInt32BE(UInt32(f.height))
        out.append(UInt8(desktop.count))
        out.append(contentsOf: desktop)
        out.appendUInt32BE(UInt32(f.jpeg.count))
        out.append(contentsOf: f.jpeg)
        return out
    }

    public static func decode(_ d: [UInt8]) throws -> FrameInfo {
        guard d.count >= 14, d[0] == 1 else { throw RemotelyError.malformed("Not a frame.") }
        let width = Int(Int32(bitPattern: d.readUInt32BE(at: 1)))
        let height = Int(Int32(bitPattern: d.readUInt32BE(at: 5)))
        guard (1...32768).contains(width), (1...32768).contains(height) else { throw RemotelyError.malformed("Bad frame size.") }
        let nameLength = Int(d[9])
        guard d.count >= 14 + nameLength else { throw RemotelyError.malformed("Truncated frame.") }
        let desktop = String(decoding: d[10..<(10 + nameLength)], as: UTF8.self)
        let jpegLength = Int(d.readUInt32BE(at: 10 + nameLength))
        guard jpegLength == d.count - 14 - nameLength else { throw RemotelyError.malformed("Frame length mismatch.") }
        return FrameInfo(width: width, height: height, desktop: desktop, jpeg: Array(d[(14 + nameLength)...]))
    }
}

public enum InputKind: UInt8, Sendable {
    case mouseMove = 1, mouseDown, mouseUp, wheel, keyDown, keyUp, releaseAll
}

public enum MouseButton: UInt16, Sendable { case left = 0, right, middle, x1, x2 }

/// Mouse positions are normalised 0..65535 across the whole captured desktop; keys carry the Windows virtual-key code (the common currency of the protocol).
public struct InputEvent: Equatable, Sendable {
    public static let wireSize = 10
    public static let flagExtended: UInt8 = 1
    public static let flagHorizontal: UInt8 = 1

    public var kind: InputKind
    public var x: UInt16
    public var y: UInt16
    public var arg1: UInt16
    public var arg2: UInt16
    public var flags: UInt8

    public init(kind: InputKind, x: UInt16 = 0, y: UInt16 = 0, arg1: UInt16 = 0, arg2: UInt16 = 0, flags: UInt8 = 0) {
        self.kind = kind; self.x = x; self.y = y; self.arg1 = arg1; self.arg2 = arg2; self.flags = flags
    }

    public static func move(_ x: UInt16, _ y: UInt16) -> InputEvent { InputEvent(kind: .mouseMove, x: x, y: y) }
    public static func down(_ b: MouseButton, _ x: UInt16, _ y: UInt16) -> InputEvent { InputEvent(kind: .mouseDown, x: x, y: y, arg1: b.rawValue) }
    public static func up(_ b: MouseButton, _ x: UInt16, _ y: UInt16) -> InputEvent { InputEvent(kind: .mouseUp, x: x, y: y, arg1: b.rawValue) }
    public static func scroll(_ delta: Int16, _ x: UInt16, _ y: UInt16, horizontal: Bool = false) -> InputEvent {
        InputEvent(kind: .wheel, x: x, y: y, arg1: UInt16(bitPattern: delta), flags: horizontal ? flagHorizontal : 0)
    }
    public static func key(down: Bool, virtualKey: UInt16, scanCode: UInt16 = 0, extended: Bool = false) -> InputEvent {
        InputEvent(kind: down ? .keyDown : .keyUp, arg1: virtualKey, arg2: scanCode, flags: extended ? flagExtended : 0)
    }
    public var wheelDelta: Int16 { Int16(bitPattern: arg1) }
}

public enum InputCodec {
    public static let maxEventsPerMessage = 256

    public static func encode(_ events: [InputEvent]) -> [UInt8] {
        var out: [UInt8] = [HostMessages.input]
        out.appendUInt16BE(UInt16(events.count))
        for e in events {
            out.append(e.kind.rawValue)
            out.append(e.flags)
            out.appendUInt16BE(e.x); out.appendUInt16BE(e.y); out.appendUInt16BE(e.arg1); out.appendUInt16BE(e.arg2)
        }
        return out
    }

    /// Strict: unknown kinds, bad buttons or a wrong length reject the whole message.
    public static func decode(_ m: [UInt8]) -> [InputEvent]? {
        guard m.count >= 3, m[0] == HostMessages.input else { return nil }
        let count = Int(m.readUInt16BE(at: 1))
        guard count <= maxEventsPerMessage, m.count == 3 + count * InputEvent.wireSize else { return nil }
        var events: [InputEvent] = []
        for i in 0..<count {
            let o = 3 + i * InputEvent.wireSize
            guard let kind = InputKind(rawValue: m[o]) else { return nil }
            let e = InputEvent(kind: kind, x: m.readUInt16BE(at: o + 2), y: m.readUInt16BE(at: o + 4), arg1: m.readUInt16BE(at: o + 6), arg2: m.readUInt16BE(at: o + 8), flags: m[o + 1])
            if (kind == .mouseDown || kind == .mouseUp) && e.arg1 > MouseButton.x2.rawValue { return nil }
            events.append(e)
        }
        return events
    }
}

public struct ShareEntry: Codable, Sendable {
    public let Name: String
    public let Size: Int64
    public init(name: String, size: Int64) { Name = name; Size = size }
}

public enum FileNames {
    private static let reserved: Set<String> = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"]

    /// A file name that is safe to create anywhere: no path parts, no control or reserved characters, no device names, bounded length. Same rules as the .NET side.
    public static func sanitize(_ requested: String) -> String {
        var name = requested.replacingOccurrences(of: "\\", with: "/")
        if let slash = name.lastIndex(of: "/") { name = String(name[name.index(after: slash)...]) }
        let invalid = Set("<>:\"/\\|?*")
        name = String(name.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) && !invalid.contains(Character($0)) && $0.value >= 32 }.map(Character.init))
        name = name.trimmingCharacters(in: .whitespaces)
        while let last = name.last, last == "." || last == " " { name.removeLast() }
        if name.isEmpty { return "file" }
        let ns = name as NSString
        if reserved.contains(ns.deletingPathExtension.uppercased()) { name = "_" + name }
        if name.count > 120 {
            var ext = (name as NSString).pathExtension
            if !ext.isEmpty { ext = "." + ext }
            if ext.count > 20 { ext = String(ext.prefix(20)) }
            name = String(name.prefix(120 - ext.count)) + ext
        }
        return name
    }
}

/// Receives one file: writes to a ".part" file, checks size and SHA-256, then renames. Used for uploads (host) and downloads (viewer).
public final class FileSink {
    private var handle: FileHandle?
    private var hash = SHA256()
    private let expectedHash: [UInt8]
    private let expectedSize: Int64
    private let partPath: String
    public let finalPath: String
    private var written: Int64 = 0
    private var finished = false

    private init(handle: FileHandle, partPath: String, finalPath: String, size: Int64, sha256: [UInt8]) {
        self.handle = handle
        self.partPath = partPath
        self.finalPath = finalPath
        self.expectedSize = size
        self.expectedHash = sha256
    }

    public static func create(directory: URL, requestedName: String, size: Int64, sha256: [UInt8]) throws -> FileSink {
        guard size >= 0, size <= HostMessages.maxFileBytes else { throw RemotelyError.io("File size is not allowed.") }
        guard sha256.count == 32 else { throw RemotelyError.io("Bad checksum.") }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let name = FileNames.sanitize(requestedName)
        let ns = name as NSString
        let stem = ns.deletingPathExtension
        let ext = ns.pathExtension.isEmpty ? "" : "." + ns.pathExtension
        for n in 0..<1000 {
            let candidate = directory.appendingPathComponent(n == 0 ? name : "\(stem) (\(n))\(ext)").path
            if FileManager.default.fileExists(atPath: candidate) { continue }
            let part = candidate + ".part"
            let fd = open(part, O_WRONLY | O_CREAT | O_EXCL, 0o600)
            if fd < 0 { continue } // taken by another transfer: try the next name
            return FileSink(handle: FileHandle(fileDescriptor: fd, closeOnDealloc: true), partPath: part, finalPath: candidate, size: size, sha256: sha256)
        }
        throw RemotelyError.io("Could not find a free file name.")
    }

    public func write(_ data: [UInt8]) throws {
        guard let handle else { throw RemotelyError.io("File is closed.") }
        guard written + Int64(data.count) <= expectedSize else { throw RemotelyError.io("More data than announced.") }
        try handle.write(contentsOf: Data(data))
        hash.update(data: data)
        written += Int64(data.count)
    }

    /// Verifies and moves the file into place. Returns the final path.
    public func complete() throws -> String {
        guard written == expectedSize else { throw RemotelyError.io("File is incomplete.") }
        guard Array(hash.finalize()) == expectedHash else { throw RemotelyError.io("Checksum mismatch.") }
        try handle?.close()
        handle = nil
        try FileManager.default.moveItem(atPath: partPath, toPath: finalPath)
        finished = true
        return finalPath
    }

    public func abort() {
        guard !finished else { return }
        try? handle?.close()
        handle = nil
        try? FileManager.default.removeItem(atPath: partPath)
        finished = true
    }

    deinit { abort() }
}

public enum ShareDirectory {
    /// Regular files directly inside the folder (no subfolders, no links, no hidden files).
    public static func list(_ directory: URL) -> [ShareEntry] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey]
        guard let urls = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return [] }
        return urls.compactMap { url -> ShareEntry? in
            guard let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true, v.isSymbolicLink != true,
                  let size = v.fileSize, Int64(size) <= HostMessages.maxFileBytes, !url.lastPathComponent.lowercased().hasSuffix(".part") else { return nil }
            return ShareEntry(name: url.lastPathComponent, size: Int64(size))
        }.sorted { $0.Name.localizedCaseInsensitiveCompare($1.Name) == .orderedAscending }
    }

    /// Resolves a requested name to a file inside the folder, or nil. A name with any path part is refused outright.
    public static func resolve(_ directory: URL, name: String) -> URL? {
        guard !name.isEmpty, name == (name as NSString).lastPathComponent, !name.contains("/"), !name.contains("\\") else { return nil }
        guard let entry = list(directory).first(where: { $0.Name.caseInsensitiveCompare(name) == .orderedSame }) else { return nil }
        return directory.appendingPathComponent(entry.Name)
    }
}
