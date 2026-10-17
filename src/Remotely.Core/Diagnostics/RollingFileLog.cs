using System.Text;

namespace Remotely.Core.Diagnostics;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>
/// Log files for one program: <c>{prefix}-yyyyMMdd-NNN.log</c> in one folder, a new file when the day changes or the size limit is passed, and only the newest few files kept.
/// Every line is <c>2026-10-17T09:30:00.123Z INFO  [source] message</c> (UTC). Text passes through a <see cref="Redactor"/> before it is written; line breaks inside a message
/// are written as <c>\n</c> so that one message is one line (a message cannot forge a log line), and the stack trace of an exception follows on lines that start with four spaces.
/// Writing never throws: a disk that is full or locked costs lines (counted in <see cref="DroppedLines"/>), not the program.
/// </summary>
public sealed class RollingFileLog
{
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024;
    public const int DefaultFilesKept = 10;

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _prefix;
    private readonly Redactor _redactor;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly Func<DateTimeOffset> _clock;
    private string? _currentPath;
    private string _currentDay = "";
    private int _currentIndex;
    private long _currentSize;
    private long _dropped;
    private bool _pruneAfterWrite;

    public RollingFileLog(string directory, string prefix, Redactor? redactor = null, LogLevel minimum = LogLevel.Info,
        long maxFileBytes = DefaultMaxFileBytes, int filesKept = DefaultFilesKept, Func<DateTimeOffset>? clock = null)
    {
        _dir = directory;
        _prefix = prefix;
        _redactor = redactor ?? Redactor.Shared;
        Minimum = minimum;
        _maxBytes = Math.Max(256, maxFileBytes);
        _keep = Math.Max(1, filesKept);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Lines below this level are not written. Can be changed while running (from the Diagnostics page).</summary>
    public LogLevel Minimum { get; set; }

    public string Directory => _dir;
    public string Prefix => _prefix;
    public long DroppedLines => Interlocked.Read(ref _dropped);

    public bool IsEnabled(LogLevel level) => level >= Minimum;

    public void Write(LogLevel level, string source, string message, Exception? exception = null)
    {
        if (!IsEnabled(level)) return;
        try
        {
            var now = _clock().ToUniversalTime();
            var text = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")).Append(' ')
                .Append(level switch { LogLevel.Debug => "DEBUG", LogLevel.Info => "INFO ", LogLevel.Warning => "WARN ", _ => "ERROR" })
                .Append(" [").Append(OneLine(source)).Append("] ").Append(OneLine(message));
            if (exception is not null)
                foreach (var line in exception.ToString().Split('\n'))
                    text.Append('\n').Append("    ").Append(line.TrimEnd('\r'));
            text.Append('\n');
            var bytes = new UTF8Encoding(false).GetBytes(_redactor.Apply(text.ToString()));
            lock (_gate)
            {
                var path = PathFor(now, bytes.Length);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
                _currentSize += bytes.Length;
                if (_pruneAfterWrite) { _pruneAfterWrite = false; Prune(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public void Warning(string source, string message) => Write(LogLevel.Warning, source, message);
    public void Error(string source, string message, Exception? exception = null) => Write(LogLevel.Error, source, message, exception);

    /// <summary>Adapts the log to the <c>Action&lt;string&gt;</c> log delegates the host, the share code and the connection code already take.</summary>
    public Action<string> AsDelegate(string source, LogLevel level = LogLevel.Info) => message => Write(level, source, message);

    private static string OneLine(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

    private string PathFor(DateTimeOffset now, int incoming)
    {
        var day = now.ToString("yyyyMMdd");
        if (_currentPath is null || day != _currentDay)
        {
            System.IO.Directory.CreateDirectory(_dir);
            _currentDay = day;
            _currentIndex = LastIndexOnDisk(day);
            _currentPath = Path.Combine(_dir, $"{_prefix}-{day}-{_currentIndex:000}.log");
            _currentSize = File.Exists(_currentPath) ? new FileInfo(_currentPath).Length : 0;
            _pruneAfterWrite = true;
        }
        if (_currentSize > 0 && _currentSize + incoming > _maxBytes)
        {
            _currentIndex++;
            _currentPath = Path.Combine(_dir, $"{_prefix}-{day}-{_currentIndex:000}.log");
            _currentSize = 0;
            _pruneAfterWrite = true;
        }
        return _currentPath;
    }

    private int LastIndexOnDisk(string day)
    {
        var last = 0;
        foreach (var file in Files(_dir, _prefix))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name[(_prefix.Length + 1)..].Split('-');
            if (parts.Length == 2 && parts[0] == day && int.TryParse(parts[1], out var index)) last = Math.Max(last, index);
        }
        return last;
    }

    private void Prune()
    {
        var files = Files(_dir, _prefix);
        for (var i = 0; i < files.Count - _keep; i++)
        {
            try { File.Delete(files[i]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays; tried again next time */ }
        }
    }

    /// <summary>The log files of one program, oldest first (the name sorts by day, then number).</summary>
    public static IReadOnlyList<string> Files(string directory, string prefix)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        try
        {
            return [.. System.IO.Directory.GetFiles(directory, prefix + "-*.log").Where(f => IsLogName(Path.GetFileName(f), prefix)).OrderBy(f => f, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsLogName(string name, string prefix)
    {
        if (!name.StartsWith(prefix + "-", StringComparison.Ordinal) || !name.EndsWith(".log", StringComparison.Ordinal)) return false;
        var parts = name[(prefix.Length + 1)..^4].Split('-');
        return parts.Length == 2 && parts[0].Length == 8 && parts[0].All(char.IsAsciiDigit) && parts[1].Length >= 3 && parts[1].All(char.IsAsciiDigit);
    }

    /// <summary>The last <paramref name="count"/> lines across the files of one program, oldest first. Reads files that another process is writing.</summary>
    public static IReadOnlyList<string> Tail(string directory, string prefix, int count) => TailOf(Files(directory, prefix), count);

    /// <summary>
    /// The same for the server's own log naming: <c>server.log</c> is the current file and <c>server-yyyyMMdd-HHmmss.log</c> the renamed older ones
    /// (see Remotely.Server.RotatingFileLoggerProvider).
    /// </summary>
    public static IReadOnlyList<string> TailServerStyle(string directory, string baseName, int count)
    {
        var files = new List<string>();
        try
        {
            if (System.IO.Directory.Exists(directory))
                files.AddRange(System.IO.Directory.GetFiles(directory, baseName + "-*.log").OrderBy(f => f, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only the current file then */ }
        var current = Path.Combine(directory, baseName + ".log");
        if (File.Exists(current)) files.Add(current);
        return TailOf(files, count);
    }

    private static List<string> TailOf(IReadOnlyList<string> filesOldestFirst, int count)
    {
        var lines = new List<string>();
        foreach (var file in filesOldestFirst.Reverse())
        {
            lines.InsertRange(0, TailOfFile(file, count - lines.Count));
            if (lines.Count >= count) break;
        }
        return lines;
    }

    /// <summary>The last lines of one text file; reads at most the last 1 MiB. Returns nothing when the file cannot be read.</summary>
    public static IReadOnlyList<string> TailOfFile(string path, int count)
    {
        if (count <= 0) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const long Window = 1024 * 1024;
            var start = Math.Max(0, stream.Length - Window);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var all = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0);   // cut in the middle of a line
            if (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
            return all.Count <= count ? all : all.GetRange(all.Count - count, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
