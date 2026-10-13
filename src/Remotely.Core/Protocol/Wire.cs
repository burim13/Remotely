using System.Buffers;
using System.Buffers.Binary;

namespace Remotely.Core.Protocol;

/// <summary>Length-prefixed field writer: each field is a u32 big-endian length followed by the bytes.</summary>
public sealed class FieldWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public FieldWriter Raw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_buffer.GetSpan(bytes.Length));
        _buffer.Advance(bytes.Length);
        return this;
    }

    public FieldWriter Byte(byte value)
    {
        _buffer.GetSpan(1)[0] = value;
        _buffer.Advance(1);
        return this;
    }

    public FieldWriter Field(ReadOnlySpan<byte> bytes)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.GetSpan(4), (uint)bytes.Length);
        _buffer.Advance(4);
        return Raw(bytes);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}

public ref struct FieldReader
{
    private ReadOnlySpan<byte> _rest;

    public FieldReader(ReadOnlySpan<byte> data) => _rest = data;

    public bool AtEnd => _rest.IsEmpty;

    public byte Byte()
    {
        if (_rest.Length < 1) throw new FormatException("Truncated message.");
        var b = _rest[0];
        _rest = _rest[1..];
        return b;
    }

    public ReadOnlySpan<byte> Raw(int count)
    {
        if (count < 0 || _rest.Length < count) throw new FormatException("Truncated message.");
        var r = _rest[..count];
        _rest = _rest[count..];
        return r;
    }

    public ReadOnlySpan<byte> Field(int maxLength = 4096)
    {
        if (_rest.Length < 4) throw new FormatException("Truncated message.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(_rest);
        if (length > (uint)maxLength || length > (uint)(_rest.Length - 4)) throw new FormatException("Bad field length.");
        var r = _rest.Slice(4, (int)length);
        _rest = _rest[(4 + (int)length)..];
        return r;
    }
}
