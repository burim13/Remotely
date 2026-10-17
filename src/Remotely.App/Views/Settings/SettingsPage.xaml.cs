using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Remotely.App.Shell;
using Remotely.App.Views.Settings;
using Remotely.Core.Storage;
using Remotely.UI.Localization;

namespace Remotely.App;

/// <summary>
/// Settings: a list of sections (General, Sharing, Connection, Security, Appearance, Advanced, About, Address book) and a search box. Every section is its own
/// page class in <c>Views/Settings</c>; this page only picks which one to show. Typing in the search box shows every matching setting from all sections.
/// </summary>
[NavigationPage(NavigationCatalog.SettingsTag, "Nav_Settings", Order = 900, Glyph = 0xE713)]
public sealed partial class SettingsPage : Page
{
    private SettingsSection? _shown;

    public SettingsPage()
    {
        InitializeComponent();
        TitleText.Text = Loc.Get("Nav_Settings");
        Search.PlaceholderText = Loc.Get("Settings_Search_Placeholder");
        AutomationProperties.SetName(Search, Loc.Get("Settings_Search_Placeholder"));
        AutomationProperties.SetName(Sections, Loc.Get("Nav_Settings"));
        foreach (var section in Enum.GetValues<SettingsSection>())
            Sections.Items.Add(new ListViewItem { Content = Loc.Get("Settings_Section_" + section), Tag = section });
        Sections.SelectedIndex = 0;
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedItem is ListViewItem { Tag: SettingsSection section }) ShowSection(section);
    }

    private void OnSectionClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ListViewItem { Tag: SettingsSection section }) ShowSection(section);
    }

    private void ShowSection(SettingsSection section)
    {
        if (_shown == section) return;
        _shown = section;
        if (Search.Text.Length > 0) Search.Text = "";
        Host.Content = section switch
        {
            SettingsSection.General => new SettingsGeneralPage(),
            SettingsSection.Sharing => new SettingsSharingPage(),
            SettingsSection.Connection => new SettingsConnectionPage(),
            SettingsSection.Security => new SettingsSecurityPage(),
            SettingsSection.Appearance => new SettingsAppearancePage(),
            SettingsSection.Advanced => new SettingsAdvancedPage(),
            SettingsSection.About => new SettingsAboutPage(),
            _ => new SettingsAddressBookPage(),
        };
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var query = sender.Text.Trim();
        if (query.Length == 0)
        {
            if (Sections.SelectedItem is ListViewItem { Tag: SettingsSection section }) ShowSection(section);
            return;
        }
        _shown = null;
        Host.Content = Results(query);
    }

    private UIElement Results(string query)
    {
        var body = new StackPanel { Padding = new Thickness(32, 24, 32, 24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 12 };
        var set = SettingsHub.Load();
        var policy = SettingsHub.Policy();
        var found = 0;

        foreach (var def in SettingsCatalog.All.Where(d => SettingsSearch.Matches(query, Loc.Get(d.LabelKey), Loc.Get(d.HelpKey), Loc.Get("Settings_Section_" + d.Section))))
        {
            body.Children.Add(SettingsUi.Secondary(Loc.Get("Settings_Section_" + def.Section)));
            body.Children.Add(SettingRows.Create(def, set, policy));
            found++;
        }
        foreach (var (titleKey, helpKey, section) in SectionView.SearchableBlocks.Where(b => SettingsSearch.Matches(query, Loc.Get(b.TitleKey), Loc.Get(b.HelpKey))))
        {
            var target = section;
            body.Children.Add(SettingsUi.CardOf(
                SettingsUi.Strong(Loc.Get(titleKey)),
                SettingsUi.Secondary(Loc.Get(helpKey)),
                SettingsUi.ButtonOf("Settings_Search_Open", (_, _) => OpenSection(target))));
            found++;
        }
        if (found == 0) body.Children.Add(SettingsUi.Secondary(SettingsUi.Format("Settings_Search_None", query)));
        return new ScrollViewer { Content = body };
    }

    private void OpenSection(SettingsSection section)
    {
        foreach (var item in Sections.Items.OfType<ListViewItem>())
            if (item.Tag is SettingsSection s && s == section) Sections.SelectedItem = item;
        ShowSection(section);
    }
}
