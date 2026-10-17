using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Remotely.Core.Diagnostics;

/// <summary>
/// Optional local crash dumps. When the person has switched them on, <see cref="TryWrite"/> saves a minidump of this process (Windows only, through dbghelp.dll)
/// into a folder on this PC and keeps the newest few. Nothing is uploaded and the export never contains a dump, because a dump holds memory.
/// The Windows call has only been compiled on CI; it has not been run on a Windows PC by the author of this file.
/// </summary>
public static class CrashDumps
{
    public const int DefaultKept = 3;

    /// <summary>Writes a dump and returns its path, or null when dumps are not available here or the write failed. Never throws.</summary>
    public static string? TryWrite(string directory, string prefix, int keep = DefaultKept, Func<DateTimeOffset>? clock = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            Directory.CreateDirectory(directory);
            var when = (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var path = Path.Combine(directory, $"{prefix}-{when:yyyyMMdd-HHmmss}.dmp");
            if (!WriteMiniDump(path)) return null;
            Prune(directory, prefix, keep);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> dumps of one program (the name sorts by time).</summary>
    public static void Prune(string directory, string prefix, int keep)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            var files = Directory.GetFiles(directory, prefix + "-*.dmp").OrderBy(f => f, StringComparer.Ordinal).ToList();
            for (var i = 0; i < files.Count - Math.Max(0, keep); i++)
            {
                try { File.Delete(files[i]); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing to prune */ }
    }

    /// <summary>The dumps that exist (name and size only), for the Diagnostics page and the export's file list.</summary>
    public static IReadOnlyList<(string Name, long Bytes)> List(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        try { return [.. Directory.GetFiles(directory, "*.dmp").OrderBy(f => f, StringComparer.Ordinal).Select(f => (Path.GetFileName(f), new FileInfo(f).Length))]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    [SupportedOSPlatform("windows")]
    private static bool WriteMiniDump(string path)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var process = Process.GetCurrentProcess();
        const int MiniDumpWithDataSegs = 0x1, MiniDumpWithHandleData = 0x4, MiniDumpWithThreadInfo = 0x1000;
        return MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle.DangerousGetHandle(),
            MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithThreadInfo, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, int dumpType, IntPtr exception, IntPtr userStream, IntPtr callback);
}
