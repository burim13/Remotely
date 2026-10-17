using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Settings;

/// <summary>Fills the body of one settings section: its title and intro, the catalogued settings of that section, and the section's own blocks.</summary>
internal static class SectionView
{
    public static void Build(StackPanel body, SettingsSection section)
    {
        var policy = SettingsHub.Policy();
        if (section == SettingsSection.AddressBook)
        {
            body.Children.Add(new AddressBookBlock());
            return;
        }

        var set = SettingsHub.Load();
        body.Children.Add(SettingsUi.Title(Loc.Get("Settings_Section_" + section)));
        body.Children.Add(SettingsUi.Secondary(Loc.Get("Settings_Intro_" + section)));

        if (section == SettingsSection.Connection) body.Children.Add(ServerBlock.Create(policy));
        if (section == SettingsSection.Security) body.Children.Add(InfoBlocks.Policies(policy));

        foreach (var def in SettingsCatalog.In(section)) body.Children.Add(SettingRows.Create(def, set, policy));

        if (section == SettingsSection.Security) body.Children.Add(InfoBlocks.Device());
        if (section == SettingsSection.Advanced) body.Children.Add(AdvancedBlock.Create(policy));
        if (section == SettingsSection.About) body.Children.Add(InfoBlocks.About());
    }

    /// <summary>Things on a section that are not catalogued settings but should be found by the search box: (title key, help key, section).</summary>
    public static readonly (string TitleKey, string HelpKey, SettingsSection Section)[] SearchableBlocks =
    [
        ("Settings_Server_Title", "Settings_Server_Manual", SettingsSection.Connection),
        ("Settings_Policy_Title", "Settings_Policy_None", SettingsSection.Security),
        ("Settings_Device_Title", "Settings_Device_Help", SettingsSection.Security),
        ("Settings_Advanced_Title", "Settings_Advanced_Help", SettingsSection.Advanced),
        ("Settings_Section_AddressBook", "Settings_Intro_AddressBook", SettingsSection.AddressBook),
        ("Settings_Section_About", "Settings_About_Body", SettingsSection.About),
    ];
}
