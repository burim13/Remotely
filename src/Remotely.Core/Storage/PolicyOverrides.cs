using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Remotely.Core.Storage;

/// <summary>Where policy values come from. The registry on a managed Windows PC; a dictionary in tests.</summary>
public interface IPolicySource
{
    /// <summary>The DWORD value with this name, or null when it is missing or not a DWORD.</summary>
    int? GetDword(string name);
}

/// <summary>Machine-wide rules set by an administrator under <c>HKLM\Software\Policies\Remotely</c> (see <c>docs/policy/Remotely.admx</c>). A user cannot change them from the settings page.</summary>
public sealed record PolicyOverrides(bool DisableServerMode, bool ForceConsent, bool DisableFileTransfer)
{
    public const string RegistryPath = @"Software\Policies\Remotely";
    public const string DisableServerModeValue = "DisableServerMode";
    public const string ForceConsentValue = "ForceConsent";
    public const string DisableFileTransferValue = "DisableFileTransfer";

    public static readonly PolicyOverrides None = new(false, false, false);

    public bool Any => DisableServerMode || ForceConsent || DisableFileTransfer;

    /// <summary>A policy is on when its value is a DWORD other than 0. A missing value, a value of another type, or 0 leaves the person's own choice in place.</summary>
    public static PolicyOverrides Read(IPolicySource source) => new(
        source.GetDword(DisableServerModeValue) is { } a && a != 0,
        source.GetDword(ForceConsentValue) is { } b && b != 0,
        source.GetDword(DisableFileTransferValue) is { } c && c != 0);

    /// <summary>Reads HKLM on this PC. Anywhere else, or when the key cannot be read, no policy applies.</summary>
    public static PolicyOverrides ReadMachine() => OperatingSystem.IsWindows() ? Read(new RegistryPolicySource(Registry.LocalMachine)) : None;

    /// <summary>"Ask me before someone connects" as it works now: always on under the ForceConsent policy.</summary>
    public bool EffectiveAskBeforeConnect(bool chosen) => ForceConsent || chosen;

    /// <summary>File transfer as it works now: always off under the DisableFileTransfer policy.</summary>
    public bool EffectiveFileTransfer(bool chosen) => !DisableFileTransfer && chosen;
}

public sealed class DictionaryPolicySource(IReadOnlyDictionary<string, object> values) : IPolicySource
{
    public int? GetDword(string name) => values.TryGetValue(name, out var v) && v is int i ? i : null;
}

[SupportedOSPlatform("windows")]
public sealed class RegistryPolicySource(RegistryKey hive) : IPolicySource
{
    public int? GetDword(string name)
    {
        try
        {
            using var key = hive.OpenSubKey(PolicyOverrides.RegistryPath, writable: false);
            return key?.GetValue(name) is int i && key.GetValueKind(name) == RegistryValueKind.DWord ? i : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
