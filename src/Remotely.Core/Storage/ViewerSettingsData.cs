using System.Text.Json.Serialization;

namespace Remotely.Core.Storage;

/// <summary>
/// The values in <c>viewer.json</c>. The app's <c>ViewerSettings</c> derives from this and adds the Windows-only parts (the protected key file). It lives here, UI-free,
/// so the file format, the migration and the defaults are tested on every platform.
/// <para>Schema 0 is what Remotely 0.3.0 and 0.4.0 wrote: six values and no <c>SchemaVersion</c>, plus the read-only <c>IsConfigured</c>, which was serialised by mistake.
/// Schema 1 removes that value, and every later key (shell, language, keyboard, clipboard, file transfer switch) is optional with a default, so a schema 0 file loads unchanged.</para>
/// </summary>
public class ViewerSettingsData : ISettingsNormalize
{
    public const int SchemaVersion = 1;

    public const string FileName = "viewer.json";

    public static readonly string[] Themes = ["system", "dark", "light"];
    public static readonly string[] KeyboardModes = ["auto", "scancode", "unicode"];
    public static readonly string[] Languages = ["system", "en-US", "sq-AL"];

    public const int ClipboardConfirmMegabytesMin = 1;
    public const int ClipboardConfirmMegabytesMax = 1024;
    public const int ClipboardConfirmMegabytesDefault = 8;

    public static IReadOnlyList<SettingsMigration> Migrations { get; } =
    [
        new SettingsMigration(0, root => root.Remove("IsConfigured")),
    ];

    public string ServerUri { get; set; } = "";
    public string ServerPin { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; set; } = "system";
    public bool SyncClipboard { get; set; } = true;
    public bool AllowInsecureTransport { get; set; }

    // ---- app shell (agent A2) ----
    /// <summary>True once the first-run flow was finished or skipped.</summary>
    public bool OnboardingDone { get; set; }
    /// <summary>Closing the window hides it to the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; }
    /// <summary>The choice behind the start-with-Windows option; the Run registry value is what Windows reads.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>Start sharing on the same network when the app opens. Chosen in the first-run flow; off otherwise.</summary>
    public bool ShareOnStart { get; set; }
    /// <summary>Last normal (not maximized) window position and size in physical pixels, and whether it was maximized.</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // ---- settings pages (agent A14) ----
    /// <summary>"system" (follow Windows), "en-US" or "sq-AL". Applied at the next start.</summary>
    public string Language { get; set; } = "system";
    /// <summary>"auto", "scancode" or "unicode": how typed keys are sent to the other PC (see KeyboardModeSetting in ViewerCore).</summary>
    public string KeyboardMode { get; set; } = "auto";
    /// <summary>A clipboard copy bigger than this many megabytes needs the receiving person to agree first (see ClipLimits.ConfirmAboveBytes in HostCore).</summary>
    public int ClipboardConfirmMegabytes { get; set; } = ClipboardConfirmMegabytesDefault;
    /// <summary>False hides and refuses sending and receiving files in a session. The policy DisableFileTransfer forces this to false.</summary>
    public bool FileTransferEnabled { get; set; } = true;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUri);

    public void Normalize()
    {
        if (!Themes.Contains(Theme)) Theme = "system";
        if (!KeyboardModes.Contains(KeyboardMode)) KeyboardMode = "auto";
        if (!Languages.Contains(Language)) Language = "system";
        ClipboardConfirmMegabytes = Math.Clamp(ClipboardConfirmMegabytes, ClipboardConfirmMegabytesMin, ClipboardConfirmMegabytesMax);
        if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;
        ServerUri ??= "";
        ServerPin ??= "";
    }
}
