using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Remotely.Core.Crypto;

namespace Remotely.Viewer;

/// <summary>What this viewer remembers: where the server is and who this device is. The private key never leaves the Windows-protected key file.</summary>
public sealed class ViewerSettings
{
    private static readonly byte[] Entropy = "Remotely.Viewer.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ServerUri { get; set; } = "";
    public string ServerPin { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; set; } = "system";
    public bool SyncClipboard { get; set; } = true;
    public bool AllowInsecureTransport { get; set; }

    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remotely");
    private static string SettingsPath => Path.Combine(Dir, "viewer.json");
    private static string KeyPath => Path.Combine(Dir, "viewer.key");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUri);

    public static ViewerSettings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllBytes(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var temp = SettingsPath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temp, SettingsPath, overwrite: true);
    }

    public static DeviceIdentity LoadOrCreateIdentity()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(KeyPath))
            return DeviceIdentity.FromPkcs8(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser));
        var identity = DeviceIdentity.Generate();
        File.WriteAllBytes(KeyPath, ProtectedData.Protect(identity.ExportPkcs8(), Entropy, DataProtectionScope.CurrentUser));
        return identity;
    }
}
