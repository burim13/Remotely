using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Remotely.Core.Crypto;

namespace Remotely.Host;

/// <summary>Where the host keeps its files. Everything lives under %ProgramData%\Remotely, readable only by SYSTEM and administrators.</summary>
internal static class HostPaths
{
    private static readonly byte[] Entropy = "Remotely.Host.v1"u8.ToArray();

    /// <summary>User-only mode: no service, no administrator. Everything lives in the user's own profile and runs only while they are signed in.</summary>
    public static bool UserMode { get; set; }

    public static string DataDir => Path.Combine(Environment.GetFolderPath(UserMode ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.CommonApplicationData), "Remotely");
    public static string ConfigFile => Path.Combine(DataDir, "host.json");
    public static string KeyFile => Path.Combine(DataDir, "host.key");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string ProgramDir => UserMode
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Remotely")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Remotely");
    /// <summary>Where files sent by a viewer land. Service mode uses the shared Public Documents so the signed-in user can open them.</summary>
    public static string ReceiveDir => Path.Combine(Environment.GetFolderPath(UserMode ? Environment.SpecialFolder.MyDocuments : Environment.SpecialFolder.CommonDocuments), "Remotely", "Received");
    /// <summary>Files placed here can be downloaded by trusted viewers. Nothing else on the PC is reachable.</summary>
    public static string ShareDir => Path.Combine(Environment.GetFolderPath(UserMode ? Environment.SpecialFolder.MyDocuments : Environment.SpecialFolder.CommonDocuments), "Remotely", "Share");
    public static string InstalledExe => Path.Combine(ProgramDir, "Remotely.Host.exe");

    public static void EnsureDataDir()
    {
        if (Directory.Exists(DataDir)) return;
        if (UserMode) { Directory.CreateDirectory(DataDir); return; }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DataDir).Create(security);
    }

    private static DataProtectionScope Scope => UserMode ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine;

    /// <summary>The private key is protected with DPAPI (machine scope for the service, user scope otherwise); the directory ACL is what keeps other users from reading the file.</summary>
    public static DeviceIdentity LoadOrCreateIdentity()
    {
        EnsureDataDir();
        if (File.Exists(KeyFile))
            return DeviceIdentity.FromPkcs8(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, Scope));
        var identity = DeviceIdentity.Generate();
        File.WriteAllBytes(KeyFile, ProtectedData.Protect(identity.ExportPkcs8(), Entropy, Scope));
        return identity;
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
