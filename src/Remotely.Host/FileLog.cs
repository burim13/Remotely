namespace Remotely.Host;

/// <summary>Best-effort log: always the console, plus a daily file under %ProgramData%\Remotely\logs when that is writable.</summary>
internal sealed class FileLog(string prefix)
{
    private readonly object _gate = new();

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{prefix}] {message}";
        Console.WriteLine(line);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(HostPaths.LogDir);
                File.AppendAllText(Path.Combine(HostPaths.LogDir, $"host-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* console only */ }
        }
    }
}
