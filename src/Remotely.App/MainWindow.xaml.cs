using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Remotely.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Remotely";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "remotely.ico"));
        Closed += async (_, _) => await App.Share.DisposeAsync();
        App.ApplyTheme(this);
        Nav.SelectedItem = Nav.MenuItems[0];   // raises SelectionChanged, which shows the first page
        _ = HandleStartOptionsAsync();
    }

    /// <summary>Shows a page by name: "home", "computers" or "settings".</summary>
    internal void Show(string page)
    {
        if (page == "settings") Nav.SelectedItem = Nav.SettingsItem;
        else Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == page);
    }

    private void OnNavSelected(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(SettingsPage)
            : (args.SelectedItemContainer?.Tag as string) == "computers" ? typeof(ComputersPage) : typeof(HomePage);
        if (Pages.CurrentSourcePageType != page) Pages.Navigate(page);
    }

    /// <summary>--invite and --pair: set up the server link at start-up (the installer uses this), then show the computer list.</summary>
    private async Task HandleStartOptionsAsync()
    {
        if (App.InviteOnStart is { Length: > 0 } invite)
        {
            await Join.WithInviteAsync(invite);
            Show("computers");
        }
        else if (App.PairOnStart is { Length: > 0 } code && App.Settings.IsConfigured)
        {
            await Join.PairAsync(code);
            Show("computers");
        }
    }
}
