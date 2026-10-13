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
