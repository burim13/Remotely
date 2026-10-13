import Foundation

public enum RemotelyError: Error, CustomStringConvertible {
    case handshake(String)
    case coordinator(String)
    case malformed(String)
    case crypto(String)
    case io(String)

    public var description: String {
        switch self {
        case .handshake(let m), .coordinator(let m), .malformed(let m), .crypto(let m), .io(let m): return m
        }
    }
}

extension Array where Element == UInt8 {
    public init?(hex: String) {
        guard hex.count % 2 == 0 else { return nil }
        var bytes = [UInt8]()
        bytes.reserveCapacity(hex.count / 2)
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
            bytes.append(byte)
            index = next
        }
        self = bytes
    }

    public var hex: String { map { String(format: "%02x", $0) }.joined() }

    mutating func appendUInt16BE(_ v: UInt16) { append(UInt8(v >> 8)); append(UInt8(v & 0xff)) }
    mutating func appendUInt32BE(_ v: UInt32) { for shift in stride(from: 24, through: 0, by: -8) { append(UInt8((v >> UInt32(shift)) & 0xff)) } }
    mutating func appendUInt64BE(_ v: UInt64) { for shift in stride(from: 56, through: 0, by: -8) { append(UInt8((v >> UInt64(shift)) & 0xff)) } }

    func readUInt16BE(at o: Int) -> UInt16 { UInt16(self[o]) << 8 | UInt16(self[o + 1]) }
    func readUInt32BE(at o: Int) -> UInt32 { (0..<4).reduce(UInt32(0)) { $0 << 8 | UInt32(self[o + $1]) } }
    func readUInt64BE(at o: Int) -> UInt64 { (0..<8).reduce(UInt64(0)) { $0 << 8 | UInt64(self[o + $1]) } }
}

/// Length-prefixed fields, as in the .NET side: each field is a u32 big-endian length followed by the bytes.
public struct FieldWriter {
    public private(set) var bytes: [UInt8] = []
    public init() {}
    public mutating func raw(_ b: [UInt8]) { bytes.append(contentsOf: b) }
    public mutating func byte(_ b: UInt8) { bytes.append(b) }
    public mutating func field(_ b: [UInt8]) { bytes.appendUInt32BE(UInt32(b.count)); bytes.append(contentsOf: b) }
}

public struct FieldReader {
    private let data: [UInt8]
    private var offset = 0
    public init(_ data: [UInt8]) { self.data = data }
    public var atEnd: Bool { offset >= data.count }

    public mutating func byte() throws -> UInt8 {
        guard offset < data.count else { throw RemotelyError.malformed("Truncated message.") }
        defer { offset += 1 }
        return data[offset]
    }

    public mutating func raw(_ count: Int) throws -> [UInt8] {
        guard count >= 0, data.count - offset >= count else { throw RemotelyError.malformed("Truncated message.") }
        defer { offset += count }
        return Array(data[offset..<(offset + count)])
    }

    public mutating func field(maxLength: Int = 4096) throws -> [UInt8] {
        guard data.count - offset >= 4 else { throw RemotelyError.malformed("Truncated message.") }
        let length = Int(data.readUInt32BE(at: offset))
        guard length <= maxLength, length <= data.count - offset - 4 else { throw RemotelyError.malformed("Bad field length.") }
        offset += 4
        return try raw(length)
    }
}
