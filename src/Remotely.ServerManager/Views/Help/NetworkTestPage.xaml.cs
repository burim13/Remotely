using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Admin;
using Remotely.Core.Diagnostics;
using Remotely.Core.Lan;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager.Views.Help;

/// <summary>The Network test on the server PC: its network connection, any PCs sharing on the local network, and the server's own public address with its connection time.</summary>
public sealed partial class NetworkTestPage : Page
{
    private readonly TextBox _peer = new() { Header = Ui.T("Diag_Net_Peer") };
    private readonly TextBox _label = new() { Header = Ui.T("Diag_Net_Label") };
    private readonly InfoBar _summary = new() { IsOpen = false, IsClosable = false };
    private readonly StackPanel _checks = new() { Spacing = 10 };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 24, Height = 24 };
    private readonly Button _run;
    private readonly Button _cancel;
    private CancellationTokenSource? _cts;
    private bool _userCancelled;

    public NetworkTestPage()
    {
        InitializeComponent();
        _run = Ui.SmallButton(Ui.T("Diag_Net_Run"), async (_, _) => await RunAsync(), accent: true);
        _cancel = Ui.SmallButton(Ui.T("Diag_Net_Cancel"), (_, _) => { _userCancelled = true; _cts?.Cancel(); });
        _cancel.Visibility = Visibility.Collapsed;
        var back = Ui.SmallButton(Ui.T("Diag_Net_Back"), (_, _) => { if (Frame?.CanGoBack == true) Frame.GoBack(); });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_run);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(back);
        buttons.Children.Add(_busy);

        Root.Children.Add(Ui.Text(Ui.T("Diag_Net_Title"), "TitleTextBlockStyle"));
        Root.Children.Add(Ui.Secondary(Ui.T("Diag_Net_Intro")));
        Root.Children.Add(_peer);
        Root.Children.Add(_label);
        Root.Children.Add(buttons);
        Root.Children.Add(_summary);
        Root.Children.Add(_checks);
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private async Task RunAsync()
    {
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _userCancelled = false;
        _run.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible;
        _busy.IsActive = true;
        _summary.IsOpen = false;
        _checks.Children.Clear();
        try
        {
            string? server = null;
            try
            {
                var status = await App.Admin.GetStatusAsync(cts.Token);
                server = $"{(string.IsNullOrWhiteSpace(status.Address) ? "127.0.0.1" : status.Address)}:{status.Port}";
            }
            catch (Exception ex) when (ex is ServerAdminException or ServerUnavailableException)
            {
                DiagnosticsHub.Log.Warning("networktest", "The server's status could not be read: " + ErrorCatalogue.Describe(ex).Code);
            }
            var label = new string((_label.Text ?? "").Where(char.IsLetterOrDigit).ToArray());
            var report = await new NetworkTest(new SystemDiagnosisEnvironment()).RunAsync(
                string.IsNullOrWhiteSpace(_peer.Text) ? null : _peer.Text, label.Length >= LanToken.IdLength ? label[..LanToken.IdLength].ToUpperInvariant() : null, server, cts.Token);
            foreach (var check in report.Checks) _checks.Children.Add(Row(check));
            _summary.Severity = report.Passed ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            _summary.Title = report.Passed ? Ui.T("Diag_Net_Passed") : Ui.T("Diag_Net_Failed");
            _summary.Message = report.FirstFailure?.Message ?? "";
            _summary.IsOpen = true;
            DiagnosticsHub.Log.Info("networktest", report.Passed ? "passed" : "failed: " + report.FirstFailure?.Error?.Code);
        }
        catch (OperationCanceledException)
        {
            _summary.Severity = InfoBarSeverity.Informational;
            _summary.Title = "";
            _summary.Message = _userCancelled ? Ui.T("Diag_Net_Cancelled") : Ui.T("Diag_Net_TimedOut");
            _summary.IsOpen = true;
        }
        finally
        {
            cts.Dispose();
            _cts = null;
            _busy.IsActive = false;
            _cancel.Visibility = Visibility.Collapsed;
            _run.IsEnabled = true;
        }
    }

    private static UIElement Row(NetworkCheck check)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = check.Status switch { CheckStatus.Passed => "", CheckStatus.Failed => "", CheckStatus.Note => "", _ => "" },
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = check.Status == CheckStatus.Skipped ? 0.5 : 1,
        });
        var text = new StackPanel();
        text.Children.Add(Ui.Text(Ui.T("Diag_Net_Check_" + check.Id), "BodyStrongTextBlockStyle"));
        text.Children.Add(Ui.Secondary(check.Message));
        if (check.LatencyMs is { } ms) text.Children.Add(Ui.Secondary(Ui.T("Diag_Net_Latency", Math.Round(ms))));
        if (check.Error is { } error) text.Children.Add(Ui.Secondary($"{error.Code}  {Ui.T(error.TitleKey)}"));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }
}
