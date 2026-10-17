using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Lan.Consent;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Share;

/// <summary>
/// The whole "Share this PC" screen: the <see cref="ShareCard"/>, the list of remembered PCs (with Remove) and the session history. A page that wants only the card (the start page)
/// can host <see cref="ShareCard"/> alone.
/// </summary>
public sealed partial class SharePage : Page
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    public SharePage()
    {
        InitializeComponent();
        RememberedTitle.Text = Loc.Get("Remembered_Title");
        RememberedHelp.Text = Loc.Get("Remembered_Help");
        RememberedEmpty.Text = Loc.Get("Remembered_Empty");
        RemoveAllButton.Content = Loc.Get("Remembered_RemoveAll");
        HistoryTitle.Text = Loc.Get("History_Title");
        HistoryEmpty.Text = Loc.Get("History_Empty");
        ClearHistoryButton.Content = Loc.Get("History_Clear");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Share.Coordinator.Remembered.Changed += OnRememberedChanged;
        App.Share.Coordinator.History.Changed += OnHistoryChanged;
        RenderRemembered();
        RenderHistory();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Share.Coordinator.Remembered.Changed -= OnRememberedChanged;
        App.Share.Coordinator.History.Changed -= OnHistoryChanged;
    }

    private void OnRememberedChanged() => _ui.TryEnqueue(RenderRemembered);
    private void OnHistoryChanged() => _ui.TryEnqueue(RenderHistory);

    private void RenderRemembered()
    {
        var items = App.Share.Coordinator.Remembered.All;
        RememberedList.Children.Clear();
        RememberedEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveAllButton.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var device in items) RememberedList.Children.Add(Row(device));
    }

    private UIElement Row(RememberedDevice device)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = device.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var seen = device.LastSeenUtc is { } last ? ShareText.Format("Remembered_LastSeen", ShareText.When(last)) : Loc.Get("Remembered_NeverSeen");
        text.Children.Add(new TextBlock { Text = seen + "  " + Remotely.Core.Crypto.DeviceIds.Short(device.DeviceId), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        var remove = new Button { Content = Loc.Get("Remembered_Remove"), Tag = device.DeviceId, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, ShareText.Format("Remembered_RemoveName", device.Name));
        remove.Click += OnRemove;
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        return grid;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) App.Share.Coordinator.Remembered.Remove(id);
    }

    private void OnRemoveAll(object sender, RoutedEventArgs e) => App.Share.Coordinator.Remembered.RemoveAll();

    private void RenderHistory()
    {
        var items = App.Share.Coordinator.History.All;
        HistoryList.Children.Clear();
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var record in items.Take(100)) HistoryList.Children.Add(HistoryRow(record));
    }

    private static UIElement HistoryRow(SessionRecord r)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = ShareText.Format("History_Who", r.ControllerName, r.Address), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = ShareText.When(r.StartedUtc) + "  " + ShareText.Outcome(r.Outcome) + "  " + ShareText.Via(r.Via);
        if (r.EndedUtc is { } ended && r.EndReason is not null)
            detail += "  " + ShareText.Duration(ended - r.StartedUtc) + "  " + ShareText.Reason(r.EndReason);
        panel.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private void OnClearHistory(object sender, RoutedEventArgs e) => App.Share.Coordinator.History.Clear();
}
