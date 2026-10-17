using System.Buffers.Binary;
using System.Text;

namespace Remotely.HostCore;

/// <summary>
/// Message ids of the input-fidelity and rich-clipboard extension (agent A7). Viewer to host uses 0x30..0x3F, host to viewer 0xB0..0xBF.
/// A host offers all of this when <see cref="HostCapabilities.RichClipboard"/> is set in its hello; a viewer must not send any of it (nor the
/// extended <see cref="InputKind"/>s) to a host that does not, because older hosts answer with an error and drop the whole input message.
/// </summary>
public static class FidelityMessages
{
    public static class ToHost
    {
        public const byte ClipOffer = 0x30, ClipChunk = 0x31, ClipEnd = 0x32, ClipAnswer = 0x33, ClipCancel = 0x34;
        /// <summary>Asks for the keyboard layout of the window that has input on the host.</summary>
        public const byte LayoutQuery = 0x35;
        public const byte ClipResult = 0x36;
        /// <summary>One byte: 1 = send me the host's rich clipboard changes, 0 = stop.</summary>
        public const byte ClipSubscribe = 0x37;
    }

    public static class ToViewer
    {
        public const byte ClipOffer = 0xB0, ClipChunk = 0xB1, ClipEnd = 0xB2, ClipAnswer = 0xB3, ClipCancel = 0xB4;
        /// <summary>u32 big-endian keyboard layout handle (HKL) of the window that has input; 0 = unknown.</summary>
        public const byte LayoutReply = 0xB5;
        public const byte ClipResult = 0xB6;
    }

    public static bool IsToHost(byte kind) => kind is >= 0x30 and <= 0x3F;
    public static bool IsToViewer(byte kind) => kind is >= 0xB0 and <= 0xBF;

    public static byte[] LayoutReply(uint layout)
    {
        var m = new byte[5];
        m[0] = ToViewer.LayoutReply;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(1), layout);
        return m;
    }

    public static bool TryReadLayoutReply(ReadOnlySpan<byte> message, out uint layout)
    {
        layout = 0;
        if (message.Length != 5 || message[0] != ToViewer.LayoutReply) return false;
        layout = BinaryPrimitives.ReadUInt32BigEndian(message[1..]);
        return true;
    }
}

/// <summary>The message ids of one transfer direction. Offer, chunk, end and cancel travel from the sender; answer and result come back from the receiver.</summary>
public sealed record ClipWire(byte Offer, byte Chunk, byte End, byte Cancel, byte Answer, byte Result)
{
    public static readonly ClipWire ViewerToHost = new(FidelityMessages.ToHost.ClipOffer, FidelityMessages.ToHost.ClipChunk, FidelityMessages.ToHost.ClipEnd,
        FidelityMessages.ToHost.ClipCancel, FidelityMessages.ToViewer.ClipAnswer, FidelityMessages.ToViewer.ClipResult);

    public static readonly ClipWire HostToViewer = new(FidelityMessages.ToViewer.ClipOffer, FidelityMessages.ToViewer.ClipChunk, FidelityMessages.ToViewer.ClipEnd,
        FidelityMessages.ToViewer.ClipCancel, FidelityMessages.ToHost.ClipAnswer, FidelityMessages.ToHost.ClipResult);
}

/// <summary>Wire encoding of the clipboard transfer messages. All integers are big-endian.</summary>
public static class ClipCodec
{
    public const int ChunkBytes = 512 * 1024;
    public const int MaxReasonBytes = 200;

    /// <summary>kind, u32 id, u8 flags (0), u8 item count, then per item: u8 kind, u32 size.</summary>
    public static byte[] EncodeOffer(byte wireKind, uint id, IReadOnlyList<ClipItem> items)
    {
        var m = new byte[7 + items.Count * 5];
        m[0] = wireKind;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(1), id);
        m[6] = (byte)items.Count;
        for (var i = 0; i < items.Count; i++)
        {
            m[7 + i * 5] = (byte)items[i].Kind;
            BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(8 + i * 5), (uint)items[i].Data.Length);
        }
        return m;
    }

    public static bool TryDecodeOffer(ReadOnlySpan<byte> m, out ClipOfferInfo info)
    {
        info = default!;
        if (m.Length < 7) return false;
        var count = m[6];
        if (count == 0 || m.Length != 7 + count * 5) return false;
        var items = new List<(ClipItemKind, long)>(count);
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(m[(8 + i * 5)..]);
            items.Add(((ClipItemKind)m[7 + i * 5], size));
            total += size;
        }
        info = new ClipOfferInfo(BinaryPrimitives.ReadUInt32BigEndian(m[1..]), items, total);
        return true;
    }

    public static byte[] EncodeChunk(byte wireKind, uint id, ReadOnlySpan<byte> data) => HostMessages.WithId(wireKind, id, data);

    public static byte[] EncodeEnd(byte wireKind, uint id, ReadOnlySpan<byte> sha256) => HostMessages.WithId(wireKind, id, sha256);

    public static byte[] EncodeCancel(byte wireKind, uint id) => HostMessages.WithId(wireKind, id, []);

    /// <summary>kind, u32 id, u8 ok, u16 reason length, reason (UTF-8, at most 200 bytes). Used for both answer (accept/refuse) and result (applied/failed).</summary>
    public static byte[] EncodeReply(byte wireKind, uint id, bool ok, string reason)
    {
        var text = Encoding.UTF8.GetBytes(reason);
        if (text.Length > MaxReasonBytes) text = text[..MaxReasonBytes];
        var payload = new byte[3 + text.Length];
        payload[0] = ok ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)text.Length);
        text.CopyTo(payload, 3);
        return HostMessages.WithId(wireKind, id, payload);
    }

    public static bool TryDecodeReply(ReadOnlySpan<byte> m, out uint id, out bool ok, out string reason)
    {
        ok = false;
        reason = "";
        if (!HostMessages.TryReadId(m, out id, out var rest) || rest.Length < 3) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(rest[1..]);
        if (length > MaxReasonBytes || rest.Length != 3 + length) return false;
        ok = rest[0] == 1;
        reason = Encoding.UTF8.GetString(rest.Slice(3, length));
        return true;
    }
}
