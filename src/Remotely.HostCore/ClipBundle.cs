using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Remotely.HostCore;

public enum ClipItemKind : byte
{
    /// <summary>UTF-8 text.</summary>
    Text = 1,
    /// <summary>UTF-8 HTML fragment (the part between the StartFragment and EndFragment markers of the Windows "HTML Format"), without any header.</summary>
    Html = 2,
    /// <summary>A PNG file.</summary>
    Png = 3,
    /// <summary>Files, packed by <see cref="ClipFiles"/>. Folders are not supported.</summary>
    Files = 4,
}

public sealed record ClipItem(ClipItemKind Kind, byte[] Data);

/// <summary>What a receiver is told before any data arrives, so it can refuse or ask the person.</summary>
public sealed record ClipOfferInfo(uint Id, IReadOnlyList<(ClipItemKind Kind, long Size)> Items, long TotalBytes)
{
    public bool Has(ClipItemKind kind) => Items.Any(i => i.Kind == kind);
}

/// <summary>Everything one copy put on the clipboard that we carry: at most one item of each kind.</summary>
public sealed class ClipBundle
{
    private byte[]? _fingerprint;

    public ClipBundle(IEnumerable<ClipItem> items) => Items = [.. items];

    public IReadOnlyList<ClipItem> Items { get; }
    public long TotalBytes => Items.Sum(i => (long)i.Data.Length);
    public bool IsEmpty => Items.Count == 0;
    /// <summary>True if there is anything beyond plain text (plain text alone travels on the original text path).</summary>
    public bool HasRich => Items.Any(i => i.Kind != ClipItemKind.Text);

    public ClipItem? Get(ClipItemKind kind) => Items.FirstOrDefault(i => i.Kind == kind);

    public string? Text => Get(ClipItemKind.Text) is { } t ? Encoding.UTF8.GetString(t.Data) : null;

    /// <summary>SHA-256 over kind, length and data of every item, in kind order. Equal clipboards give equal fingerprints.</summary>
    public byte[] Fingerprint()
    {
        if (_fingerprint is not null) return _fingerprint;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[5];
        foreach (var item in Items.OrderBy(i => i.Kind))
        {
            header[0] = (byte)item.Kind;
            BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)item.Data.Length);
            hash.AppendData(header);
            hash.AppendData(item.Data);
        }
        return _fingerprint = hash.GetHashAndReset();
    }

    public static ClipBundle FromText(string text) => new([new ClipItem(ClipItemKind.Text, Encoding.UTF8.GetBytes(text))]);
}

/// <summary>Size limits for clipboard transfers. A receiver refuses an offer that breaks them before any data is sent.</summary>
public sealed record ClipLimits
{
    public static readonly ClipLimits Default = new();

    public int MaxTextBytes { get; init; } = HostMessages.MaxClipboardChars * 4;
    public int MaxHtmlBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxPngBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxFilesBytes { get; init; } = 128 * 1024 * 1024;
    public int MaxFileCount { get; init; } = 64;
    public long MaxTotalBytes { get; init; } = 160L * 1024 * 1024;
    /// <summary>Above this many bytes the receiving person has to agree first (a receiver with no way to ask refuses).</summary>
    public long ConfirmAboveBytes { get; init; } = 8L * 1024 * 1024;

    public long MaxFor(ClipItemKind kind) => kind switch
    {
        ClipItemKind.Text => MaxTextBytes,
        ClipItemKind.Html => MaxHtmlBytes,
        ClipItemKind.Png => MaxPngBytes,
        ClipItemKind.Files => MaxFilesBytes,
        _ => -1,
    };

    /// <summary>Null if the offer is acceptable, otherwise a short reason that is safe to show to the person.</summary>
    public string? Check(IReadOnlyList<(ClipItemKind Kind, long Size)> items)
    {
        if (items.Count == 0 || items.Count > 4) return "Nothing to transfer.";
        long total = 0;
        var seen = new HashSet<ClipItemKind>();
        foreach (var (kind, size) in items)
        {
            var max = MaxFor(kind);
            if (max < 0) return "Unknown clipboard format.";
            if (!seen.Add(kind)) return "Duplicate clipboard format.";
            if (size > max) return $"The {kind} part of the clipboard is too large ({size / 1024 / 1024} MB, limit {max / 1024 / 1024} MB).";
            total += size;
        }
        return total > MaxTotalBytes ? "The clipboard is too large." : null;
    }

    public string? Check(ClipBundle bundle) => Check([.. bundle.Items.Select(i => (i.Kind, (long)i.Data.Length))]);

    public bool NeedsConfirmation(long totalBytes) => totalBytes > ConfirmAboveBytes;
}

public sealed record ClipFile(string Name, byte[] Content);

/// <summary>
/// Packs a list of files into the <see cref="ClipItemKind.Files"/> item: u16 count, then per file u16 name length, name (UTF-8) and u64 size,
/// then every file's bytes in the same order. Names are file names only; the decoder strips any path part and makes names unique.
/// </summary>
public static class ClipFiles
{
    public static byte[] Encode(IReadOnlyList<ClipFile> files)
    {
        var names = files.Select(f => Encoding.UTF8.GetBytes(f.Name)).ToList();
        var size = 2L + files.Sum(f => 2 + 8L) + names.Sum(n => (long)n.Length) + files.Sum(f => (long)f.Content.Length);
        if (size > int.MaxValue || names.Any(n => n.Length > ushort.MaxValue)) throw new ArgumentException("Too much data.", nameof(files));
        var data = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)files.Count);
        var o = 2;
        for (var i = 0; i < files.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(o), (ushort)names[i].Length);
            names[i].CopyTo(data, o + 2);
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(o + 2 + names[i].Length), (ulong)files[i].Content.Length);
            o += 2 + names[i].Length + 8;
        }
        foreach (var f in files)
        {
            f.Content.CopyTo(data, o);
            o += f.Content.Length;
        }
        return data;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, ClipLimits limits, out List<ClipFile> files, out string? error)
    {
        files = [];
        error = null;
        if (data.Length < 2) { error = "Bad file list."; return false; }
        var count = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (count == 0 || count > limits.MaxFileCount) { error = "Too many files."; return false; }
        var entries = new List<(string Name, long Size)>(count);
        var o = 2;
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            if (o + 2 > data.Length) { error = "Bad file list."; return false; }
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(data[o..]);
            if (o + 2 + nameLength + 8 > data.Length) { error = "Bad file list."; return false; }
            var name = Encoding.UTF8.GetString(data.Slice(o + 2, nameLength));
            var size = BinaryPrimitives.ReadUInt64BigEndian(data[(o + 2 + nameLength)..]);
            if (size > (ulong)limits.MaxFilesBytes) { error = "A file is too large."; return false; }
            entries.Add((name, (long)size));
            total += (long)size;
            o += 2 + nameLength + 8;
        }
        if (total != data.Length - o) { error = "Bad file list."; return false; }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, size) in entries)
        {
            files.Add(new ClipFile(Unique(FileNames.Sanitize(name), used), data.Slice(o, (int)size).ToArray()));
            o += (int)size;
        }
        return true;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 1; !used.Add(candidate); n++) candidate = $"{stem} ({n}){ext}";
        return candidate;
    }
}

/// <summary>Conversion between an HTML fragment and the Windows clipboard's "HTML Format" (CF_HTML), whose header holds UTF-8 byte offsets.</summary>
public static class ClipHtml
{
    public static byte[] BuildCfHtml(string fragment)
    {
        const string Start = "<html><body>\r\n<!--StartFragment-->";
        const string End = "<!--EndFragment-->\r\n</body></html>";
        string Header(int a, int b, int c, int d) => $"Version:0.9\r\nStartHTML:{a:D10}\r\nEndHTML:{b:D10}\r\nStartFragment:{c:D10}\r\nEndFragment:{d:D10}\r\n";
        var headerLength = Header(0, 0, 0, 0).Length;
        var fragmentBytes = Encoding.UTF8.GetByteCount(fragment);
        var startHtml = headerLength;
        var startFragment = startHtml + Start.Length;
        var endFragment = startFragment + fragmentBytes;
        var endHtml = endFragment + End.Length;
        return Encoding.UTF8.GetBytes(Header(startHtml, endHtml, startFragment, endFragment) + Start + fragment + End);
    }

    /// <summary>The fragment from CF_HTML bytes. Falls back to the marker comments, then to everything after the header, when the offsets are missing or wrong.</summary>
    public static string ExtractFragment(ReadOnlySpan<byte> cfHtml)
    {
        var length = cfHtml.Length;
        while (length > 0 && cfHtml[length - 1] == 0) length--; // Windows pads with a terminating NUL
        cfHtml = cfHtml[..length];
        var head = Encoding.ASCII.GetString(cfHtml[..Math.Min(cfHtml.Length, 400)]);
        var start = Offset(head, "StartFragment:");
        var end = Offset(head, "EndFragment:");
        if (start >= 0 && end >= start && end <= cfHtml.Length) return Encoding.UTF8.GetString(cfHtml[start..end]);
        var all = Encoding.UTF8.GetString(cfHtml);
        const string StartMarker = "<!--StartFragment-->", EndMarker = "<!--EndFragment-->";
        var s = all.IndexOf(StartMarker, StringComparison.Ordinal);
        var e = all.IndexOf(EndMarker, StringComparison.Ordinal);
        if (s >= 0 && e > s) return all[(s + StartMarker.Length)..e];
        var html = all.IndexOf("<", StringComparison.Ordinal);
        return html >= 0 ? all[html..] : all;
    }

    private static int Offset(string head, string key)
    {
        var i = head.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        i += key.Length;
        var j = i;
        while (j < head.Length && char.IsAsciiDigit(head[j])) j++;
        return j > i && int.TryParse(head.AsSpan(i, j - i), out var value) ? value : -1;
    }
}
