using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Remotely.UI.Controls;

/// <summary>A small rounded label for a state ("Connected", "Offline"). The kind picks colours and an icon, so the state is readable without colour; the text always says it in words.</summary>
public sealed partial class StatusPill : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata("", (d, _) => ((StatusPill)d).Refresh()));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusPill), new PropertyMetadata(StatusKind.Neutral, (d, _) => ((StatusPill)d).Refresh()));

    public StatusPill()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    internal static string GlyphFor(StatusKind kind) => kind switch
    {
        StatusKind.Connected => "Success",
        StatusKind.Info => "Info",
        StatusKind.Warning => "Warning",
        StatusKind.Error => "Error",
        _ => "Clock",
    };

    private void Refresh()
    {
        if (Label is null) return;
        Label.Text = Text ?? "";
        Mark.Glyph = GlyphFor(Kind);
        VisualStateManager.GoToState(this, Kind.ToString(), false);
        AutomationProperties.SetName(this, Text ?? "");
    }
}
