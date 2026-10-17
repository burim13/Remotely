using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.ServerManager.Setup;
using Remotely.ServerManager.Views;

namespace Remotely.ServerManager;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = Ui.T("App_Name");
        if (!App.Args.Fake && App.Args.Show is null) Remotely.ServerManager.Update.ManagerUpdates.StartBackgroundCheck();   // does nothing unless the person turned update checks on
        PaneTitle.Text = Ui.T("App_Name");
        NavDashboard.Content = Ui.T("Nav_Dashboard");
        NavInvite.Content = Ui.T("Nav_Invite");
        NavDevices.Content = Ui.T("Nav_Devices");
        NavActivity.Content = Ui.T("Nav_Activity");
        NavService.Content = Ui.T("Nav_Service");
        NavSetup.Content = Ui.T("Setup_Nav");
        NavDiagnostics.Content = Ui.T("Diag_Help_Nav");
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, (string)item.Content);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 780));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "remotely.ico"));
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => App.OnWindowClosed();
        App.ApplyTheme(this);

        if (ElevatedHelper.IsElevated())
        {
            ElevatedBar.Title = Ui.T("Elevated_Title");
            ElevatedBar.Message = Ui.T("Elevated_Message");
            ElevatedBar.IsOpen = true;
        }

        if (App.Args.Show is { Length: > 0 } name
            && typeof(MainWindow).Assembly.GetTypes().FirstOrDefault(t => t.Name == name && typeof(Page).IsAssignableFrom(t)) is { } pageType)
        {
            var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == name);
            if (item is not null) Nav.SelectedItem = item;   // raises SelectionChanged, which navigates
            else Pages.Navigate(pageType, pageType == typeof(SetupWizardPage) ? SetupLifecycle.Dependencies : null);
        }
        else
        {
            Nav.SelectedItem = Nav.MenuItems[0];
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Exiting || !App.Settings.CloseToTray || App.Tray is null) return;
        args.Cancel = true;
        sender.Hide();
    }

    private void OnNavSelected(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(ServerSettingsPage)
            : (args.SelectedItemContainer?.Tag as string) switch
            {
                "InvitePage" => typeof(InvitePage),
                "DevicesPage" => typeof(DevicesPage),
                "ActivityPage" => typeof(ActivityPage),
                "ServicePage" => typeof(ServicePage),
                "SetupWizardPage" => typeof(SetupWizardPage),
                "DiagnosticsPage" => typeof(Views.Help.DiagnosticsPage),
                _ => typeof(DashboardPage),
            };
        if (Pages.CurrentSourcePageType != page) Pages.Navigate(page, page == typeof(SetupWizardPage) ? SetupLifecycle.Dependencies : null);
    }
}
