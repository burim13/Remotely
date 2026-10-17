using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

/// <summary>The AddressBook section of Settings. Shown inside <see cref="Remotely.App.SettingsPage"/>, and on its own by <c>--show SettingsAddressBookPage</c>.</summary>
public sealed partial class SettingsAddressBookPage : Page
{
    public SettingsAddressBookPage()
    {
        InitializeComponent();
        SectionView.Build(Body, SettingsSection.AddressBook);
    }
}
