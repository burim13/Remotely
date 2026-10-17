using System.IO.Compression;

namespace Remotely.Core.ServerManager;

/// <summary>
/// Puts a backup back into the server's data folder. The backup format is chosen by the server (<c>IServerAdmin.CreateBackupAsync</c> returns the path it wrote);
/// this accepts the two shapes a backup can reasonably have: a .zip file or a plain folder. Nothing outside the data folder is ever written.
/// The caller stops the service first; see <see cref="ServiceHelper"/>.
/// </summary>
public static class BackupRestorer
{
    public const string SafetyFolder = "before-restore";

    /// <summary>Copies the backup over the data folder and returns how many files were restored. The files being replaced are first kept in <see cref="SafetyFolder"/>.</summary>
    public static int Restore(string backupPath, string dataDir)
    {
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar)) ?? Path.GetTempPath(),
            ".restore-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(staging);
            if (Directory.Exists(backupPath)) CopyTree(backupPath, staging);
            else if (File.Exists(backupPath) && backupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ExtractSafely(backupPath, staging);
            else throw new FileNotFoundException("The backup is neither a folder nor a .zip file.", backupPath);

            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0) throw new InvalidDataException("The backup is empty.");

            Directory.CreateDirectory(dataDir);
            var safety = Path.Combine(dataDir, SafetyFolder);
            if (Directory.Exists(safety)) Directory.Delete(safety, recursive: true);
            foreach (var existing in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dataDir, existing);
                if (relative.StartsWith(SafetyFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                var target = Path.Combine(safety, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(existing, target, overwrite: true);
            }
            foreach (var file in files)
            {
                var target = Path.Combine(dataDir, Path.GetRelativePath(staging, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            return files.Count;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;   // never follow links out of the backup folder
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private static void ExtractSafely(string zipPath, string to)
    {
        var root = Path.GetFullPath(to) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(to, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("The backup contains a path outside its folder: " + entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
