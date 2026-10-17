using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Lan;
using Remotely.UI;
using Remotely.UI.Controls;

namespace Remotely.App.Views.Gallery;

public sealed partial class GalleryPanel : UserControl
{
    public GalleryPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => ThemeName.Text = $"{ActualTheme} theme";
        foreach (var token in new[] { EmptyToken, FilledToken, InvalidToken }) token.Alphabet = LanToken.Alphabet;
        FilledToken.Value = "K7P44NX2QMH9";
        InvalidToken.Value = "K7P4-4NX";
        var icons = new List<UIElement>();
        foreach (var name in Icons.Names)
        {
            var cell = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new PorticoIcon { Glyph = name, Size = 24, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["Portico.Text.Caption"], TextAlignment = TextAlignment.Center });
            icons.Add(cell);
        }
        IconGrid.ItemsSource = icons;
    }
}
