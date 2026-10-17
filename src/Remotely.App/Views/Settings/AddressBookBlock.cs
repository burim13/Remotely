using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Remotely.Core.Storage;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Settings;

/// <summary>
/// The address book screen: saved PCs grouped by group name, favourites first, with a colour tag and notes; add, edit, remove, filter, import and export as JSON.
/// The list is <see cref="ViewerSettings.AddressBook"/>, the same one the Connect screen's recent PCs use.
/// </summary>
internal sealed class AddressBookBlock : UserControl
{
    private static readonly Dictionary<string, string> ColorBrushes = new()
    {
        ["red"] = "Portico.Error", ["amber"] = "Portico.Warning", ["green"] = "Portico.Connected", ["blue"] = "Portico.Info", ["gray"] = "Portico.Offline",
    };

    private readonly JsonAddressBook _book = ViewerSettings.AddressBook;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly TextBox _filter = new() { PlaceholderText = Loc.Get("Settings_AddressBook_Filter"), MinWidth = 220 };
    private readonly CheckBox _favoritesOnly = new() { Content = Loc.Get("Settings_AddressBook_FavoritesOnly") };
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _empty = SettingsUi.Secondary("");
    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = true };

    public AddressBookBlock()
    {
        AutomationProperties.SetName(_filter, Loc.Get("Settings_AddressBook_Filter"));
        _filter.TextChanged += (_, _) => Render();
        _favoritesOnly.Click += (_, _) => Render();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                SettingsUi.ButtonOf("Settings_AddressBook_Add", async (_, _) => await EditAsync(null)),
                SettingsUi.ButtonOf("Settings_AddressBook_Import", async (_, _) => await ImportAsync()),
                SettingsUi.ButtonOf("Settings_AddressBook_Export", async (_, _) => await ExportAsync()),
            },
        };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _filter, _favoritesOnly } };
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                SettingsUi.Title(Loc.Get("Settings_Section_AddressBook")),
                SettingsUi.Secondary(Loc.Get("Settings_Intro_AddressBook")),
                toolbar, filters, _bar, _empty, _list,
            },
        };
        Loaded += (_, _) => { _book.Changed += OnChanged; Render(); };
        Unloaded += (_, _) => _book.Changed -= OnChanged;
    }

    private void OnChanged() => _ui.TryEnqueue(Render);

    private void Render()
    {
        var items = _book.All
            .Where(e => (_favoritesOnly.IsChecked != true || e.Favorite) && SettingsSearch.Matches(_filter.Text, e.Name, e.Target, e.Group ?? "", e.Notes ?? ""))
            .ToList();
        _list.Children.Clear();
        _empty.Text = Loc.Get(_book.All.Count == 0 ? "Settings_AddressBook_Empty" : "Settings_AddressBook_NoMatch");
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var group in items.GroupBy(e => e.Group ?? "").OrderBy(g => g.Key.Length == 0 ? "￿" : g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Children.Add(SettingsUi.Strong(group.Key.Length == 0 ? Loc.Get("Settings_AddressBook_Ungrouped") : group.Key));
            foreach (var entry in group.OrderByDescending(e => e.Favorite).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                _list.Children.Add(Row(entry));
        }
    }

    private UIElement Row(AddressEntry entry)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(SettingsUi.Strong(entry.Name));
        text.Children.Add(SettingsUi.Mono(entry.Target));
        if (!string.IsNullOrEmpty(entry.Notes)) text.Children.Add(SettingsUi.Secondary(entry.Notes));
        var details = new List<string>();
        if (entry.ColorTag is { } tag) details.Add(Loc.Get("Settings_AddressBook_Color_" + tag));
        if (entry.LastUsedUtc is { } used) details.Add(SettingsUi.Format("Settings_AddressBook_LastUsed", used.ToLocalTime().ToString("g")));
        if (details.Count > 0) text.Children.Add(SettingsUi.Secondary(string.Join(" · ", details)));

        var star = new Button { Content = new FontIcon { Glyph = entry.Favorite ? "" : "", FontSize = 16 } };
        AutomationProperties.SetName(star, Loc.Get(entry.Favorite ? "Settings_AddressBook_Unfavorite" : "Settings_AddressBook_Favorite"));
        ToolTipService.SetToolTip(star, Loc.Get(entry.Favorite ? "Settings_AddressBook_Unfavorite" : "Settings_AddressBook_Favorite"));
        star.Click += (_, _) => Try(() => _book.Update(entry with { Favorite = !entry.Favorite }));
        var edit = SettingsUi.ButtonOf("Settings_AddressBook_Edit", async (_, _) => await EditAsync(entry));
        var remove = SettingsUi.ButtonOf("Settings_AddressBook_Remove", async (_, _) =>
        {
            if (await App.Services.ConfirmAsync(Loc.Get("Settings_AddressBook_RemoveTitle"), SettingsUi.Format("Settings_AddressBook_RemoveBody", entry.Name),
                    Loc.Get("Settings_AddressBook_Remove"), Loc.Get("Settings_Reset_Cancel")))
                _book.Remove(entry.Id);
        });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (entry.ColorTag is { } colorTag && ColorBrushes.TryGetValue(colorTag, out var brush))
            grid.Children.Add(Dot(brush));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { star, edit, remove } };
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return SettingsUi.CardOf(grid);
    }

    private static UIElement Dot(string brushKey) => (UIElement)XamlReader.Load(
        $"<Ellipse xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Width=\"12\" Height=\"12\" Fill=\"{{ThemeResource {brushKey}}}\" VerticalAlignment=\"Center\" />");

    private void Try(Action change)
    {
        try { change(); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException) { Say(InfoBarSeverity.Error, Loc.Get("Settings_AddressBook_Failed")); }
    }

    private void Say(InfoBarSeverity severity, string message)
    {
        _bar.Severity = severity;
        _bar.Message = message;
        _bar.IsOpen = true;
    }

    private async Task EditAsync(AddressEntry? existing)
    {
        var name = new TextBox { Header = Loc.Get("Settings_AddressBook_Name"), Text = existing?.Name ?? "", MaxLength = JsonAddressBook.MaxName };
        var target = new TextBox { Header = Loc.Get("Settings_AddressBook_Target"), Text = existing?.Target ?? "", MaxLength = JsonAddressBook.MaxTarget, PlaceholderText = Loc.Get("Settings_AddressBook_TargetHint") };
        var group = new TextBox { Header = Loc.Get("Settings_AddressBook_Group"), Text = existing?.Group ?? "", MaxLength = JsonAddressBook.MaxGroup };
        var groups = _book.Groups;
        var groupHint = SettingsUi.Secondary(groups.Count == 0 ? "" : SettingsUi.Format("Settings_AddressBook_GroupsInUse", string.Join(", ", groups)));
        var notes = new TextBox { Header = Loc.Get("Settings_AddressBook_Notes"), Text = existing?.Notes ?? "", MaxLength = JsonAddressBook.MaxNotes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
        var color = new ComboBox { Header = Loc.Get("Settings_AddressBook_ColorHeader"), MinWidth = 200 };
        color.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings_AddressBook_Color_none"), Tag = "" });
        foreach (var c in AddressBookTags.Names) color.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings_AddressBook_Color_" + c), Tag = c });
        color.SelectedIndex = Math.Max(0, Array.IndexOf(AddressBookTags.Names, existing?.ColorTag) + 1);
        var favorite = new CheckBox { Content = Loc.Get("Settings_AddressBook_Favorite"), IsChecked = existing?.Favorite == true };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var form = new StackPanel { Spacing = 10, MinWidth = 380 };
        foreach (var child in new UIElement[] { name, target, group, groupHint, notes, color, favorite, problem }) form.Children.Add(child);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get(existing is null ? "Settings_AddressBook_AddTitle" : "Settings_AddressBook_EditTitle"),
            Content = new ScrollViewer { Content = form, MaxHeight = 520 },
            PrimaryButtonText = Loc.Get("Settings_AddressBook_Save"),
            CloseButtonText = Loc.Get("Settings_Reset_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (App.Main?.Content is FrameworkElement root) dialog.RequestedTheme = root.RequestedTheme;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var address = target.Text.Trim();
            if (address.Length == 0) { Fail(Loc.Get("Settings_AddressBook_NeedTarget")); return; }
            var other = _book.All.FirstOrDefault(e => e.Id != existing?.Id && string.Equals(e.Target, address, StringComparison.OrdinalIgnoreCase));
            if (other is not null) { Fail(SettingsUi.Format("Settings_AddressBook_Duplicate", other.Name)); return; }
            var tag = (color.SelectedItem as ComboBoxItem)?.Tag as string;
            try
            {
                var entry = existing ?? _book.Add(name.Text, address);
                _book.Update(entry with
                {
                    Name = name.Text.Trim().Length == 0 ? address : name.Text.Trim(),
                    Target = address,
                    Group = group.Text,
                    Notes = notes.Text,
                    Favorite = favorite.IsChecked == true,
                    ColorTag = string.IsNullOrEmpty(tag) ? null : tag,
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException) { Fail(Loc.Get("Settings_AddressBook_Failed")); }

            void Fail(string message)
            {
                args.Cancel = true;
                problem.Text = message;
                problem.Visibility = Visibility.Visible;
            }
        };
        await dialog.ShowAsync();
    }

    private async Task ExportAsync()
    {
        if (await FileDialogs.PickSavePathAsync("remotely-address-book") is not { } path) return;
        try
        {
            File.WriteAllText(path, _book.Export());
            Say(InfoBarSeverity.Success, SettingsUi.Format("Settings_Export_Done", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(InfoBarSeverity.Error, Loc.Get("Settings_File_WriteFailed")); }
    }

    private async Task ImportAsync()
    {
        if (await App.Services.PickFileAsync() is not { Length: > 0 } path) return;
        if (FileDialogs.ReadSmallText(path) is not { } text) { Say(InfoBarSeverity.Error, Loc.Get("Settings_File_ReadFailed")); return; }
        try
        {
            var result = _book.ImportWithResult(text);
            Say(InfoBarSeverity.Success, SettingsUi.Format("Settings_AddressBook_ImportDone", result.Added, result.Updated, result.Skipped));
        }
        catch (FormatException) { Say(InfoBarSeverity.Error, Loc.Get("Settings_AddressBook_ImportBad")); }
    }
}
