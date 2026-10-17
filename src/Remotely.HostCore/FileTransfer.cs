using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Remotely.HostCore;

public static class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>A file name that is safe to create on any platform: no path parts, no control or reserved characters, no device names, bounded length.</summary>
    public static string Sanitize(string requested)
    {
        var name = requested.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        name = new string(name.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "file";
        var stem = Path.GetFileNameWithoutExtension(name);
        if (Reserved.Contains(stem)) name = "_" + name;
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            ext = ext.Length > 20 ? ext[..20] : ext;
            name = name[..(120 - ext.Length)] + ext;
        }
        return name;
    }
}

/// <summary>Receives one file: writes to a ".part" file in the target folder, checks size and SHA-256, then renames. Used for uploads (host) and downloads (viewer).</summary>
public sealed class FileSink : IDisposable
{
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _expectedHash;
    private readonly long _expectedSize;
    private readonly string _partPath;
    private long _written;
    private bool _finished;

    private FileSink(FileStream stream, string partPath, string finalPath, long size, byte[] sha256)
    {
        _stream = stream;
        _partPath = partPath;
        FinalPath = finalPath;
        _expectedSize = size;
        _expectedHash = sha256;
    }

    public string FinalPath { get; private set; }

    /// <exception cref="InvalidDataException">Size is out of range or the hash is not 32 bytes.</exception>
    public static FileSink Create(string directory, string requestedName, long size, byte[] sha256)
    {
        if (size < 0 || size > HostMessages.MaxFileBytes) throw new InvalidDataException("File size is not allowed.");
        if (sha256.Length != 32) throw new InvalidDataException("Bad checksum.");
        Directory.CreateDirectory(directory);
        var name = FileNames.Sanitize(requestedName);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 0; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, n == 0 ? name : $"{stem} ({n}){ext}");
            var part = candidate + ".part";
            if (File.Exists(candidate)) continue;
            try
            {
                var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new FileSink(stream, part, candidate, size, sha256);
            }
            catch (IOException) when (File.Exists(part)) { /* taken by another transfer: try the next name */ }
        }
        throw new IOException("Could not find a free file name.");
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_written + data.Length > _expectedSize) throw new InvalidDataException("More data than announced.");
        _stream.Write(data);
        _hash.AppendData(data);
        _written += data.Length;
    }

    /// <summary>Verifies and moves the file into place. Returns the final path.</summary>
    public string Complete()
    {
        if (_written != _expectedSize) throw new InvalidDataException("File is incomplete.");
        if (!CryptographicOperations.FixedTimeEquals(_hash.GetHashAndReset(), _expectedHash)) throw new InvalidDataException("Checksum mismatch.");
        _stream.Flush();
        _stream.Dispose();
        File.Move(_partPath, FinalPath, overwrite: false);
        _finished = true;
        return FinalPath;
    }

    public void Dispose()
    {
        if (_finished) return;
        _stream.Dispose();
        _hash.Dispose();
        try { File.Delete(_partPath); } catch (IOException) { }
    }
}

public sealed record ShareEntry(string Name, long Size);

public static class ShareDirectory
{
    /// <summary>Regular files directly inside the folder (no subfolders, no links, no hidden or system files).</summary>
    public static List<ShareEntry> List(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => f.LinkTarget is null && !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System)
                        && f.Length <= HostMessages.MaxFileBytes && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ShareEntry(f.Name, f.Length)).ToList();
    }

    /// <summary>Resolves a requested name to a file inside the folder, or null. A name with any path part is refused outright.</summary>
    public static FileInfo? Resolve(string directory, string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\')) return null;
        var entry = List(directory).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : new FileInfo(Path.Combine(directory, entry.Name));
    }
}

internal static class Text
{
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}

public sealed record ShareEntryEx(string Name, long Size, bool IsFolder);

public static class ShareDirectoryEx
{
    private static bool Visible(FileSystemInfo f) =>
        f.LinkTarget is null && !f.Attributes.HasFlag(FileAttributes.ReparsePoint) && !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System);

    /// <summary>Files and folders directly inside the share folder (no links, no hidden or system entries, no ".part" files). Folder sizes are -1: they are not measured.</summary>
    public static List<ShareEntryEx> List(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var dir = new DirectoryInfo(directory);
        var files = dir.EnumerateFiles().Where(f => Visible(f) && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ShareEntryEx(f.Name, f.Length, false));
        var folders = dir.EnumerateDirectories().Where(Visible).Select(d => new ShareEntryEx(d.Name, -1, true));
        return files.Concat(folders).OrderBy(e => !e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A file or folder directly inside the share folder, or null. A name with any path part is refused outright.</summary>
    public static ShareEntryEx? Resolve(string directory, string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\')) return null;
        return List(directory).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------
// Resumable transfers (capability HostCapabilities.ResumableFiles). The original FileStart/FileChunk/FileEnd/ShareList/ShareGet flow above
// is untouched and still used with peers that do not offer the capability.
// ---------------------------------------------------------------------------------------------------------------------------------------

/// <summary>Message ids of the resumable file protocol. Viewer to host: 0x40-0x4F. Host to viewer: 0xC0-0xCF. Every message is kind, u32 transfer id, body.</summary>
public static class TransferMessages
{
    public const byte FirstViewerToHost = 0x40, LastViewerToHost = 0x4F;
    public const byte FirstHostToViewer = 0xC0, LastHostToViewer = 0xCF;

    // viewer -> host

    /// <summary>Upload announcement (the "announce" layout in <see cref="TransferWire"/>). The host answers <see cref="Accept"/>, or <see cref="Result"/> with an error.</summary>
    public const byte Offer = 0x40;
    /// <summary>Upload data: u64 offset, bytes.</summary>
    public const byte Data = 0x41;
    /// <summary>Upload end: u64 total length, 32-byte SHA-256 of the whole content.</summary>
    public const byte End = 0x42;
    /// <summary>Cancel a transfer in either direction (empty body). The receiver deletes its partial file.</summary>
    public const byte Cancel = 0x43;
    /// <summary>Download request: u16 name length, name of a file or folder in the host's share folder. The host answers <see cref="Begin"/>, or <see cref="Result"/> with an error.</summary>
    public const byte Pull = 0x44;
    /// <summary>List the share folder with folders included (id 0, empty body). Answer: <see cref="EntriesReply"/>.</summary>
    public const byte ListEntries = 0x45;
    /// <summary>The viewer's answer to <see cref="Begin"/>: u64 offset it already holds. The host streams from there.</summary>
    public const byte Start = 0x46;

    // host -> viewer

    /// <summary>Upload accepted: u64 offset the host already holds. The viewer sends from there.</summary>
    public const byte Accept = 0xC0;
    /// <summary>Download data: u64 offset, bytes.</summary>
    public const byte DataToViewer = 0xC1;
    /// <summary>Download end: u64 total length, 32-byte SHA-256.</summary>
    public const byte EndToViewer = 0xC2;
    /// <summary>Final answer or failure of a transfer: u8 <see cref="TransferError"/> (0 = success), u16 text length, text (the stored name on success).</summary>
    public const byte Result = 0xC3;
    /// <summary>Download announcement (the "announce" layout).</summary>
    public const byte Begin = 0xC4;
    /// <summary>JSON array of <see cref="ShareEntryEx"/>.</summary>
    public const byte EntriesReply = 0xC5;

    /// <summary>kind + id + offset, in front of every data chunk.</summary>
    public const int HeaderBytes = 13;
    public const int ChunkBytes = HostMessages.FileChunkBytes;
}

public enum TransferError : byte
{
    None = 0,
    Declined = 1,
    TooLarge = 2,
    QuotaExceeded = 3,
    DiskFull = 4,
    NameClash = 5,
    AccessDenied = 6,
    HashMismatch = 7,
    NotFound = 8,
    Cancelled = 9,
    Busy = 10,
    Protocol = 11,
    UnsafeArchive = 12,
    Unsupported = 13,
    LinkLost = 14,
    Other = 15,
    SourceChanged = 16,
}

/// <summary>A failed transfer with a code a screen can translate and a readable English message.</summary>
public sealed class TransferException(TransferError code, string message, Exception? inner = null) : IOException(message, inner)
{
    public TransferError Code { get; } = code;

    /// <summary>Turns any failure of a file operation into a coded, readable error.</summary>
    public static TransferException From(Exception ex) => ex switch
    {
        TransferException t => t,
        UnauthorizedAccessException => new(TransferError.AccessDenied, "Access was denied. Check the permissions of the folder.", ex),
        DirectoryNotFoundException or FileNotFoundException => new(TransferError.NotFound, "The file or folder was not found.", ex),
        PathTooLongException => new(TransferError.Other, "The file path is too long.", ex),
        InvalidDataException => new(TransferError.Protocol, ex.Message, ex),
        IOException io when IsDiskFull(io) => new(TransferError.DiskFull, "There is not enough free space for this file.", ex),
        IOException io when IsSharingViolation(io) => new(TransferError.Busy, "The file is being used by another program.", ex),
        _ => new(TransferError.Other, ex.Message, ex),
    };

    public static bool IsDiskFull(IOException io) =>
        OperatingSystem.IsWindows() ? (io.HResult & 0xFFFF) is 0x70 or 0x27 : io.HResult == 28; /* ENOSPC */

    private static bool IsSharingViolation(IOException io) => OperatingSystem.IsWindows() && (io.HResult & 0xFFFF) is 32 or 33;
}

/// <summary>What the owner of a PC is asked about before an incoming file or folder is accepted.</summary>
public sealed record IncomingFileRequest(string Viewer, string Name, long Size, bool IsFolder);

/// <summary>Limits and policy for receiving files. On a host: <see cref="HostAgent.FileOptions"/>. On a viewer: <c>ViewerSession.TransferOptions</c>.</summary>
public sealed class FileTransferOptions
{
    /// <summary>Largest single file. Default 32 GB.</summary>
    public long MaxFileBytes { get; init; } = 32L << 30;
    /// <summary>Largest folder, counted as uncompressed bytes. Default 32 GB.</summary>
    public long MaxFolderBytes { get; init; } = 32L << 30;
    /// <summary>Total bytes allowed in the receive folder (what is already there included). 0 means no quota.</summary>
    public long ReceiveQuotaBytes { get; init; }
    /// <summary>Free space that must remain on the drive after a transfer. Default 256 MB.</summary>
    public long MinFreeDiskBytes { get; init; } = 256L << 20;
    /// <summary>Unpack received folders (safely) instead of leaving a .zip. Default true.</summary>
    public bool ExtractFolders { get; init; } = true;
    public int MaxZipEntries { get; init; } = 200_000;
    public int MaxConcurrentIncoming { get; init; } = 3;
    /// <summary>Asked before a new incoming file or folder is accepted (not when an interrupted transfer resumes). Null accepts everything. Return false to decline.</summary>
    public Func<IncomingFileRequest, CancellationToken, Task<bool>>? ConfirmIncoming { get; init; }
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Interrupted partial files older than this are deleted when a session starts. Default 7 days.</summary>
    public TimeSpan StalePartialAge { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>Binary layouts shared by both sides. Every reader validates lengths and returns false on malformed input.</summary>
public static class TransferWire
{
    public const byte FlagNotResumable = 1;
    public const byte FlagSizeIsEstimate = 2;

    /// <summary>What a sender tells the receiver: kind, flags, a 16-byte key naming "the same source" across attempts, a size, a name.</summary>
    public sealed record Announce(bool IsFolder, byte Flags, byte[] Key, long Size, string Name)
    {
        public bool Resumable => (Flags & FlagNotResumable) == 0;
        public bool SizeKnown => (Flags & FlagSizeIsEstimate) == 0 && Size >= 0;
    }

    public static byte[] EncodeAnnounce(byte kind, uint id, Announce a)
    {
        var name = Encoding.UTF8.GetBytes(a.Name);
        if (name.Length > 1024 || a.Key.Length != 16) throw new ArgumentException("Name too long or bad key.");
        var body = new byte[28 + name.Length];
        body[0] = (byte)(a.IsFolder ? 1 : 0);
        body[1] = a.Flags;
        a.Key.CopyTo(body, 2);
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(18), a.Size);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(26), (ushort)name.Length);
        name.CopyTo(body, 28);
        return HostMessages.WithId(kind, id, body);
    }

    public static bool TryReadAnnounce(ReadOnlySpan<byte> body, out Announce announce)
    {
        announce = null!;
        if (body.Length < 28 || body[0] > 1) return false;
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(body[26..]);
        if (nameLength > 1024 || body.Length != 28 + nameLength) return false;
        var size = BinaryPrimitives.ReadInt64BigEndian(body[18..]);
        if (size < -1) return false;
        string name;
        try { name = new UTF8Encoding(false, true).GetString(body.Slice(28, nameLength)); }
        catch (ArgumentException) { return false; }
        announce = new Announce(body[0] == 1, body[1], body.Slice(2, 16).ToArray(), size, name);
        return true;
    }

    public static byte[] EncodeOffset(byte kind, uint id, long offset)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(body, offset);
        return HostMessages.WithId(kind, id, body);
    }

    public static bool TryReadOffset(ReadOnlySpan<byte> body, out long offset)
    {
        offset = body.Length == 8 ? BinaryPrimitives.ReadInt64BigEndian(body) : -1;
        return offset >= 0;
    }

    public static byte[] EncodeEnd(byte kind, uint id, long total, ReadOnlySpan<byte> sha256)
    {
        var body = new byte[8 + 32];
        BinaryPrimitives.WriteInt64BigEndian(body, total);
        sha256.CopyTo(body.AsSpan(8));
        return HostMessages.WithId(kind, id, body);
    }

    public static bool TryReadEnd(ReadOnlySpan<byte> body, out long total, out byte[] sha256)
    {
        sha256 = [];
        total = body.Length == 40 ? BinaryPrimitives.ReadInt64BigEndian(body) : -1;
        if (total < 0) return false;
        sha256 = body.Slice(8, 32).ToArray();
        return true;
    }

    public static byte[] EncodeResult(uint id, TransferError code, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Length > 300 ? text[..300] : text);
        var body = new byte[3 + bytes.Length];
        body[0] = (byte)code;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1), (ushort)bytes.Length);
        bytes.CopyTo(body, 3);
        return HostMessages.WithId(TransferMessages.Result, id, body);
    }

    public static bool TryReadResult(ReadOnlySpan<byte> body, out TransferError code, out string text)
    {
        code = TransferError.Other; text = "";
        if (body.Length < 3) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(body[1..]);
        if (body.Length != 3 + length) return false;
        code = Enum.IsDefined((TransferError)body[0]) ? (TransferError)body[0] : TransferError.Other;
        text = Encoding.UTF8.GetString(body.Slice(3, length));
        return true;
    }

    public static byte[] EncodePull(uint id, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > 1024) throw new ArgumentException("Name too long.");
        var body = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)bytes.Length);
        bytes.CopyTo(body, 2);
        return HostMessages.WithId(TransferMessages.Pull, id, body);
    }

    public static bool TryReadPull(ReadOnlySpan<byte> body, out string name)
    {
        name = "";
        if (body.Length < 2) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(body);
        if (length > 1024 || body.Length != 2 + length) return false;
        try { name = new UTF8Encoding(false, true).GetString(body.Slice(2, length)); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Data chunk body after the id: u64 offset, bytes.</summary>
    public static bool TryReadData(ReadOnlySpan<byte> body, out long offset, out ReadOnlySpan<byte> data)
    {
        data = default;
        offset = body.Length >= 8 ? BinaryPrimitives.ReadInt64BigEndian(body) : -1;
        if (offset < 0) return false;
        data = body[8..];
        return true;
    }

    /// <summary>Identifies "the same source" across attempts: name, size and modification time. Not a secret.</summary>
    public static byte[] FileKey(string name, long size, DateTime modifiedUtc)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, size);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8), modifiedUtc.Ticks);
        return SHA256.HashData(Encoding.UTF8.GetBytes("file|" + name + "|").Concat(bytes).ToArray()).AsSpan(0, 16).ToArray();
    }
}

/// <summary>
/// A write-only stream that cuts whatever is written into data messages (kind, id, offset, bytes) and hands each to <c>send</c>. One buffer for the whole transfer, so memory does
/// not grow with the size of the file. The first <c>skip</c> bytes are hashed but not sent (they are already at the receiver after a resume).
/// </summary>
internal sealed class ChunkWriter : Stream
{
    private readonly byte[] _buffer = new byte[TransferMessages.HeaderBytes + TransferMessages.ChunkBytes];
    private readonly byte _kind;
    private readonly uint _id;
    private readonly long _skip;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _send;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly CancellationToken _ct;
    private readonly Action<long>? _progress;
    private int _filled;
    private long _position;

    public ChunkWriter(byte kind, uint id, long skip, Func<ReadOnlyMemory<byte>, CancellationToken, Task> send, Action<long>? progress, CancellationToken ct)
    {
        _kind = kind; _id = id; _skip = skip; _send = send; _progress = progress; _ct = ct;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var n = Math.Min(data.Length, TransferMessages.ChunkBytes - _filled);
            data[..n].CopyTo(_buffer.AsSpan(TransferMessages.HeaderBytes + _filled));
            _filled += n;
            data = data[n..];
            if (_filled == TransferMessages.ChunkBytes) FlushChunkAsync().GetAwaiter().GetResult();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        while (!data.IsEmpty)
        {
            var n = Math.Min(data.Length, TransferMessages.ChunkBytes - _filled);
            data[..n].Span.CopyTo(_buffer.AsSpan(TransferMessages.HeaderBytes + _filled));
            _filled += n;
            data = data[n..];
            if (_filled == TransferMessages.ChunkBytes) await FlushChunkAsync().ConfigureAwait(false);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Sends the last partial chunk and returns the total length and SHA-256 of everything written.</summary>
    public async Task<(long Total, byte[] Hash)> FinishAsync()
    {
        if (_filled > 0) await FlushChunkAsync().ConfigureAwait(false);
        return (_position, _hash.GetHashAndReset());
    }

    private async Task FlushChunkAsync()
    {
        _ct.ThrowIfCancellationRequested();
        var count = _filled;
        _filled = 0;
        var start = _position;
        _hash.AppendData(_buffer.AsSpan(TransferMessages.HeaderBytes, count));
        _position += count;
        if (_position > _skip)
        {
            var skipIn = start < _skip ? (int)(_skip - start) : 0;   // the header goes just in front of the first byte that is actually sent
            _buffer[skipIn] = _kind;
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(skipIn + 1), _id);
            BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(skipIn + 5), start + skipIn);
            await _send(_buffer.AsMemory(skipIn, TransferMessages.HeaderBytes + count - skipIn), _ct).ConfigureAwait(false);
        }
        _progress?.Invoke(_position);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Something that can be sent: a file, or a folder that is zipped while it is sent.</summary>
public abstract class TransferSource
{
    /// <summary>The name announced to the receiver (a folder's is "Name.zip").</summary>
    public abstract string Name { get; }
    public abstract bool IsFolder { get; }
    /// <summary>Exact length for a file; for a folder, the uncompressed total (an estimate of the zip's length).</summary>
    public abstract long Size { get; }
    public abstract byte[] Key { get; }
    public abstract byte Flags { get; }
    /// <summary>Files of a folder that could not be read and were left out (known once sending has finished).</summary>
    public int SkippedFiles { get; protected set; }

    public TransferWire.Announce Announce => new(IsFolder, Flags, Key, Size, Name);

    /// <summary>Writes the content (always from the beginning) into the writer. Bytes before the writer's skip point are still produced, so the hash covers everything.</summary>
    internal abstract Task WriteAsync(ChunkWriter writer, CancellationToken ct);

    public static TransferSource ForFile(string path) => new FileSource(path);
    public static TransferSource ForFolder(string path) => new FolderSource(path);

    private sealed class FileSource : TransferSource
    {
        private readonly string _path;
        private readonly long _size;

        public FileSource(string path)
        {
            _path = path;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("The file was not found.", path);
                _size = info.Length;
                Name = info.Name;
                Key = TransferWire.FileKey(info.Name, info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is not TransferException) { throw TransferException.From(ex); }
        }

        public override string Name { get; }
        public override bool IsFolder => false;
        public override long Size => _size;
        public override byte[] Key { get; }
        public override byte Flags => 0;

        internal override async Task WriteAsync(ChunkWriter writer, CancellationToken ct)
        {
            var rented = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                await using var stream = new FileStream(_path, new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 0,
                });
                var remaining = _size;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(rented.AsMemory(0, (int)Math.Min(rented.Length, remaining)), ct).ConfigureAwait(false);
                    if (read == 0) throw new TransferException(TransferError.SourceChanged, "The file changed while it was being sent.");
                    await writer.WriteAsync(rented.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            catch (Exception ex) when (ex is not (TransferException or OperationCanceledException)) { throw TransferException.From(ex); }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
    }

    private sealed class FolderSource : TransferSource
    {
        public const int MaxEntries = 200_000;
        private readonly string _rootName;
        private readonly List<(string Full, string Relative, bool IsDirectory, DateTime Modified)> _entries = [];
        private readonly long _size;

        public FolderSource(string path)
        {
            var root = Path.GetFullPath(path);
            _rootName = new DirectoryInfo(root).Name;
            if (!Directory.Exists(root)) throw new TransferException(TransferError.NotFound, "The folder was not found.");
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
                {
                    if (info.LinkTarget is not null) continue;   // links are never followed
                    if (_entries.Count >= MaxEntries) throw new TransferException(TransferError.TooLarge, "The folder has too many files to send at once.");
                    var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
                    if (info is FileInfo file) { _entries.Add((file.FullName, relative, false, file.LastWriteTimeUtc)); _size += file.Length; }
                    else _entries.Add((info.FullName, relative + "/", true, info.LastWriteTimeUtc));
                }
            }
            catch (Exception ex) when (ex is not TransferException) { throw TransferException.From(ex); }
            Key = RandomNumberGenerator.GetBytes(16);   // a zip stream cannot be resumed, so it never matches an earlier attempt
        }

        public override string Name => _rootName + ".zip";
        public override bool IsFolder => true;
        public override long Size => _size;
        public override byte[] Key { get; }
        public override byte Flags => TransferWire.FlagNotResumable | TransferWire.FlagSizeIsEstimate;

        internal override Task WriteAsync(ChunkWriter writer, CancellationToken ct) => Task.Run(() =>
        {
            try
            {
                using var zip = new ZipArchive(writer, ZipArchiveMode.Create, leaveOpen: true);
                zip.CreateEntry(_rootName + "/");
                var copy = ArrayPool<byte>.Shared.Rent(256 << 10);
                try
                {
                    foreach (var e in _entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entryName = _rootName + "/" + e.Relative;
                        if (e.IsDirectory) { zip.CreateEntry(entryName).LastWriteTime = ZipTime(e.Modified); continue; }
                        FileStream input;
                        try { input = new FileStream(e.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SkippedFiles++; continue; }
                        using (input)
                        {
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                            entry.LastWriteTime = ZipTime(e.Modified);
                            using var output = entry.Open();
                            int read;
                            while ((read = input.Read(copy, 0, copy.Length)) > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                output.Write(copy, 0, read);
                            }
                        }
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(copy); }
            }
            catch (Exception ex) when (ex is not (TransferException or OperationCanceledException)) { throw TransferException.From(ex); }
        }, CancellationToken.None);

        private static DateTimeOffset ZipTime(DateTime utc) =>
            utc.Year is < 1980 or > 2107 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}

/// <summary>Runs a source into data messages and returns the total length and hash for the end message.</summary>
public static class TransferPump
{
    public static async Task<(long Total, byte[] Hash)> RunAsync(
        TransferSource source, byte dataKind, uint id, long startOffset,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> send, Action<long>? progress, CancellationToken ct)
    {
        using var writer = new ChunkWriter(dataKind, id, startOffset, send, progress, ct);
        await source.WriteAsync(writer, ct).ConfigureAwait(false);
        return await writer.FinishAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Receives one file (or one zipped folder) that may arrive over several attempts. Data goes to a ".part" file whose name is derived from the sender and the source's key, so a later
/// attempt finds it and the receiver can say how many bytes it already holds (offset negotiation). Finishing verifies length and SHA-256, then moves the result into place under a free name.
/// Disposing without finishing keeps the partial file (the link dropped); <see cref="Discard"/> deletes it (cancelled or corrupt).
/// </summary>
public sealed class TransferReceiver : IDisposable
{
    private readonly FileTransferOptions _options;
    private readonly string _directory;
    private readonly string _displayName;
    private readonly bool _isFolder;
    private readonly bool _resumable;
    private readonly long _announcedSize;
    private readonly bool _sizeKnown;
    private readonly long _usedBytes;
    private FileStream? _stream;
    private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _done;
    private readonly object _gate = new();
    private string? _storedPath;

    private TransferReceiver(FileTransferOptions options, string directory, string name, TransferWire.Announce announce, string partPath, long used, FileStream stream)
    {
        _options = options; _directory = directory; _displayName = name; _isFolder = announce.IsFolder; _resumable = announce.Resumable;
        PartPath = partPath; _announcedSize = announce.Size; _sizeKnown = announce.SizeKnown; _usedBytes = used; _stream = stream;
        Offset = stream.Length;
    }

    /// <summary>Bytes already stored. After <see cref="Open"/> this is what the receiver tells the sender it holds.</summary>
    public long Offset { get; private set; }
    public string PartPath { get; }

    /// <summary>Where the partial file of this transfer lives (it may not exist).</summary>
    public static string PartPathFor(string directory, string scope, TransferWire.Announce announce)
    {
        var name = StoredName(announce);
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "|").Concat(announce.Key).ToArray()))[..12].ToLowerInvariant();
        return Path.Combine(directory, $"{name}.{tag}.part");
    }

    private static string StoredName(TransferWire.Announce announce)
    {
        var name = FileNames.Sanitize(announce.Name);
        return announce.IsFolder && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name + ".zip" : name;
    }

    /// <summary>Opens a receiver for an announced transfer: checks limits, free space and quota, and picks up an earlier partial file if there is one.</summary>
    /// <param name="scope">Who is sending (a device label): partial files of different senders never mix.</param>
    public static TransferReceiver Open(string directory, string scope, TransferWire.Announce announce, FileTransferOptions options)
    {
        FileStream? stream = null;
        var partPath = "";
        long existing = 0;
        try
        {
            var limit = announce.IsFolder ? options.MaxFolderBytes : options.MaxFileBytes;
            if (announce.Size > limit)
                throw new TransferException(TransferError.TooLarge, $"This {(announce.IsFolder ? "folder" : "file")} is larger than the allowed {FormatBytes(limit)}.");
            Directory.CreateDirectory(directory);
            partPath = PartPathFor(directory, scope, announce);
            try
            {
                stream = new FileStream(partPath, new FileStreamOptions
                {
                    Mode = announce.Resumable ? FileMode.OpenOrCreate : FileMode.Create, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 1 << 16,
                });
            }
            catch (IOException) when (File.Exists(partPath)) { throw new TransferException(TransferError.Busy, "This file is already being received."); }
            existing = stream.Length;
            if (announce.SizeKnown && existing > announce.Size) { stream.SetLength(0); existing = 0; }
            var used = options.ReceiveQuotaBytes > 0 ? UsedBytes(directory, partPath) : 0;
            var receiver = new TransferReceiver(options, directory, StoredName(announce), announce, partPath, used, stream);
            stream = null;   // owned by the receiver now
            try
            {
                receiver.CheckRoom(announce.Size >= 0 ? Math.Max(0, announce.Size - existing) : 0);
                receiver.RehashPrefix();
                return receiver;
            }
            catch
            {
                receiver.Dispose();
                if (existing == 0) TryDelete(partPath);
                throw;
            }
        }
        catch (Exception ex) when (ex is not TransferException)
        {
            stream?.Dispose();
            if (existing == 0 && partPath.Length > 0) TryDelete(partPath);
            throw TransferException.From(ex);
        }
        catch (TransferException)
        {
            if (stream is not null) { stream.Dispose(); if (existing == 0 && partPath.Length > 0) TryDelete(partPath); }
            throw;
        }
    }

    private void RehashPrefix()
    {
        if (Offset == 0) return;
        _stream!.Position = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var left = Offset;
            while (left > 0)
            {
                var read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0) break;
                _hash!.AppendData(buffer.AsSpan(0, read));
                left -= read;
            }
            if (left > 0) throw new TransferException(TransferError.Other, "The partial file could not be read back.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        _stream.Position = Offset;
    }

    private void CheckRoom(long stillToWrite)
    {
        if (_options.ReceiveQuotaBytes > 0 && _usedBytes + stillToWrite + Offset > _options.ReceiveQuotaBytes)
            throw new TransferException(TransferError.QuotaExceeded, $"The receive folder is limited to {FormatBytes(_options.ReceiveQuotaBytes)} and this would go over it.");
        if (stillToWrite <= 0) return;
        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_directory));
            if (string.IsNullOrEmpty(root)) return;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return;   // free space cannot be read here: the write itself fails clearly if the disk is full
        }
        if (free - stillToWrite < _options.MinFreeDiskBytes)
            throw new TransferException(TransferError.DiskFull, $"There is not enough free space: {FormatBytes(stillToWrite)} needed, {FormatBytes(free)} free.");
    }

    internal static long UsedBytes(string directory, string except)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
            if (!string.Equals(file.FullName, except, StringComparison.OrdinalIgnoreCase)) total += file.Length;
        return total;
    }

    /// <summary>Appends data at <paramref name="offset"/>, which must be exactly where the previous data ended.</summary>
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        lock (_gate) WriteLocked(offset, data);
    }

    private void WriteLocked(long offset, ReadOnlySpan<byte> data)
    {
        if (_done || _stream is null) throw new TransferException(TransferError.Protocol, "The transfer already ended.");
        if (offset != Offset) throw new TransferException(TransferError.Protocol, "Data arrived out of order.");
        var after = Offset + data.Length;
        var limit = _isFolder ? _options.MaxFolderBytes : _options.MaxFileBytes;
        if (after > limit || (_sizeKnown && after > _announcedSize)) throw new TransferException(TransferError.TooLarge, "More data arrived than was announced.");
        if (_options.ReceiveQuotaBytes > 0 && _usedBytes + after > _options.ReceiveQuotaBytes)
            throw new TransferException(TransferError.QuotaExceeded, $"The receive folder is limited to {FormatBytes(_options.ReceiveQuotaBytes)} and this would go over it.");
        try
        {
            _stream.Write(data);
            _hash!.AppendData(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw TransferException.From(ex); }
        Offset = after;
    }

    /// <summary>Verifies length and hash, then stores the result. Returns the stored name (a file name, or the folder's name when it was unpacked). Deletes the partial file on any failure except a full disk.</summary>
    public string Complete(long total, ReadOnlySpan<byte> sha256)
    {
        lock (_gate) return CompleteLocked(total, sha256);
    }

    private string CompleteLocked(long total, ReadOnlySpan<byte> sha256)
    {
        if (_done || _stream is null) throw new TransferException(TransferError.Protocol, "The transfer already ended.");
        try
        {
            if (total != Offset || (_sizeKnown && total != _announcedSize))
                throw new TransferException(TransferError.Protocol, "The file is incomplete.");
            if (!CryptographicOperations.FixedTimeEquals(_hash!.GetHashAndReset(), sha256))
                throw new TransferException(TransferError.HashMismatch, "The file arrived damaged (checksum mismatch). Nothing was kept; send it again.");
            _stream.Flush(true);
            _stream.Dispose();
            _stream = null;
            _done = true;
            if (_isFolder && _options.ExtractFolders)
            {
                try
                {
                    var folder = SafeZip.ExtractToUniqueFolder(PartPath, _directory, Path.GetFileNameWithoutExtension(_displayName), _options, _usedBytes);
                    _storedPath = Path.Combine(_directory, folder);
                    return folder;
                }
                finally { TryDelete(PartPath); }
            }
            var stored = MoveIntoPlace();
            _storedPath = Path.Combine(_directory, stored);
            return stored;
        }
        catch (Exception ex)
        {
            var failure = TransferException.From(ex);
            if (failure.Code != TransferError.DiskFull) Discard();
            throw failure;
        }
    }

    private string MoveIntoPlace()
    {
        var stem = Path.GetFileNameWithoutExtension(_displayName);
        var ext = Path.GetExtension(_displayName);
        for (var n = 0; n < 1000; n++)
        {
            var candidate = n == 0 ? _displayName : $"{stem} ({n}){ext}";
            var path = Path.Combine(_directory, candidate);
            if (File.Exists(path) || Directory.Exists(path)) continue;
            try { File.Move(PartPath, path, overwrite: false); return candidate; }
            catch (IOException) when (File.Exists(path)) { /* taken a moment ago: try the next name */ }
        }
        throw new TransferException(TransferError.NameClash, "A file with that name already exists and no free name was found.");
    }

    /// <summary>
    /// Deletes the partial file (the sender cancelled, or the content is bad). If a completion won the race against a cancel and the result is already in place, that result is
    /// removed too: a cancelled transfer leaves nothing behind. Safe to call while another thread is writing or completing.
    /// </summary>
    public void Discard()
    {
        lock (_gate)
        {
            _done = true;
            _stream?.Dispose();
            _stream = null;
            TryDelete(PartPath);
            if (_storedPath is { } stored)
            {
                _storedPath = null;
                try
                {
                    if (Directory.Exists(stored)) Directory.Delete(stored, true);
                    else File.Delete(stored);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
        }
    }

    /// <summary>Closes the partial file. It is kept for a later attempt unless the transfer cannot be resumed.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stream is not null)
            {
                _stream.Dispose();
                _stream = null;
                if (!_done && !_resumable) TryDelete(PartPath);
            }
            _hash?.Dispose();
            _hash = null;
        }
    }

    /// <summary>Deletes ".part" files in a receive folder that have not been touched for a long time.</summary>
    public static void PurgeStalePartials(string directory, TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.part"))
                if (DateTime.UtcNow - file.LastWriteTimeUtc > olderThan) TryDelete(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}

/// <summary>Unpacks a received zip into a new folder without ever writing outside it.</summary>
public static class SafeZip
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into a fresh folder inside <paramref name="directory"/>. Entries with ".." parts, drive or root parts, links, or an unsafe total size are refused;
    /// names are made valid for any file system. The folder appears under its final name only once everything is written. Returns the folder's name.
    /// </summary>
    public static string ExtractToUniqueFolder(string zipPath, string directory, string suggestedName, FileTransferOptions options, long usedBytes = 0)
    {
        var temp = Path.Combine(directory, $".extracting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > options.MaxZipEntries) throw new TransferException(TransferError.UnsafeArchive, "The folder has too many files.");
            long declared = 0;
            foreach (var e in zip.Entries) declared += e.Length;
            if (declared > options.MaxFolderBytes) throw new TransferException(TransferError.TooLarge, "The folder is larger than the allowed size.");
            if (options.ReceiveQuotaBytes > 0 && usedBytes + declared > options.ReceiveQuotaBytes)
                throw new TransferException(TransferError.QuotaExceeded, "The receive folder is limited and this folder would go over it.");

            var items = zip.Entries.Select(e => (Entry: e, IsDirectory: e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'), Parts: SplitSafely(e.FullName))).ToList();
            var root = CommonRoot(items.Select(i => (i.Parts, i.IsDirectory)).ToList());
            var tempFull = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
            long written = 0;
            foreach (var (entry, isDirectory, allParts) in items)
            {
                var parts = root is null ? allParts : allParts[1..];
                if (parts.Length == 0) continue;
                if (!isDirectory && IsLink(entry)) continue;   // links are never created
                var target = Path.GetFullPath(Path.Combine(temp, Path.Combine(parts)));
                if (!target.StartsWith(tempFull, StringComparison.Ordinal))
                    throw new TransferException(TransferError.UnsafeArchive, "The folder contains a path that points outside of it.");
                if (isDirectory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
                try
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        if (written > options.MaxFolderBytes) throw new TransferException(TransferError.UnsafeArchive, "The folder expands to more than the allowed size.");
                        output.Write(buffer, 0, read);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            var baseName = FileNames.Sanitize(root ?? suggestedName);
            for (var n = 0; n < 1000; n++)
            {
                var name = n == 0 ? baseName : $"{baseName} ({n})";
                var finalPath = Path.Combine(directory, name);
                if (File.Exists(finalPath) || Directory.Exists(finalPath)) continue;
                try { Directory.Move(temp, finalPath); return name; }
                catch (IOException) when (Directory.Exists(finalPath)) { /* taken a moment ago: try the next name */ }
            }
            throw new TransferException(TransferError.NameClash, "A folder with that name already exists and no free name was found.");
        }
        catch (InvalidDataException ex) { throw new TransferException(TransferError.UnsafeArchive, "The folder arrived as a damaged or invalid zip file.", ex); }
        catch (Exception ex) when (ex is not TransferException) { throw TransferException.From(ex); }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    private static string[] SplitSafely(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length >= 2 && normalized[1] == ':'))
            throw new TransferException(TransferError.UnsafeArchive, "The folder contains an absolute path.");
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "." or "..") throw new TransferException(TransferError.UnsafeArchive, "The folder contains a path that points outside of it.");
            parts[i] = FileNames.Sanitize(parts[i]);
        }
        return parts;
    }

    /// <summary>The single top-level folder every entry lives in, or null.</summary>
    private static string? CommonRoot(List<(string[] Parts, bool IsDirectory)> all)
    {
        if (all.Count == 0 || all[0].Parts.Length == 0) return null;
        var first = all[0].Parts[0];
        foreach (var (parts, isDirectory) in all)
        {
            if (parts.Length == 0 || !string.Equals(parts[0], first, StringComparison.Ordinal)) return null;
            if (parts.Length == 1 && !isDirectory) return null;   // a file at the top level
        }
        return first;
    }

    private static bool IsLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
}
