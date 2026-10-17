using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Remotely.Server;

/// <summary>
/// A backup is a zip with manifest.json, devices.json, and (when they exist) settings.json, address.txt and server.pfx.
/// It holds the server's TLS private key, so it must be kept as private as the data directory. The admin token and the logs are not in it.
/// </summary>
public static class DataBackup
{
    public const int Format = 1;
    public const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxEntryBytes = 8 * 1024 * 1024;
    private static readonly string[] Names = ["manifest.json", "devices.json", "settings.json", "address.txt", "server.pfx"];

    public sealed record Contents(byte[] Devices, byte[]? Settings, byte[]? Address, byte[]? Pfx, string ServerVersion, DateTimeOffset CreatedUtc);

    public static byte[] Create(string dataDir, DeviceStore store, string serverVersion, string pin, DateTimeOffset now)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(new { format = Format, createdUtc = now, serverVersion, pin }));
            Add(zip, "devices.json", store.Snapshot());
            foreach (var name in new[] { "settings.json", "address.txt", "server.pfx" })
            {
                var path = Path.Combine(dataDir, name);
                if (File.Exists(path)) Add(zip, name, File.ReadAllBytes(path));
            }
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    /// <summary>Reads and checks a backup. Throws <see cref="DataFormatException"/> with a plain message if it is not one this server can restore.</summary>
    public static Contents Read(byte[] zipBytes)
    {
        var files = new Dictionary<string, byte[]>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > Names.Length) throw new DataFormatException("That file is not a Remotely server backup (too many files in it).");
            foreach (var entry in zip.Entries)
            {
                if (!Names.Contains(entry.FullName, StringComparer.Ordinal) || files.ContainsKey(entry.FullName))
                    throw new DataFormatException($"That file is not a Remotely server backup (unexpected entry '{Printable(entry.FullName)}').");
                using var stream = entry.Open();
                using var limited = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    limited.Write(chunk, 0, read);
                    if (limited.Length > MaxEntryBytes) throw new DataFormatException("That file is not a Remotely server backup (an entry is too large).");
                }
                files[entry.FullName] = limited.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            throw new DataFormatException("That file is not a Remotely server backup (it is not a readable zip).");
        }

        if (!files.TryGetValue("manifest.json", out var manifestBytes) || !files.TryGetValue("devices.json", out var devices))
            throw new DataFormatException("That file is not a Remotely server backup (manifest or device list missing).");
        try
        {
            using var manifest = JsonDocument.Parse(manifestBytes);
            var root = manifest.RootElement;
            var format = root.GetProperty("format").GetInt32();
            if (format > Format) throw new DataFormatException($"This backup was made by a newer Remotely server (backup format {format}; this server reads up to {Format}).");
            if (format < 1) throw new DataFormatException("That backup has an unknown format.");
            return new Contents(devices, files.GetValueOrDefault("settings.json"), files.GetValueOrDefault("address.txt"), files.GetValueOrDefault("server.pfx"),
                root.TryGetProperty("serverVersion", out var v) ? v.GetString() ?? "" : "",
                root.TryGetProperty("createdUtc", out var c) && c.TryGetDateTimeOffset(out var when) ? when : default);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DataFormatException("That file is not a Remotely server backup (the manifest cannot be read).");
        }
    }

    private static string Printable(string name) => new string(name.Where(c => !char.IsControl(c)).Take(40).ToArray());

    public static string TextOf(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
