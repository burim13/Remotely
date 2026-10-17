using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The About section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsAboutPage</c>.</summary>
public sealed partial class SettingsAboutPage : Page
{
    public SettingsAboutPage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.About);
        Body.Children.Add(new Remotely.App.Update.UpdateSettingsSection());
    }
}
