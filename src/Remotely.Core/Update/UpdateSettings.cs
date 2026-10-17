using System.Text.Json;

namespace Remotely.Core.Update;

/// <summary>
/// The update preferences of one app, stored per Windows user. <see cref="Enabled"/> is false until the person turns update checks on: a fresh install, a damaged file
/// and a missing file all mean "never contact GitHub on my own".
/// </summary>
public sealed class UpdateSettings
{
    public int Schema { get; set; } = 1;
    public bool Enabled { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>A version the person chose to skip; it is not offered again, a newer one is.</summary>
    public string? SkippedVersion { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string appFolderName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appFolderName, "updates.json");

    public static UpdateSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path)) ?? new UpdateSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new UpdateSettings(); }
    }

    /// <summary>Temporary file, then move, so a crash never leaves half a file. Returns false when the folder is not writable.</summary>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
