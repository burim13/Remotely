using System.Text.Json;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.App.Session.Files;

/// <summary>
/// What this viewer remembers about file transfers: where received files go and how much it accepts. Stored next to the other viewer settings (transfers.json).
/// The session window applies it to a session with <see cref="ApplyTo"/>.
/// </summary>
public sealed class FileTransferSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Folder for files received from the other PC. Empty means the Downloads folder.</summary>
    public string ReceiveFolder { get; set; } = "";
    /// <summary>Total size allowed in the receive folder, in megabytes. 0 means no limit.</summary>
    public long ReceiveQuotaMegabytes { get; set; }
    /// <summary>Largest single file or folder to accept, in megabytes. Default 32 GB.</summary>
    public long MaxItemMegabytes { get; set; } = 32 * 1024;
    /// <summary>Unpack received folders instead of leaving a zip file.</summary>
    public bool ExtractFolders { get; set; } = true;

    private static string PathOnDisk => System.IO.Path.Combine(ViewerSettings.Dir, "transfers.json");

    public static string DefaultFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>The folder to save into: the chosen one, or Downloads when none was chosen.</summary>
    public string EffectiveReceiveFolder => string.IsNullOrWhiteSpace(ReceiveFolder) ? DefaultFolder : ReceiveFolder;

    public static FileTransferSettings Load()
    {
        try { return File.Exists(PathOnDisk) ? JsonSerializer.Deserialize<FileTransferSettings>(File.ReadAllBytes(PathOnDisk)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ViewerSettings.Dir);
            var temp = PathOnDisk + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, Json));
            File.Move(temp, PathOnDisk, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the setting is lost, the transfer still works */ }
    }

    public FileTransferOptions ToOptions() => new()
    {
        MaxFileBytes = Math.Max(1, MaxItemMegabytes) << 20,
        MaxFolderBytes = Math.Max(1, MaxItemMegabytes) << 20,
        ReceiveQuotaBytes = Math.Max(0, ReceiveQuotaMegabytes) << 20,
        ExtractFolders = ExtractFolders,
    };

    /// <summary>Points a new session at the chosen receive folder and limits. Call before the first download.</summary>
    public void ApplyTo(ViewerSession session)
    {
        session.DownloadDirectory = EffectiveReceiveFolder;
        session.TransferOptions = ToOptions();
    }
}
