using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Diagnostics;
using Remotely.Core.Lan;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Help;

/// <summary>The in-app Network test: this PC's network, the other PC (when an address or token is given), and the server (address from the settings) with its connection time.</summary>
public sealed partial class NetworkTestPage : Page
{
    private CancellationTokenSource? _cts;
    private bool _userCancelled;

    public NetworkTestPage()
    {
        InitializeComponent();
        TitleText.Text = Loc.Get("Diag_Net_Title");
        IntroText.Text = Loc.Get("Diag_Net_Intro");
        PeerBox.Header = Loc.Get("Diag_Net_Peer");
        LabelBox.Header = Loc.Get("Diag_Net_Label");
        RunButton.Content = Loc.Get("Diag_Net_Run");
        CancelButton.Content = Loc.Get("Diag_Net_Cancel");
        BackButton.Content = Loc.Get("Diag_Net_Back");
        Loaded += (_, _) => BackButton.Visibility = Frame?.CanGoBack == true ? Visibility.Visible : Visibility.Collapsed;
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private void OnRun(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _userCancelled = true;
        _cts?.Cancel();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
    }

    private async Task RunAsync()
    {
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _userCancelled = false;
        RunButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        Busy.IsActive = true;
        SummaryBar.IsOpen = false;
        ChecksPanel.Children.Clear();
        try
        {
            var server = App.Settings.IsConfigured ? App.Settings.ServerUri : null;
            var label = new string((LabelBox.Text ?? "").Where(char.IsLetterOrDigit).ToArray());
            var report = await new NetworkTest(new SystemDiagnosisEnvironment()).RunAsync(
                string.IsNullOrWhiteSpace(PeerBox.Text) ? null : PeerBox.Text, label.Length >= LanToken.IdLength ? label[..LanToken.IdLength].ToUpperInvariant() : null, server, cts.Token);
            foreach (var check in report.Checks) ChecksPanel.Children.Add(Row(check));
            SummaryBar.Severity = report.Passed ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            SummaryBar.Title = Loc.Get(report.Passed ? "Diag_Net_Passed" : "Diag_Net_Failed");
            SummaryBar.Message = report.FirstFailure is { } failure ? failure.Message : "";
            SummaryBar.IsOpen = true;
            DiagnosticsHub.Log.Info("networktest", report.Passed ? "passed" : "failed: " + report.FirstFailure?.Error?.Code);
        }
        catch (OperationCanceledException)
        {
            SummaryBar.Severity = InfoBarSeverity.Informational;
            SummaryBar.Title = "";
            SummaryBar.Message = Loc.Get(_userCancelled ? "Diag_Net_Cancelled" : "Diag_Net_TimedOut");
            SummaryBar.IsOpen = true;
        }
        finally
        {
            cts.Dispose();
            _cts = null;
            Busy.IsActive = false;
            CancelButton.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = true;
        }
    }

    private static UIElement Row(NetworkCheck check)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = check.Status switch { CheckStatus.Passed => "\uE73E", CheckStatus.Failed => "\uE711", CheckStatus.Note => "\uE946", _ => "\uE738" },
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = check.Status == CheckStatus.Skipped ? 0.5 : 1,
        });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = Loc.Get("Diag_Net_Check_" + check.Id), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock { Text = check.Message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        if (check.LatencyMs is { } ms) text.Children.Add(new TextBlock { Text = string.Format(Loc.Get("Diag_Net_Latency"), Math.Round(ms)), Opacity = 0.8 });
        if (check.Error is { } error) text.Children.Add(new TextBlock { Text = $"{error.Code}  {Loc.Get(error.TitleKey)}", Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }
}
