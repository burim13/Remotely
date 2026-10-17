using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The Appearance section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsAppearancePage</c>.</summary>
public sealed partial class SettingsAppearancePage : Page
{
    public SettingsAppearancePage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.Appearance);
    }
}
