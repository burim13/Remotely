using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Localization;

namespace Remotely.UI.Controls;

/// <summary>A message that stays in the page next to what it is about (not a dialog). The icon and title carry the severity as well as the colour; errors are announced by screen readers at once, other kinds politely.</summary>
public sealed partial class InlineBanner : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(InlineBanner), new PropertyMetadata(StatusKind.Info, (d, _) => ((InlineBanner)d).Refresh()));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(InlineBanner), new PropertyMetadata("", (d, _) => ((InlineBanner)d).Refresh()));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(InlineBanner), new PropertyMetadata("", (d, _) => ((InlineBanner)d).Refresh()));
    public static readonly DependencyProperty IsDismissibleProperty = DependencyProperty.Register(nameof(IsDismissible), typeof(bool), typeof(InlineBanner), new PropertyMetadata(false, (d, _) => ((InlineBanner)d).Refresh()));
    public static readonly DependencyProperty DismissLabelProperty = DependencyProperty.Register(nameof(DismissLabel), typeof(string), typeof(InlineBanner), new PropertyMetadata("", (d, _) => ((InlineBanner)d).Refresh()));
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(InlineBanner), new PropertyMetadata(null, (d, _) => ((InlineBanner)d).Refresh()));

    public InlineBanner()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised when the person closes the banner. The banner does not hide itself; the page decides.</summary>
    public event RoutedEventHandler? Dismissed;

    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool IsDismissible { get => (bool)GetValue(IsDismissibleProperty); set => SetValue(IsDismissibleProperty, value); }
    /// <summary>Accessible name of the close button; when empty the string <c>Banner_Dismiss</c> is looked up in the app's resources.</summary>
    public string DismissLabel { get => (string)GetValue(DismissLabelProperty); set => SetValue(DismissLabelProperty, value); }
    /// <summary>A button to the right of the text, for the one thing the person can do about it.</summary>
    public object? ActionContent { get => GetValue(ActionContentProperty); set => SetValue(ActionContentProperty, value); }

    private void OnDismiss(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, e);

    private void Refresh()
    {
        if (TitleText is null) return;
        Mark.Glyph = StatusPill.GlyphFor(Kind);
        TitleText.Text = Title ?? "";
        TitleText.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = Message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        ActionHost.Content = ActionContent;
        DismissButton.Visibility = IsDismissible ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(DismissButton, string.IsNullOrEmpty(DismissLabel) ? Loc.Get("Banner_Dismiss") : DismissLabel);
        AutomationProperties.SetLiveSetting(Root, Kind == StatusKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        VisualStateManager.GoToState(this, Kind.ToString(), false);
    }
}
