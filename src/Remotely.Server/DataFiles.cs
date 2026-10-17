namespace Remotely.Server;

internal static class DataFiles
{
    /// <summary>Temp file, flush to disk, then move over the target, so a crash leaves either the old file or the new one.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(content);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
