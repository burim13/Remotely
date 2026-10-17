namespace Remotely.Unattended;

/// <summary>
/// The only things the app ever asks Windows to run elevated. Each is a fixed argument list for <c>Remotely.Host.exe</c>; nothing typed by the person or read from a file
/// is passed on, so the UAC prompt can only ever lead to one of these.
/// </summary>
public enum ElevatedAction
{
    InstallService,
    UninstallService,
    StartService,
    StopService,
    AllowLan,
    DisallowLan,
    AskOn,
    AskOff,
    KeepAwakeOn,
    KeepAwakeOff,
    SasOn,
    SasOff,
}

public static class ElevatedActions
{
    public const string HostFileName = "Remotely.Host.exe";

    public static IReadOnlyList<string> Arguments(ElevatedAction action) => action switch
    {
        ElevatedAction.InstallService => ["install"],
        ElevatedAction.UninstallService => ["uninstall"],
        ElevatedAction.StartService => ["service-start"],
        ElevatedAction.StopService => ["service-stop"],
        ElevatedAction.AllowLan => ["unattended", "lan", "on"],
        ElevatedAction.DisallowLan => ["unattended", "lan", "off"],
        ElevatedAction.AskOn => ["unattended", "ask", "on"],
        ElevatedAction.AskOff => ["unattended", "ask", "off"],
        ElevatedAction.KeepAwakeOn => ["unattended", "keep-awake", "on"],
        ElevatedAction.KeepAwakeOff => ["unattended", "keep-awake", "off"],
        ElevatedAction.SasOn => ["unattended", "sas", "on"],
        ElevatedAction.SasOff => ["unattended", "sas", "off"],
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// True when <paramref name="hostExePath"/> is <c>Remotely.Host.exe</c> inside a folder below <paramref name="programFiles"/>. The app refuses to ask for administrator
    /// rights for a program in a folder an ordinary user can write to (a per-user install, a download folder): whoever can replace that file could otherwise have it run as
    /// administrator the next time the person clicks. Judged on the text of the path only (no symbolic links or junctions are resolved, no folder permissions are read),
    /// with both slash styles and any letter case accepted and any "." or ".." segment or UNC path refused.
    /// </summary>
    public static bool IsTrustedLocation(string? hostExePath, string? programFiles)
    {
        if (string.IsNullOrWhiteSpace(hostExePath) || string.IsNullOrWhiteSpace(programFiles)) return false;
        var exe = Segments(hostExePath);
        var root = Segments(programFiles);
        if (exe is null || root is null || root.Length == 0 || exe.Length < root.Length + 2) return false;
        for (var i = 0; i < root.Length; i++)
            if (!string.Equals(exe[i], root[i], StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(exe[^1], HostFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string[]? Segments(string path)
    {
        var text = path.Trim().Replace('\\', '/');
        if (text.StartsWith("//", StringComparison.Ordinal)) return null;   // UNC and device paths
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) return null;
        return parts;
    }
}

/// <summary>The policy "Disable or enable software Secure Attention Sequence" (registry value <c>SoftwareSASGeneration</c>): 0 none, 1 services, 2 Ease of Access apps, 3 both.</summary>
public static class SasPolicy
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string ValueName = "SoftwareSASGeneration";

    /// <summary>Whether a service (which is what Remotely.Host is) may send Ctrl+Alt+Del. A missing value means the policy is not configured: no.</summary>
    public static bool ServicesMaySend(int? value) => value is 1 or 3;
}
