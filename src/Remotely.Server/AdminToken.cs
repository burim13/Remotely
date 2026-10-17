using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Remotely.Core.Admin;

namespace Remotely.Server;

/// <summary>
/// The secret that /admin/v1 calls present as a bearer token. It lives in <c>admin.token</c> in the data directory, created with a restrictive
/// access list: on Unix owner read/write only; on Windows only SYSTEM, Administrators, LocalService and the account that created it. It can be rotated
/// (the old value stops working at once). The server keeps only the token itself in memory, and compares in constant time.
/// </summary>
public sealed class AdminToken
{
    private readonly object _gate = new();
    private byte[] _hash;

    private AdminToken(string path, string token)
    {
        Path = path;
        _hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public string Path { get; }

    /// <summary>Loads the token, or creates it. Returns null (and says why in <paramref name="problem"/>) when the file can be neither read nor replaced.</summary>
    public static AdminToken? LoadOrCreate(string dataDir, out string? problem)
    {
        problem = null;
        Directory.CreateDirectory(dataDir);
        var path = System.IO.Path.Combine(dataDir, ServerAdminClient.TokenFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32) return new AdminToken(path, existing);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"The admin token file could not be read ({ex.Message}); making a new one.";
        }
        try
        {
            var token = NewValue();
            WriteProtected(path, token);
            return new AdminToken(path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"The admin token file could not be written ({ex.Message}). The admin API v1 is switched off.";
            return null;
        }
    }

    public bool Verify(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        byte[] hash;
        lock (_gate) hash = _hash;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), hash);
    }

    /// <summary>Makes a new token, writes it, and makes the old one invalid. The file is replaced in one step.</summary>
    public string Rotate()
    {
        var token = NewValue();
        lock (_gate)
        {
            WriteProtected(Path, token);
            _hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }
        return token;
    }

    private static string NewValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void WriteProtected(string path, string token)
    {
        var temp = path + ".new";
        if (File.Exists(temp)) File.Delete(temp);
        var bytes = Encoding.UTF8.GetBytes(token);
        if (OperatingSystem.IsWindows()) WriteWindows(path, temp, bytes);
        else WriteUnix(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteUnix(string temp, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using var file = new FileStream(temp, options);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, string temp, byte[] bytes)
    {
        var security = new FileSecurity();
        if (File.Exists(path))
        {
            // Rotating: keep the access list the file already has (it may include the administrator who installed the service).
            security.SetSecurityDescriptorBinaryForm(new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        }
        else
        {
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalServiceSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            if (WindowsIdentity.GetCurrent().User is { } me)
                security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        using var file = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
