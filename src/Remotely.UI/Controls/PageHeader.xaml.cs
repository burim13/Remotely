using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Remotely.UI.Controls;

/// <summary>The top of a page: optional icon, the page title (a level-1 heading for screen readers), an optional line under it and a slot for page-level buttons on the right.</summary>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null, (d, _) => ((PageHeader)d).Refresh()));

    public PageHeader()
    {
        InitializeComponent();
        Refresh();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    /// <summary>An icon key from <see cref="Icons"/>; empty for none.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    /// <summary>Buttons shown at the right edge.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    private void Refresh()
    {
        if (TitleText is null) return;
        TitleText.Text = Title ?? "";
        SubtitleText.Text = Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        IconView.Glyph = Glyph ?? "";
        IconView.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        ActionsHost.Content = Actions;
    }
}
