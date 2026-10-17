using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Remotely.Server;

/// <summary>
/// Structured logs: one JSON object per line in <c>logs/server.log</c>. The file is closed and renamed (server-yyyyMMdd-HHmmss.log) when it passes
/// the size limit or the day changes; only the newest few renamed files are kept. A full queue drops lines rather than slow the server down.
/// </summary>
public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private const int QueueLimit = 20_000;

    private readonly string _dir;
    private readonly string _current;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly BlockingCollection<byte[]> _queue = new(QueueLimit);
    private readonly Thread _writer;
    private FileStream? _file;
    private DateOnly _fileDay;
    private long _size;

    public RotatingFileLoggerProvider(string dir, long maxBytes, int keep)
    {
        _dir = dir;
        _current = Path.Combine(dir, "server.log");
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _keep = Math.Max(0, keep);
        Directory.CreateDirectory(dir);
        _writer = new Thread(Pump) { IsBackground = true, Name = "Remotely log writer" };
        _writer.Start();
    }

    public static string CurrentFile(string dataDir) => Path.Combine(dataDir, "logs", "server.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
    }

    private void Enqueue(byte[] line) => _queue.TryAdd(line);

    private void Pump()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Write(line);
                    if (_queue.Count == 0) _file?.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _file?.Dispose();
                    _file = null;   // try again with the next line; logging never takes the server down
                }
            }
        }
        finally
        {
            _file?.Dispose();
        }
    }

    private void Write(byte[] line)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_file is not null && (_size + line.Length > _maxBytes || today != _fileDay)) Roll();
        if (_file is null)
        {
            if (File.Exists(_current) && new FileInfo(_current).Length > 0 && DateOnly.FromDateTime(File.GetLastWriteTimeUtc(_current)) != today) Roll();
            _file = new FileStream(_current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _size = _file.Length;
            _fileDay = today;
        }
        _file.Write(line);
        _size += line.Length;
    }

    private void Roll()
    {
        _file?.Dispose();
        _file = null;
        if (File.Exists(_current))
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(_dir, $"server-{stamp}.log");
            for (var i = 1; File.Exists(target); i++) target = Path.Combine(_dir, $"server-{stamp}-{i}.log");
            File.Move(_current, target);
        }
        foreach (var old in Directory.GetFiles(_dir, "server-*.log").OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(f => f, StringComparer.Ordinal).Skip(_keep))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
    }

    private sealed class FileLogger(string category, RotatingFileLoggerProvider owner) : ILogger
    {
        private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var entry = new Dictionary<string, object?>
            {
                ["time"] = DateTimeOffset.UtcNow.ToString("O"),
                ["level"] = logLevel.ToString(),
                ["category"] = category,
                ["message"] = formatter(state, exception),
            };
            if (eventId.Id != 0) entry["eventId"] = eventId.Id;
            if (exception is not null) entry["exception"] = exception.ToString();
            owner.Enqueue(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n"));
        }
    }
}
