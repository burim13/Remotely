using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The Security section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsSecurityPage</c>.</summary>
public sealed partial class SettingsSecurityPage : Page
{
    public SettingsSecurityPage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.Security);
    }
}
