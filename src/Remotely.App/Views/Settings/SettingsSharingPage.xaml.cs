using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The Sharing section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsSharingPage</c>.</summary>
public sealed partial class SettingsSharingPage : Page
{
    public SettingsSharingPage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.Sharing);
    }
}
