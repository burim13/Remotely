using System.Security.Cryptography;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Storage;

namespace Remotely.App;

/// <summary>
/// What this viewer remembers: where the server is and who this device is. The values and the file format (schema version, migration from the 0.3.0 and 0.4.0 file)
/// are in <see cref="ViewerSettingsData"/>; this class adds the paths, the Windows-protected key file and the shared address book. The private key never leaves the key file.
/// </summary>
public sealed class ViewerSettings : ViewerSettingsData
{
    private static readonly byte[] Entropy = "Remotely.Viewer.v1"u8.ToArray();
    private static readonly Lazy<JsonAddressBook> Book = new(OpenAddressBook);
    private static readonly Lazy<IRecentPcs> Recent = new(() => new AddressBookRecentPcs(AddressBook));

    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remotely");
    private static string SettingsPath => Path.Combine(Dir, FileName);
    private static string KeyPath => Path.Combine(Dir, "viewer.key");

    /// <summary>How the last <see cref="Load"/> went (for example a damaged file that was set aside).</summary>
    public static SettingsLoadInfo LastLoad { get; private set; } = new(LoadStatus.Created, SchemaVersion, null);

    /// <summary>The saved PCs, shared by every screen so they all see the same list.</summary>
    public static JsonAddressBook AddressBook => Book.Value;

    /// <summary>The Connect screen's recent PCs, kept in the address book.</summary>
    public static IRecentPcs RecentPcs => Recent.Value;

    public static ViewerSettings Load()
    {
        var settings = SettingsFile.Read<ViewerSettings>(SettingsPath, SchemaVersion, Migrations, out var info);
        LastLoad = info;
        return settings;
    }

    public void Save() => SettingsFile.Write(SettingsPath, SchemaVersion, this);

    public static DeviceIdentity LoadOrCreateIdentity()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(KeyPath))
            return DeviceIdentity.FromPkcs8(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser));
        var identity = DeviceIdentity.Generate();
        File.WriteAllBytes(KeyPath, ProtectedData.Protect(identity.ExportPkcs8(), Entropy, DataProtectionScope.CurrentUser));
        return identity;
    }

    private static JsonAddressBook OpenAddressBook()
    {
        var path = Path.Combine(Dir, JsonAddressBook.FileName);
        var firstTime = !File.Exists(path);
        var book = new JsonAddressBook(path);
        var legacy = Path.Combine(Dir, "recent-pcs.json");
        if (firstTime && File.Exists(legacy)) AddressBookRecentPcs.ImportLegacy(book, new JsonRecentPcs(legacy));
        return book;
    }
}
