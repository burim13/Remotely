using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Remotely.UI.Controls;

/// <summary>One PC in a list: icon, name, a line of detail (address or id) and its state. The whole tile is a button; screen readers hear the name, detail and state together.</summary>
public sealed partial class DeviceTile : UserControl
{
    public static readonly DependencyProperty DeviceNameProperty = DependencyProperty.Register(nameof(DeviceName), typeof(string), typeof(DeviceTile), new PropertyMetadata("", (d, _) => ((DeviceTile)d).Refresh()));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(DeviceTile), new PropertyMetadata("", (d, _) => ((DeviceTile)d).Refresh()));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(DeviceTile), new PropertyMetadata("Monitor", (d, _) => ((DeviceTile)d).Refresh()));
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(DeviceTile), new PropertyMetadata("", (d, _) => ((DeviceTile)d).Refresh()));
    public static readonly DependencyProperty StatusKindProperty = DependencyProperty.Register(nameof(StatusKind), typeof(StatusKind), typeof(DeviceTile), new PropertyMetadata(StatusKind.Neutral, (d, _) => ((DeviceTile)d).Refresh()));

    public DeviceTile()
    {
        InitializeComponent();
        Refresh();
    }

    public event RoutedEventHandler? Click;

    public string DeviceName { get => (string)GetValue(DeviceNameProperty); set => SetValue(DeviceNameProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string StatusText { get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }
    public StatusKind StatusKind { get => (StatusKind)GetValue(StatusKindProperty); set => SetValue(StatusKindProperty, value); }

    private void OnTileClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);

    private void Refresh()
    {
        if (NameText is null) return;
        NameText.Text = DeviceName ?? "";
        DetailText.Text = Detail ?? "";
        DetailText.Visibility = string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
        Mark.Glyph = Glyph ?? "";
        Pill.Text = StatusText ?? "";
        Pill.Kind = StatusKind;
        Pill.Visibility = string.IsNullOrEmpty(StatusText) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Tile, string.Join(", ", new[] { DeviceName, Detail, StatusText }.Where(s => !string.IsNullOrEmpty(s))));
    }
}
