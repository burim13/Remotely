using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Settings;

/// <summary>The read-only parts of the Security and About sections.</summary>
internal static class InfoBlocks
{
    /// <summary>What an administrator's policy changes on this PC, or a line saying none applies.</summary>
    public static UIElement Policies(PolicyOverrides policy)
    {
        var lines = new List<UIElement> { SettingsUi.Subtitle(Loc.Get("Settings_Policy_Title")) };
        if (!policy.Any)
        {
            lines.Add(SettingsUi.Secondary(Loc.Get("Settings_Policy_None")));
        }
        else
        {
            lines.Add(SettingsUi.Bar(InfoBarSeverity.Informational, Loc.Get("Settings_Policy_Active")));
            if (policy.DisableServerMode) lines.Add(SettingsUi.Strong("• " + Loc.Get("Settings_Policy_DisableServerMode")));
            if (policy.ForceConsent) lines.Add(SettingsUi.Strong("• " + Loc.Get("Settings_Policy_ForceConsent")));
            if (policy.DisableFileTransfer) lines.Add(SettingsUi.Strong("• " + Loc.Get("Settings_Policy_DisableFileTransfer")));
        }
        return SettingsUi.CardOf([.. lines]);
    }

    /// <summary>This device's id (what a PC reached through a server must trust) and whether the server link is encrypted.</summary>
    public static UIElement Device()
    {
        var id = App.Connection.Identity.DeviceId;
        var copy = SettingsUi.ButtonOf("Settings_Device_Copy", (_, _) => App.Services.CopyText(id));
        var items = new List<UIElement>
        {
            SettingsUi.Subtitle(Loc.Get("Settings_Device_Title")),
            SettingsUi.Secondary(Loc.Get("Settings_Device_Help")),
            SettingsUi.Mono(id),
            copy,
        };
        if (App.Settings.IsConfigured)
            items.Add(App.Settings.AllowInsecureTransport
                ? SettingsUi.Bar(InfoBarSeverity.Warning, Loc.Get("Settings_Device_Insecure"))
                : SettingsUi.Secondary(Loc.Get("Settings_Device_Secure")));
        return SettingsUi.CardOf([.. items]);
    }

    public static UIElement About()
    {
        var version = typeof(InfoBlocks).Assembly.GetName().Version?.ToString(3) ?? "";
        var link = new HyperlinkButton { Content = Loc.Get("Settings_About_Project"), NavigateUri = new Uri("https://github.com/burim13/Remotely") };
        return SettingsUi.CardOf(
            SettingsUi.Subtitle(Loc.Get("App_Name") + " " + version),
            SettingsUi.Secondary(Loc.Get("Settings_About_Body")),
            SettingsUi.Secondary(SettingsUi.Format("Settings_About_Folder", ViewerSettings.Dir)),
            SettingsUi.Secondary(SettingsUi.Format("Settings_About_Schema", ViewerSettingsData.SchemaVersion, JsonAddressBook.SchemaVersion)),
            SettingsUi.Secondary(Loc.Get("Settings_About_License")),
            link);
    }
}
