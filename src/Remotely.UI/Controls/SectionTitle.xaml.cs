using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Remotely.UI.Controls;

/// <summary>A heading for a group of settings or cards (level 2), with an optional explanation under it.</summary>
public sealed partial class SectionTitle : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SectionTitle), new PropertyMetadata("", (d, _) => ((SectionTitle)d).Refresh()));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SectionTitle), new PropertyMetadata("", (d, _) => ((SectionTitle)d).Refresh()));

    public SectionTitle()
    {
        InitializeComponent();
        Refresh();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    private void Refresh()
    {
        if (TitleText is null) return;
        TitleText.Text = Text ?? "";
        DescriptionText.Text = Description ?? "";
        DescriptionText.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
    }
}
