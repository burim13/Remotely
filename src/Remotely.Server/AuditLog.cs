using System.Text;
using System.Text.Json;
using Remotely.Core.Admin;

namespace Remotely.Server;

/// <summary>
/// Who did what, kept on disk as a ring of two JSON-lines segments (audit.log, audit.old.log) whose total size stays under <c>maxBytes</c>:
/// when the current segment is full the older one is dropped and the current one takes its place. A line torn by a crash is skipped when reading.
/// Entries: pairing, sessions between devices, device and invite changes by the administrator, and refused admin calls. Never holds keys or tokens.
/// </summary>
public sealed class AuditLog : IDisposable
{
    public const long MinBytes = 64 * 1024;
    private const int MaxDetail = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _current;
    private readonly string _old;
    private readonly long _segmentMax;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private FileStream? _stream;
    private long _size;

    public AuditLog(string dataDir, long maxBytes, TimeProvider time)
    {
        Directory.CreateDirectory(dataDir);
        _current = Path.Combine(dataDir, "audit.log");
        _old = Path.Combine(dataDir, "audit.old.log");
        _segmentMax = Math.Max(MinBytes, maxBytes) / 2;
        _time = time;
        lock (_gate) Open();
    }

    public long MaxBytes => _segmentMax * 2;

    private void Open()
    {
        var existing = File.Exists(_current) ? new FileInfo(_current).Length : 0;
        _stream = new FileStream(_current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _size = existing;
        if (existing > 0 && !EndsWithNewline())
        {
            _stream.WriteByte((byte)'\n');
            _size++;
        }
    }

    private bool EndsWithNewline()
    {
        using var read = new FileStream(_current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        read.Seek(-1, SeekOrigin.End);
        return read.ReadByte() == '\n';
    }

    public void Write(string kind, string deviceId, string? peerDeviceId, string result, string? detail = null)
    {
        var entry = new AuditEntry(_time.GetUtcNow(), kind, deviceId, peerDeviceId, result, Trim(detail));
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
        lock (_gate)
        {
            if (_stream is null) return;
            if (_size > 0 && _size + line.Length > _segmentMax) Rotate();
            _stream.Write(line);
            _stream.Flush();
            _size += line.Length;
        }
    }

    private void Rotate()
    {
        _stream!.Dispose();
        File.Move(_current, _old, overwrite: true);
        Open();
    }

    private static string? Trim(string? detail)
    {
        if (detail is null) return null;
        var clean = new string(detail.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > MaxDetail ? clean[..MaxDetail] : clean;
    }

    /// <summary>Entries newer than <paramref name="since"/>, newest first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<AuditEntry> Query(DateTimeOffset? since, int max)
    {
        var all = new List<AuditEntry>();
        lock (_gate)
        {
            foreach (var path in new[] { _old, _current })
                if (File.Exists(path)) ReadInto(path, all);
        }
        return [.. all.Where(e => since is null || e.TimeUtc > since).OrderByDescending(e => e.TimeUtc).Take(Math.Clamp(max, 1, 5000))];
    }

    private static void ReadInto(string path, List<AuditEntry> into)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<AuditEntry>(line, Json) is { } entry) into.Add(entry);
            }
            catch (JsonException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
