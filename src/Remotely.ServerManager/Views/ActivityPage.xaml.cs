using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Admin;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager.Views;

public sealed partial class ActivityPage : Page
{
    private const int MaxRows = 500;

    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = false };
    private readonly TextBox _search = new();
    private readonly ComboBox _kind = new() { MinWidth = 150 };
    private readonly ComboBox _result = new() { MinWidth = 150 };
    private readonly ComboBox _period = new() { MinWidth = 150 };
    private readonly TextBlock _summary = Ui.Secondary("");
    private readonly StackPanel _list = new() { Spacing = 4 };
    private IReadOnlyList<AuditEntry> _entries = [];
    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();
    private IReadOnlyList<AuditEntry> _shown = [];
    private bool _loading;

    public ActivityPage()
    {
        InitializeComponent();

        _search.PlaceholderText = Ui.T("Activity_Search");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_search, Ui.T("Activity_Search"));
        _search.TextChanged += (_, _) => Render();
        FillValues(_kind, Ui.T("Activity_AllKinds"), "Kind_", [], "");
        FillValues(_result, Ui.T("Activity_AllResults"), "Result_", [], "");
        foreach (var (label, hours) in new[] { (Ui.T("Activity_Hour"), 1), (Ui.T("Activity_Day"), 24), (Ui.T("Activity_Week"), 168), (Ui.T("Activity_Anytime"), 0) })
            _period.Items.Add(new ComboBoxItem { Content = label, Tag = hours });
        _period.SelectedIndex = 3;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_kind, Ui.T("Activity_Kind"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_result, Ui.T("Activity_Result"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_period, Ui.T("Activity_Period"));
        _kind.SelectionChanged += (_, _) => { if (!_loading) Render(); };
        _result.SelectionChanged += (_, _) => { if (!_loading) Render(); };
        _period.SelectionChanged += (_, _) => { if (!_loading) Render(); };

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filters.Children.Add(_kind);
        filters.Children.Add(_result);
        filters.Children.Add(_period);
        filters.Children.Add(Ui.SmallButton(Ui.T("Common_Refresh"), async (_, _) => await LoadAsync()));
        filters.Children.Add(Ui.SmallButton(Ui.T("Activity_Export"), async (_, _) => await ExportAsync()));

        Root.Children.Add(Ui.Text(Ui.T("Nav_Activity"), "TitleTextBlockStyle"));
        Root.Children.Add(_bar);
        Root.Children.Add(_search);
        Root.Children.Add(filters);
        Root.Children.Add(_summary);
        Root.Children.Add(Ui.Card(_list));

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<AuditEntry> entries = [];
        IReadOnlyList<AdminDevice> devices = [];
        if (!await Ui.GuardAsync(async () =>
            {
                entries = await App.Admin.GetAuditAsync(null, MaxRows);
                devices = await App.Admin.ListDevicesAsync();
            }, _bar)) return;
        _entries = entries;
        _names = devices.ToDictionary(d => d.DeviceId, d => d.Name);

        _loading = true;
        var kind = (_kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var result = (_result.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        FillValues(_kind, Ui.T("Activity_AllKinds"), "Kind_", AuditView.DistinctKinds(entries), kind);
        FillValues(_result, Ui.T("Activity_AllResults"), "Result_", AuditView.DistinctResults(entries), result);
        _loading = false;
        Render();
    }

    private static void FillValues(ComboBox box, string allLabel, string prefix, IReadOnlyList<string> values, string selected)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = allLabel, Tag = "" });
        foreach (var value in values) box.Items.Add(new ComboBoxItem { Content = Ui.Label(prefix, value), Tag = value });
        box.SelectedIndex = Math.Max(0, values.ToList().FindIndex(v => v == selected) + 1);
    }

    private AuditFilter CurrentFilter()
    {
        var hours = (_period.SelectedItem as ComboBoxItem)?.Tag is int h ? h : 0;
        var kind = (_kind.SelectedItem as ComboBoxItem)?.Tag as string;
        var result = (_result.SelectedItem as ComboBoxItem)?.Tag as string;
        return new AuditFilter(_search.Text, kind, result, null, hours > 0 ? DateTimeOffset.UtcNow.AddHours(-hours) : null);
    }

    private void Render()
    {
        _shown = AuditView.Apply(_entries, CurrentFilter(), _names);
        _summary.Text = Ui.T("Activity_Summary", _shown.Count, _entries.Count);
        _list.Children.Clear();
        foreach (var entry in _shown.Take(MaxRows)) _list.Children.Add(Row(entry));
        if (_shown.Count == 0) _list.Children.Add(Ui.Secondary(_entries.Count == 0 ? Ui.T("Activity_None") : Ui.T("Activity_NoMatch")));
    }

    private UIElement Row(AuditEntry entry)
    {
        string NameOf(string id) => _names.TryGetValue(id, out var n) ? n : DeviceView.ShortId(id);
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

        grid.Children.Add(Ui.Secondary(Ui.When(entry.TimeUtc)));
        var kind = Ui.Text(Ui.Label("Kind_", entry.Kind), semiBold: true);
        Grid.SetColumn(kind, 1);
        grid.Children.Add(kind);

        var who = entry.DeviceId.Length == 0 ? "" : NameOf(entry.DeviceId);
        if (entry.PeerDeviceId is { Length: > 0 } peer) who += "  →  " + NameOf(peer);
        if (!string.IsNullOrEmpty(entry.Detail)) who += (who.Length > 0 ? "  ·  " : "") + entry.Detail;
        var detail = Ui.Text(who);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);

        var result = Ui.Text(Ui.Label("Result_", entry.Result));
        Grid.SetColumn(result, 3);
        grid.Children.Add(result);
        return grid;
    }

    private async Task ExportAsync()
    {
        if (_shown.Count == 0) { Ui.Show(_bar, InfoBarSeverity.Warning, Ui.T("Activity_NothingToExport")); return; }
        try
        {
            var path = await Ui.PickSaveFileAsync("remotely-activity-" + DateTime.Now.ToString("yyyyMMdd-HHmm"), "CSV", ".csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, AuditCsv.Write(_shown, _names), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Activity_Exported", _shown.Count, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, Ui.Describe(ex));
        }
    }
}
