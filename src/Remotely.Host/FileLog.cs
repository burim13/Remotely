using Remotely.Core.Diagnostics;

namespace Remotely.Host;

/// <summary>Best-effort log: always the console, plus rolling files (host-yyyyMMdd-NNN.log, secrets redacted) under the host's log folder when that is writable.</summary>
internal sealed class FileLog(string prefix)
{
    private static readonly object Gate = new();
    private static RollingFileLog? _files;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{prefix}] {Redactor.Shared.Apply(message)}";
        Console.WriteLine(line);
        Files().Info(prefix, message);
    }

    private static RollingFileLog Files()
    {
        lock (Gate)
        {
            if (_files is null || _files.Directory != HostPaths.LogDir)
                _files = new RollingFileLog(HostPaths.LogDir, "host", Redactor.Shared, DiagnosticsSettings.Load(HostPaths.DataDir).LogLevel);
            return _files;
        }
    }
}
