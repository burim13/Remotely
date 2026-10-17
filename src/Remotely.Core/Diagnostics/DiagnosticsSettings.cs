using System.Text.Json;

namespace Remotely.Core.Diagnostics;

/// <summary>
/// The two things a person can switch about diagnostics, kept in their own small file (<c>diagnostics.json</c>) so no other settings file changes:
/// how much is logged, and whether a crash writes a local memory dump. Nothing here is ever sent anywhere.
/// </summary>
public sealed class DiagnosticsSettings
{
    public const string FileName = "diagnostics.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>Off by default. A dump holds the program's memory (which can include keys and tokens), stays on this PC and is never put in the diagnostics export.</summary>
    public bool CrashDumps { get; set; }

    /// <summary>A missing or damaged file gives the defaults.</summary>
    public static DiagnosticsSettings Load(string directory)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<DiagnosticsSettings>(File.ReadAllText(Path.Combine(directory, FileName))) ?? new DiagnosticsSettings();
            if (!Enum.IsDefined(loaded.LogLevel)) loaded.LogLevel = LogLevel.Info;
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new DiagnosticsSettings();
        }
    }

    /// <summary>Temporary file, then move, so a crash never leaves half a file. Returns false when the folder cannot be written.</summary>
    public bool TrySave(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
