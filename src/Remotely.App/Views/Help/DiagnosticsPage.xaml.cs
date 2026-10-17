using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.App.Shell;
using Remotely.Core.Diagnostics;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Help;

/// <summary>Log detail, the crash dump switch, Export diagnostics, the log folder, the Network test, and the list of error codes. Nothing on this page uses the network.</summary>
[NavigationPage("diagnostics", "Diag_Help_Nav", Order = 800, Glyph = 0xE9D9)]
public sealed partial class DiagnosticsPage : Page
{
    private bool _loading = true;

    public DiagnosticsPage()
    {
        InitializeComponent();
        TitleText.Text = Loc.Get("Diag_Help_Title");
        IntroText.Text = Loc.Get("Diag_Help_Intro");
        LevelBox.Header = Loc.Get("Diag_LogLevel");
        foreach (var (level, key) in new[] { (LogLevel.Debug, "Diag_Level_Debug"), (LogLevel.Info, "Diag_Level_Info"), (LogLevel.Warning, "Diag_Level_Warning"), (LogLevel.Error, "Diag_Level_Error") })
            LevelBox.Items.Add(new ComboBoxItem { Content = Loc.Get(key), Tag = level });
        DumpSwitch.Header = Loc.Get("Diag_CrashDumps");
        DumpNote.Text = Loc.Get("Diag_CrashDumps_Note");
        ExportButton.Content = Loc.Get("Diag_Export");
        LogsButton.Content = Loc.Get("Diag_OpenLogs");
        NetworkButton.Content = Loc.Get("Diag_NetworkTest");
        ExportNote.Text = Loc.Get("Diag_Export_Note");
        CodesTitle.Text = Loc.Get("Diag_Codes_Title");
        CodesIntro.Text = Loc.Get("Diag_Codes_Intro");
        foreach (var error in ErrorCatalogue.All.Where(e => e.Scope != ErrorScope.Manager))
            CodesList.Children.Add(new TextBlock { Text = $"{error.Code}  {Loc.Get(error.TitleKey)}", TextWrapping = TextWrapping.Wrap });

        var settings = DiagnosticsHub.Settings;
        LevelBox.SelectedIndex = Math.Max(0, LevelBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (LogLevel)i.Tag == settings.LogLevel));
        DumpSwitch.IsOn = settings.CrashDumps;
        LevelBox.SelectionChanged += (_, _) => Save();
        DumpSwitch.Toggled += (_, _) => Save();
        _loading = false;
    }

    private void Save()
    {
        if (_loading || LevelBox.SelectedItem is not ComboBoxItem { Tag: LogLevel level }) return;
        DiagnosticsHub.Apply(new DiagnosticsSettings { LogLevel = level, CrashDumps = DumpSwitch.IsOn });
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        ExportButton.IsEnabled = false;
        try
        {
            var folder = await App.Services.PickFolderAsync();
            if (folder is null) return;
            var path = Path.Combine(folder, $"remotely-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                await DiagnosticsBundle.WriteAsync(DiagnosticsHub.Source(), file);
            Show(InfoBarSeverity.Success, string.Format(Loc.Get("Diag_Export_Done"), path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Error, string.Format(Loc.Get("Diag_Export_Failed"), ex.Message));
        }
        finally { ExportButton.IsEnabled = true; }
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsHub.LogDir);
            Process.Start(new ProcessStartInfo(DiagnosticsHub.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Show(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void OnNetworkTest(object sender, RoutedEventArgs e) => Frame?.Navigate(typeof(NetworkTestPage));
}
