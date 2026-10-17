using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The Connection section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsConnectionPage</c>.</summary>
public sealed partial class SettingsConnectionPage : Page
{
    public SettingsConnectionPage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.Connection);
    }
}
