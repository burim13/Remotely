using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Admin;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager.Views;

public sealed partial class DevicesPage : Page
{
    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = false };
    private readonly TextBox _search = new();
    private readonly ComboBox _state = new() { MinWidth = 150 };
    private readonly TextBlock _summary = Ui.Secondary("");
    private readonly StackPanel _list = new() { Spacing = 8 };
    private IReadOnlyList<AdminDevice> _devices = [];

    public DevicesPage()
    {
        InitializeComponent();

        _search.PlaceholderText = Ui.T("Devices_Search");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_search, Ui.T("Devices_Search"));
        _search.TextChanged += (_, _) => Render();
        foreach (var (label, value) in new[]
        {
            (Ui.T("Devices_All"), DeviceStateFilter.All), (Ui.T("Devices_OnlineOnly"), DeviceStateFilter.Online),
            (Ui.T("Devices_OfflineOnly"), DeviceStateFilter.Offline), (Ui.T("Devices_BlockedOnly"), DeviceStateFilter.Blocked),
        })
            _state.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _state.SelectedIndex = 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_state, Ui.T("Devices_State"));
        _state.SelectionChanged += (_, _) => Render();

        var tools = new Grid { ColumnSpacing = 8 };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.Children.Add(_search);
        Grid.SetColumn(_state, 1);
        tools.Children.Add(_state);
        var refresh = Ui.SmallButton(Ui.T("Common_Refresh"), async (_, _) => await LoadAsync());
        Grid.SetColumn(refresh, 2);
        tools.Children.Add(refresh);

        Root.Children.Add(Ui.Text(Ui.T("Nav_Devices"), "TitleTextBlockStyle"));
        Root.Children.Add(_bar);
        Root.Children.Add(tools);
        Root.Children.Add(_summary);
        Root.Children.Add(_list);

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<AdminDevice> devices = [];
        if (!await Ui.GuardAsync(async () => devices = await App.Admin.ListDevicesAsync(), _bar)) return;
        _devices = devices;
        Render();
    }

    private void Render()
    {
        var state = _state.SelectedItem is ComboBoxItem { Tag: DeviceStateFilter f } ? f : DeviceStateFilter.All;
        var shown = DeviceView.Filter(_devices, _search.Text, state);
        _summary.Text = Ui.T("Devices_Summary", shown.Count, _devices.Count);
        _list.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var device in shown) _list.Children.Add(Row(device, now));
        if (_devices.Count == 0) _list.Children.Add(Ui.Card(Ui.Secondary(Ui.T("Devices_None"))));
        else if (shown.Count == 0) _list.Children.Add(Ui.Card(Ui.Secondary(Ui.T("Devices_NoMatch"))));
    }

    private UIElement Row(AdminDevice device, DateTimeOffset now)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dotStyle = device.Blocked ? "DotBadStyle" : device.Online ? "DotGoodStyle" : "DotNeutralStyle";
        grid.Children.Add(new Border { Child = Ui.Dot(dotStyle), VerticalAlignment = VerticalAlignment.Center });

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(Ui.Text(device.Name, semiBold: true));
        var status = device.Blocked ? Ui.T("Devices_Blocked") : Ui.LastSeenText(DeviceView.DescribeLastSeen(device, now));
        var role = device.CanHost ? Ui.T("Devices_CanHost") : Ui.T("Devices_ViewerOnly");
        info.Children.Add(Ui.Secondary($"{status}  ·  {role}  ·  {Ui.T("Devices_Paired", Ui.When(device.PairedUtc))}"));
        info.Children.Add(Ui.Text(Ui.T("Devices_Id", DeviceView.ShortId(device.DeviceId)), "MonoTextStyle", 12));
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Ui.SmallButton(Ui.T("Devices_History"), async (_, _) => await ShowHistoryAsync(device)));
        actions.Children.Add(Ui.SmallButton(Ui.T("Devices_Rename"), async (_, _) => await RenameAsync(device)));
        actions.Children.Add(Ui.SmallButton(device.Blocked ? Ui.T("Devices_Unblock") : Ui.T("Devices_Block"), async (_, _) => await BlockAsync(device)));
        actions.Children.Add(Ui.SmallButton(Ui.T("Devices_Revoke"), async (_, _) => await RevokeAsync(device)));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return Ui.Card(grid);
    }

    private async Task RenameAsync(AdminDevice device)
    {
        var box = new TextBox { Text = device.Name, SelectionStart = device.Name.Length, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Ui.T("Devices_RenameTitle"),
            Content = box,
            PrimaryButtonText = Ui.T("Common_Save"),
            CloseButtonText = Ui.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0) { Ui.Show(_bar, InfoBarSeverity.Error, Ui.T("Devices_NameEmpty")); return; }
        if (await Ui.GuardAsync(() => App.Admin.RenameDeviceAsync(device.DeviceId, name), _bar)) await LoadAsync();
    }

    private async Task BlockAsync(AdminDevice device)
    {
        var block = !device.Blocked;
        if (block && !await Ui.ConfirmAsync(XamlRoot, Ui.T("Devices_BlockTitle", device.Name), Ui.T("Devices_BlockBody"), Ui.T("Devices_Block"))) return;
        if (await Ui.GuardAsync(() => App.Admin.SetDeviceBlockedAsync(device.DeviceId, block), _bar)) await LoadAsync();
    }

    private async Task RevokeAsync(AdminDevice device)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, Ui.T("Devices_RevokeTitle", device.Name), Ui.T("Devices_RevokeBody"), Ui.T("Devices_Revoke"))) return;
        if (await Ui.GuardAsync(() => App.Admin.RevokeDeviceAsync(device.DeviceId), _bar)) await LoadAsync();
    }

    private async Task ShowHistoryAsync(AdminDevice device)
    {
        IReadOnlyList<AuditEntry> entries = [];
        if (!await Ui.GuardAsync(async () => entries = await App.Admin.GetAuditAsync(null, 500), _bar)) return;
        var names = _devices.ToDictionary(d => d.DeviceId, d => d.Name);
        var mine = AuditView.Apply(entries, new AuditFilter(DeviceId: device.DeviceId), names);
        var list = new StackPanel { Spacing = 6, MinWidth = 460 };
        foreach (var e in mine.Take(100))
        {
            var peer = e.PeerDeviceId is { } p ? "  →  " + (names.TryGetValue(p, out var pn) ? pn : DeviceView.ShortId(p)) : "";
            list.Children.Add(Ui.Text($"{Ui.When(e.TimeUtc)}   {Ui.Label("Kind_", e.Kind)}{peer}   {Ui.Label("Result_", e.Result)}", size: 13));
        }
        if (mine.Count == 0) list.Children.Add(Ui.Secondary(Ui.T("Devices_NoHistory")));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Ui.T("Devices_HistoryTitle", device.Name),
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
            CloseButtonText = Ui.T("Common_Close"),
        };
        await dialog.ShowAsync();
    }
}
