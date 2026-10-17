using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Admin;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager.Views;

public sealed partial class ServerSettingsPage : Page
{
    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = true };
    private readonly TextBox _address = new();
    private readonly NumberBox _port = new() { Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
    private readonly NumberBox _maxDevices = new() { Minimum = 1, Maximum = 100000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _maxSessions = new() { Minimum = 1, Maximum = 1000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ToggleSwitch _tls = new();
    private readonly Button _save;
    private readonly ToggleSwitch _autoStart = new();
    private readonly ToggleSwitch _closeToTray = new();
    private readonly ToggleSwitch _notify = new();
    private readonly ComboBox _theme = new() { MinWidth = 200 };
    private bool _loading = true;

    public ServerSettingsPage()
    {
        InitializeComponent();

        _address.Header = Ui.T("Settings_Address");
        _port.Header = Ui.T("Settings_Port");
        _maxDevices.Header = Ui.T("Settings_MaxDevices");
        _maxSessions.Header = Ui.T("Settings_MaxSessions");
        _tls.Header = Ui.T("Settings_Tls");
        _tls.OnContent = Ui.T("Common_On");
        _tls.OffContent = Ui.T("Common_Off");
        Name(_address, "Settings_Address");
        Name(_port, "Settings_Port");
        Name(_maxDevices, "Settings_MaxDevices");
        Name(_maxSessions, "Settings_MaxSessions");
        Name(_tls, "Settings_Tls");
        _save = Ui.SmallButton(Ui.T("Common_Save"), async (_, _) => await SaveServerAsync(), accent: true);
        _save.IsEnabled = false;

        var server = new StackPanel { Spacing = 12 };
        server.Children.Add(Ui.Text(Ui.T("Settings_ServerTitle"), "BodyStrongTextBlockStyle"));
        server.Children.Add(Ui.Secondary(Ui.T("Settings_ServerHelp")));
        server.Children.Add(_address);
        server.Children.Add(_port);
        server.Children.Add(_maxDevices);
        server.Children.Add(_maxSessions);
        server.Children.Add(_tls);
        server.Children.Add(_save);

        _autoStart.Header = Ui.T("Settings_AutoStart");
        _closeToTray.Header = Ui.T("Settings_CloseToTray");
        _notify.Header = Ui.T("Settings_Notify");
        foreach (var toggle in new[] { _autoStart, _closeToTray, _notify })
        {
            toggle.OnContent = Ui.T("Common_On");
            toggle.OffContent = Ui.T("Common_Off");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, (string)toggle.Header);
            toggle.Toggled += OnManagerSettingChanged;
        }
        _theme.Header = Ui.T("Settings_Theme");
        foreach (var (label, value) in new[] { (Ui.T("Settings_ThemeSystem"), "system"), (Ui.T("Settings_ThemeDark"), "dark"), (Ui.T("Settings_ThemeLight"), "light") })
            _theme.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _theme.SelectionChanged += (_, _) => OnManagerSettingChanged(this, new RoutedEventArgs());

        var manager = new StackPanel { Spacing = 12 };
        manager.Children.Add(Ui.Text(Ui.T("Settings_ManagerTitle"), "BodyStrongTextBlockStyle"));
        manager.Children.Add(_autoStart);
        manager.Children.Add(_closeToTray);
        manager.Children.Add(_notify);
        manager.Children.Add(_theme);

        var backupButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        backupButtons.Children.Add(Ui.SmallButton(Ui.T("Backup_Create"), async (_, _) => await BackupAsync(), accent: true));
        backupButtons.Children.Add(Ui.SmallButton(Ui.T("Backup_RestoreFile"), async (_, _) => await RestoreAsync(folder: false)));
        backupButtons.Children.Add(Ui.SmallButton(Ui.T("Backup_RestoreFolder"), async (_, _) => await RestoreAsync(folder: true)));
        var backup = new StackPanel { Spacing = 12 };
        backup.Children.Add(Ui.Text(Ui.T("Backup_Title"), "BodyStrongTextBlockStyle"));
        backup.Children.Add(Ui.Secondary(Ui.T("Backup_Help")));
        backup.Children.Add(backupButtons);
        backup.Children.Add(Ui.Secondary(Ui.T("Backup_RestoreHelp")));

        var about = Ui.Secondary(Ui.T("Settings_About", typeof(ServerSettingsPage).Assembly.GetName().Version?.ToString(3) ?? ""));

        Root.Children.Add(Ui.Text(Ui.T("Nav_Settings"), "TitleTextBlockStyle"));
        Root.Children.Add(_bar);
        Root.Children.Add(Ui.Card(server));
        Root.Children.Add(Ui.Card(manager));
        Root.Children.Add(Ui.Card(backup));
        Root.Children.Add(about);

        Loaded += async (_, _) => await LoadAsync();
    }

    private static void Name(DependencyObject element, string key) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, Ui.T(key));

    private async Task LoadAsync()
    {
        _loading = true;
        var s = App.Settings;
        _closeToTray.IsOn = s.CloseToTray;
        _notify.IsOn = s.NotifyNewDevices;
        try { _autoStart.IsOn = AutoStart.IsEnabled(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { _autoStart.IsOn = s.StartWithWindows; }
        _theme.SelectedItem = _theme.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Theme) ?? _theme.Items[0];
        _loading = false;

        ServerSettings? server = null;
        if (!await Ui.GuardAsync(async () => server = await App.Admin.GetSettingsAsync(), _bar) || server is null)
        {
            _save.IsEnabled = false;
            return;
        }
        _address.Text = server.Address;
        _port.Value = server.Port;
        _maxDevices.Value = server.MaxDevices;
        _maxSessions.Value = server.MaxSessionsPerDevice;
        _tls.IsOn = server.RequireTls;
        _save.IsEnabled = true;
    }

    private async Task SaveServerAsync()
    {
        var settings = new ServerSettings(_address.Text.Trim(), ToInt(_port.Value), ToInt(_maxDevices.Value), ToInt(_maxSessions.Value), _tls.IsOn);
        if (SettingsValidator.Validate(settings).FirstOrDefault() is { } problem)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, Ui.T(problem.MessageKey));
            return;
        }
        if (await Ui.GuardAsync(() => App.Admin.SetSettingsAsync(settings), _bar))
            Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Settings_Saved"));
    }

    private static int ToInt(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0 : (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    private void OnManagerSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        s.CloseToTray = _closeToTray.IsOn;
        s.NotifyNewDevices = _notify.IsOn;
        s.StartWithWindows = _autoStart.IsOn;
        if (_theme.SelectedItem is ComboBoxItem { Tag: string theme }) s.Theme = theme;
        try
        {
            AutoStart.Set(s.StartWithWindows);
            s.Save();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, Ui.Describe(ex));
        }
        if (App.Main is { } main) App.ApplyTheme(main);
    }

    private async Task BackupAsync()
    {
        try
        {
            var folder = await Ui.PickFolderAsync();
            if (folder is null) return;
            var path = "";
            if (await Ui.GuardAsync(async () => path = await App.Admin.CreateBackupAsync(folder), _bar))
                Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Backup_Done", path));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, Ui.Describe(ex));
        }
    }

    private async Task RestoreAsync(bool folder)
    {
        string? path;
        try { path = folder ? await Ui.PickFolderAsync() : await Ui.PickOpenFileAsync(".zip"); }
        catch (System.Runtime.InteropServices.COMException ex) { Ui.Show(_bar, InfoBarSeverity.Error, Ui.Describe(ex)); return; }
        if (path is null) return;
        if (!await Ui.ConfirmAsync(XamlRoot, Ui.T("Backup_RestoreTitle"), Ui.T("Backup_RestoreBody", path), Ui.T("Backup_Restore"))) return;

        var result = await ElevatedHelper.RunAsync(ServiceVerb.Restore, path);
        if (result.Cancelled) Ui.Show(_bar, InfoBarSeverity.Warning, Ui.T("Service_Cancelled"));
        else if (result.Ok) Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Backup_Restored"));
        else Ui.Show(_bar, InfoBarSeverity.Error, Ui.T("Service_Failed", result.ExitCode) + (result.Log.Length > 0 ? "\n" + result.Log : ""));
        await App.PollAsync();
        await LoadAsync();
    }
}
