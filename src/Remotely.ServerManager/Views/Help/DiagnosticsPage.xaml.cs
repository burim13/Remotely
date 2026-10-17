using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Diagnostics;

namespace Remotely.ServerManager.Views.Help;

/// <summary>Log detail, the crash dump switch, Export diagnostics, the log folder, the Network test and the list of error codes. Nothing on this page uses the network.</summary>
public sealed partial class DiagnosticsPage : Page
{
    private readonly ComboBox _level = new() { MinWidth = 280, Header = Ui.T("Diag_LogLevel") };
    private readonly ToggleSwitch _dumps = new() { Header = Ui.T("Diag_CrashDumps") };
    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = true };
    private readonly Button _export;
    private bool _loading = true;

    public DiagnosticsPage()
    {
        InitializeComponent();
        _level.Items.Add(new ComboBoxItem { Content = Ui.T("Diag_Level_Debug"), Tag = LogLevel.Debug });
        _level.Items.Add(new ComboBoxItem { Content = Ui.T("Diag_Level_Info"), Tag = LogLevel.Info });
        _level.Items.Add(new ComboBoxItem { Content = Ui.T("Diag_Level_Warning"), Tag = LogLevel.Warning });
        _level.Items.Add(new ComboBoxItem { Content = Ui.T("Diag_Level_Error"), Tag = LogLevel.Error });
        _level.SelectedIndex = Math.Max(0, _level.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (LogLevel)i.Tag == DiagnosticsHub.Settings.LogLevel));
        _dumps.IsOn = DiagnosticsHub.Settings.CrashDumps;
        _level.SelectionChanged += (_, _) => Save();
        _dumps.Toggled += (_, _) => Save();

        _export = Ui.SmallButton(Ui.T("Diag_Export"), async (_, _) => await ExportAsync(), accent: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_export);
        buttons.Children.Add(Ui.SmallButton(Ui.T("Diag_OpenLogs"), (_, _) => OpenLogs()));
        buttons.Children.Add(Ui.SmallButton(Ui.T("Diag_NetworkTest"), (_, _) => Frame?.Navigate(typeof(NetworkTestPage))));

        var settings = new StackPanel { Spacing = 10 };
        settings.Children.Add(_level);
        settings.Children.Add(_dumps);
        settings.Children.Add(Ui.Secondary(Ui.T("Diag_CrashDumps_Note")));

        var export = new StackPanel { Spacing = 10 };
        export.Children.Add(buttons);
        export.Children.Add(Ui.Secondary(Ui.T("Diag_Export_Note")));

        var codes = new StackPanel { Spacing = 6 };
        codes.Children.Add(Ui.Text(Ui.T("Diag_Codes_Title"), "BodyStrongTextBlockStyle"));
        codes.Children.Add(Ui.Secondary(Ui.T("Diag_Codes_Intro")));
        foreach (var error in ErrorCatalogue.All.Where(e => e.Scope != ErrorScope.App))
            codes.Children.Add(Ui.Text($"{error.Code}  {Ui.T(error.TitleKey)}"));

        Root.Children.Add(Ui.Text(Ui.T("Diag_Help_Title"), "TitleTextBlockStyle"));
        Root.Children.Add(Ui.Secondary(Ui.T("Diag_Help_Intro")));
        Root.Children.Add(_bar);
        Root.Children.Add(Ui.Card(settings));
        Root.Children.Add(Ui.Card(export));
        Root.Children.Add(Ui.Card(codes));
        _loading = false;
    }

    private void Save()
    {
        if (_loading || _level.SelectedItem is not ComboBoxItem { Tag: LogLevel level }) return;
        DiagnosticsHub.Apply(new DiagnosticsSettings { LogLevel = level, CrashDumps = _dumps.IsOn });
    }

    private async Task ExportAsync()
    {
        _export.IsEnabled = false;
        try
        {
            var path = await Ui.PickSaveFileAsync($"remotely-manager-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}", "Zip", ".zip");
            if (path is null) return;
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                await DiagnosticsBundle.WriteAsync(DiagnosticsHub.Source(), file);
            Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Diag_Export_Done", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, Ui.T("Diag_Export_Failed", ex.Message));
        }
        finally { _export.IsEnabled = true; }
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsHub.LogDir);
            Process.Start(new ProcessStartInfo(DiagnosticsHub.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, ex.Message);
        }
    }
}
