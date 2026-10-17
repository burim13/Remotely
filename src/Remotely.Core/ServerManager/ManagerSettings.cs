using System.Text.Json;

namespace Remotely.Core.ServerManager;

/// <summary>The Manager's own preferences (not the server's settings, which live on the server). Stored per Windows user.</summary>
public sealed class ManagerSettings
{
    public const string AutoStartValueName = "Remotely Server Manager";
    public const string AutoStartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool NotifyNewDevices { get; set; } = true;
    public string Theme { get; set; } = "system";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remotely Server Manager", "manager.json");

    /// <summary>The value written under the Run key. Starts the Manager hidden in the tray.</summary>
    public static string AutoStartCommand(string exePath) => "\"" + exePath + "\" --minimized";

    /// <summary>A missing or damaged file gives the defaults; the Manager must always start.</summary>
    public static ManagerSettings Load(string? path = null)
    {
        try
        {
            var text = File.ReadAllText(path ?? DefaultPath);
            var loaded = JsonSerializer.Deserialize<ManagerSettings>(text) ?? new ManagerSettings();
            if (loaded.Theme is not ("system" or "dark" or "light")) loaded.Theme = "system";
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ManagerSettings();
        }
    }

    /// <summary>Writes a temporary file and moves it into place, so a crash never leaves half a file.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
