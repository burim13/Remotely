using System.Globalization;

namespace Remotely.Core.Update;

public enum UpdateSeverity { Info, Success, Warning, Error }

/// <summary>A line of text as a resource key plus its arguments, so each app looks the words up in its own <c>Resources.resw</c>.</summary>
public sealed record UpdateLine(string Key, object[] Args, UpdateSeverity Severity);

/// <summary>
/// What the Updates section and the banner say for each <see cref="UpdateState"/>. <see cref="English"/> holds the English text of every key; an app uses it when
/// its resource file does not have the key yet (so the screen never shows a bare key).
/// </summary>
public static class UpdatePresentation
{
    public static UpdateLine Status(UpdateState s, ReleaseVersion current) => s.Phase switch
    {
        UpdatePhase.Off => new("Update_Status_Off", [], UpdateSeverity.Info),
        UpdatePhase.Idle => new("Update_Status_Idle", [], UpdateSeverity.Info),
        UpdatePhase.Checking => new("Update_Status_Checking", [], UpdateSeverity.Info),
        UpdatePhase.UpToDate => new("Update_Status_UpToDate", [current.ToString()], UpdateSeverity.Success),
        UpdatePhase.Available => new("Update_Status_Available", [s.Offer?.Version.ToString() ?? ""], UpdateSeverity.Info),
        UpdatePhase.Downloading => new("Update_Status_Downloading", [s.Offer?.Version.ToString() ?? "", Math.Round(s.Progress * 100).ToString(CultureInfo.CurrentCulture)], UpdateSeverity.Info),
        UpdatePhase.Ready => new("Update_Status_Ready", [s.Offer?.Version.ToString() ?? ""], UpdateSeverity.Success),
        UpdatePhase.Unverifiable => new("Update_Status_Unverifiable", [s.Detail ?? ""], UpdateSeverity.Warning),
        UpdatePhase.NoPackage => new("Update_Status_NoPackage", [s.Detail ?? ""], UpdateSeverity.Warning),
        _ => new("Update_Status_Failed", [s.Detail ?? ""], UpdateSeverity.Error),
    };

    /// <summary>Second line under a downloaded package: whether and by whom it is signed.</summary>
    public static UpdateLine? Signature(UpdateState s) => s.Package?.Signature switch
    {
        { Status: SignatureStatus.Valid } sig => new("Update_Sig_Valid", [sig.Signer ?? ""], UpdateSeverity.Success),
        { Status: SignatureStatus.NotSigned } => new("Update_Sig_Unsigned", [], UpdateSeverity.Info),
        { Status: SignatureStatus.NotChecked } => new("Update_Sig_NotChecked", [], UpdateSeverity.Info),
        _ => null,
    };

    /// <summary>The banner appears only when there is something the person can act on.</summary>
    public static bool ShowBanner(UpdateState s) => s.Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready;

    public static string Format(string template, object[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, template, args); }
        catch (FormatException) { return template; }
    }

    public static string English(UpdateLine line) => Format(EnglishText.GetValueOrDefault(line.Key, line.Key), line.Args);

    public static string English(string key) => EnglishText.GetValueOrDefault(key, key);

    /// <summary>Every key the update screens use, with its English text. Add these to <c>Strings/en-US/Resources.resw</c> to translate them.</summary>
    public static IReadOnlyDictionary<string, string> EnglishText { get; } = new Dictionary<string, string>
    {
        ["Update_Section_Title"] = "Updates",
        ["Update_Toggle_Header"] = "Check for updates",
        ["Update_Toggle_On"] = "On",
        ["Update_Toggle_Off"] = "Off",
        ["Update_Toggle_Description"] = "When this is on, Remotely asks the project's releases page on GitHub once a day whether a newer version exists. It only tells you: nothing is installed by itself. It is off until you turn it on.",
        ["Update_CheckNow"] = "Check now",
        ["Update_Download"] = "Download",
        ["Update_Skip"] = "Skip this version",
        ["Update_OpenInstaller"] = "Open the installer",
        ["Update_ShowInFolder"] = "Show in folder",
        ["Update_ReleaseNotes"] = "What is new",
        ["Update_Banner_Title"] = "Version {0} is available",
        ["Update_Banner_Message"] = "Nothing is installed by itself. Download it, then open the installer when you are ready.",
        ["Update_Banner_Ready"] = "Version {0} was downloaded and checked. Open the installer when you are ready.",
        ["Update_Status_Off"] = "Update checks are off.",
        ["Update_Status_Idle"] = "Not checked yet.",
        ["Update_Status_Checking"] = "Checking…",
        ["Update_Status_UpToDate"] = "You have the latest version ({0}).",
        ["Update_Status_Available"] = "Version {0} is available.",
        ["Update_Status_Downloading"] = "Downloading version {0}… {1}%",
        ["Update_Status_Ready"] = "Version {0} was downloaded and checked against the SHA-256 published with the release.",
        ["Update_Status_Unverifiable"] = "A newer version exists, but its release does not publish a SHA-256 for the installer, so it is not offered. {0}",
        ["Update_Status_NoPackage"] = "A newer version exists, but it has no installer for this PC. {0}",
        ["Update_Status_Failed"] = "The update check or download did not work: {0}",
        ["Update_Sig_Valid"] = "The installer has a valid digital signature. Signed by: {0}",
        ["Update_Sig_Unsigned"] = "The installer is not digitally signed (Windows SmartScreen will warn). It matches the SHA-256 published with the release.",
        ["Update_Sig_NotChecked"] = "The digital signature was not checked on this system.",
        ["Update_Changed_Warning"] = "The downloaded file changed after it was checked, so it was not opened. Download it again.",
    };
}
