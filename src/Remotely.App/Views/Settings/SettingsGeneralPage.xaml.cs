using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The General section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsGeneralPage</c>.</summary>
public sealed partial class SettingsGeneralPage : Page
{
    public SettingsGeneralPage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.General);
    }
}
