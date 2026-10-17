using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Remotely.Core.Lan;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Connect;

/// <summary>What the connection check should look at: the address that was typed (if any) and the token's 4-character label (if any).</summary>
public sealed record DiagnosisRequest(string? Address, string? Label);

/// <summary>Checks the network step by step (adapter, same network, reply to the search, connection port) and says in plain English which step failed.</summary>
public sealed partial class DiagnosisPage : Page
{
    private DiagnosisRequest _request = new(null, null);
    private CancellationTokenSource? _cts;
    private bool _userCancelled;

    public DiagnosisPage()
    {
        InitializeComponent();
        TitleText.Text = Loc.Get("Diag_Title");
        IntroText.Text = Loc.Get("Diag_Intro");
        RunButton.Content = Loc.Get("Diag_Run");
        CancelButton.Content = Loc.Get("Connect_Cancel");
        BackButton.Content = Loc.Get("Diag_Back");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is DiagnosisRequest request) _request = request;
        BackButton.Visibility = Frame?.CanGoBack == true ? Visibility.Visible : Visibility.Collapsed;
        _ = RunAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _cts?.Cancel();

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
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        _userCancelled = false;
        RunButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        Busy.IsActive = true;
        SummaryBar.IsOpen = false;
        StepsPanel.Children.Clear();
        try
        {
            var report = await new LanDiagnosis(new SystemDiagnosisEnvironment()).RunAsync(_request.Address, _request.Label, cts.Token);
            foreach (var step in report.Steps) StepsPanel.Children.Add(Row(step));
            SummaryBar.Severity = report.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            SummaryBar.Title = Loc.Get(report.Succeeded ? "Diag_Passed" : "Diag_Failed");
            SummaryBar.Message = report.Summary;
            SummaryBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            SummaryBar.Severity = InfoBarSeverity.Informational;
            SummaryBar.Title = "";
            SummaryBar.Message = Loc.Get(_userCancelled ? "Diag_Cancelled" : "Diag_TimedOut");
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

    private static UIElement Row(DiagnosisStepResult step)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon
        {
            Glyph = step.State switch { StepState.Passed => "", StepState.Failed => "", _ => "" },
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = step.State == StepState.Skipped ? 0.5 : 1,
        };
        row.Children.Add(icon);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = Loc.Get(step.Step switch
        {
            DiagnosisStep.Adapter => "Diag_Step_Adapter",
            DiagnosisStep.Subnet => "Diag_Step_Subnet",
            DiagnosisStep.DiscoveryReply => "Diag_Step_Discovery",
            _ => "Diag_Step_Port",
        }), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock { Text = step.Message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }
}
