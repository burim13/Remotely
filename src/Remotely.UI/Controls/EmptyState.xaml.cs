using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Remotely.UI.Controls;

/// <summary>What a list shows when it has nothing yet: an icon, one sentence saying so, one sentence on what to do, and the button that does it. Fades in (not when Windows animations are off).</summary>
public sealed partial class EmptyState : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata("Monitor", (d, _) => ((EmptyState)d).Refresh()));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("", (d, _) => ((EmptyState)d).Refresh()));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata("", (d, _) => ((EmptyState)d).Refresh()));
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(EmptyState), new PropertyMetadata(null, (d, _) => ((EmptyState)d).Refresh()));

    public EmptyState()
    {
        InitializeComponent();
        Refresh();
        Loaded += (_, _) => Motion.FadeIn(Root);
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    /// <summary>The button (or buttons) that fix the emptiness.</summary>
    public object? ActionContent { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    private void Refresh()
    {
        if (TitleText is null) return;
        Mark.Glyph = Glyph ?? "";
        TitleText.Text = Title ?? "";
        MessageText.Text = Message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        ActionHost.Content = ActionContent;
    }
}
