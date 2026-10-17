using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The Advanced section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsAdvancedPage</c>.</summary>
public sealed partial class SettingsAdvancedPage : Page
{
    public SettingsAdvancedPage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.Advanced);
    }
}
