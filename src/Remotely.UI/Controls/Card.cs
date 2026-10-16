using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Remotely.UI.Controls;

/// <summary>A panel with the Portico card fill, a 1 px stroke and rounded corners. Put any content inside. <see cref="Raised"/> uses the lighter fill for a card that sits on another card.</summary>
public sealed class Card : ContentControl
{
    public static readonly DependencyProperty RaisedProperty = DependencyProperty.Register(nameof(Raised), typeof(bool), typeof(Card), new PropertyMetadata(false, (d, _) => ((Card)d).Rebuild()));

    public Card()
    {
        IsTabStop = false;
        Padding = new Thickness(16);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Rebuild();
    }

    public bool Raised { get => (bool)GetValue(RaisedProperty); set => SetValue(RaisedProperty, value); }

    private void Rebuild()
    {
        var fill = Raised ? "Portico.CardRaised" : "Portico.Card";
        Template = (ControlTemplate)XamlReader.Load(
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"ContentControl\">" +
            $"<Border Background=\"{{ThemeResource {fill}}}\" BorderBrush=\"{{ThemeResource Portico.CardStroke}}\" BorderThickness=\"1\" CornerRadius=\"{{StaticResource Portico.Radius.Medium}}\" Padding=\"{{TemplateBinding Padding}}\">" +
            "<ContentPresenter Content=\"{TemplateBinding Content}\" ContentTemplate=\"{TemplateBinding ContentTemplate}\" HorizontalContentAlignment=\"{TemplateBinding HorizontalContentAlignment}\" VerticalContentAlignment=\"{TemplateBinding VerticalContentAlignment}\" />" +
            "</Border></ControlTemplate>");
    }
}
