using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Remotely.Core.Crypto;

namespace Remotely.Host;

/// <summary>Where the host keeps its files. Everything lives under %ProgramData%\Remotely, readable only by SYSTEM and administrators.</summary>
internal static class HostPaths
{
    private static readonly byte[] Entropy = "Remotely.Host.v1"u8.ToArray();

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remotely");
    public static string ConfigFile => Path.Combine(DataDir, "host.json");
    public static string KeyFile => Path.Combine(DataDir, "host.key");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string ProgramDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Remotely");
    public static string InstalledExe => Path.Combine(ProgramDir, "Remotely.Host.exe");

    public static void EnsureDataDir()
    {
        if (Directory.Exists(DataDir)) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DataDir).Create(security);
    }

    /// <summary>The private key is protected with DPAPI (machine scope); the directory ACL is what keeps other users from reading the file.</summary>
    public static DeviceIdentity LoadOrCreateIdentity()
    {
        EnsureDataDir();
        if (File.Exists(KeyFile))
            return DeviceIdentity.FromPkcs8(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.LocalMachine));
        var identity = DeviceIdentity.Generate();
        File.WriteAllBytes(KeyFile, ProtectedData.Protect(identity.ExportPkcs8(), Entropy, DataProtectionScope.LocalMachine));
        return identity;
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
