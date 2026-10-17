using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Remotely.Core.NetworkSetup;
using static Remotely.ServerManager.Setup.SetupUi;

namespace Remotely.ServerManager.Setup;

/// <summary>
/// First-run "make my server reachable" wizard: address, port and firewall, test from inside, router, test from outside, certificate.
/// The window never runs elevated; everything that needs administrator rights goes through <see cref="IElevatedActions"/> supplied by the host.
/// </summary>
public sealed partial class SetupWizardPage : Page
{
    private sealed record Step(string TitleKey, UIElement Panel, Func<Task>? OnEnter = null, Func<Task<bool>>? CanLeave = null);

    private SetupWizardDependencies? _deps;
    private SetupWizard? _wizard;
    private readonly List<Step> _steps = [];
    private int _index;
    private DispatcherTimer? _renewTimer;

    public SetupWizardPage()
    {
        InitializeComponent();
        TitleText.Text = T("Setup_Title");
        BackButton.Content = T("Setup_Back");
        NextButton.Content = T("Setup_Next");
        BackButton.Click += async (_, _) => await GoAsync(_index - 1);
        NextButton.Click += async (_, _) => await GoAsync(_index + 1);
        Unloaded += (_, _) => { _renewTimer?.Stop(); _outsideCancel?.Cancel(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SetupWizardDependencies deps) Initialize(deps);
    }

    /// <summary>The wizard can be started only once per page instance.</summary>
    public void Initialize(SetupWizardDependencies dependencies)
    {
        if (_deps is not null) return;
        _deps = dependencies;
        _wizard = new SetupWizard(dependencies.Services);

        _steps.Add(new Step("Setup_Step_Address", BuildAddressStep(), OnEnter: LoadAddressStepAsync, CanLeave: ApplyAddressStepAsync));
        _steps.Add(new Step("Setup_Step_Port", BuildPortStep(), OnEnter: RunPortChecksAsync));
        _steps.Add(new Step("Setup_Step_Inside", BuildInsideStep()));
        _steps.Add(new Step("Setup_Step_Router", BuildRouterStep(), OnEnter: RefreshRouterStepAsync));
        _steps.Add(new Step("Setup_Step_Outside", BuildOutsideStep()));
        _steps.Add(new Step("Setup_Step_Certificate", BuildCertificateStep(), OnEnter: RefreshCertificateStepAsync));
        foreach (var step in _steps)
        {
            step.Panel.Visibility = Visibility.Collapsed;
            StepHost.Children.Add(step.Panel);
        }

        // Leases are short on purpose; while this page is open it asks the router to extend them. The host should keep doing this while the server runs.
        _renewTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _renewTimer.Tick += async (_, _) => { try { await dependencies.Services.Mapping.RenewDueAsync(); } catch (Exception) { } };
        _renewTimer.Start();

        _ = GoAsync(0);
    }

    /// <summary>Removes every router mapping the wizard created. The host calls this when the server stops and when the Manager closes.</summary>
    public async Task<IReadOnlyList<PortMapping>> RemoveMappingsAsync() =>
        _deps is null ? [] : await _deps.Services.Mapping.RemoveAllAsync();

    private async Task GoAsync(int target)
    {
        if (_wizard is null || target < 0) return;
        if (target > _index && _steps[_index].CanLeave is { } canLeave && !await canLeave()) return;
        if (target >= _steps.Count)
        {
            _deps?.Finished?.Invoke();
            return;
        }
        _index = target;
        for (var i = 0; i < _steps.Count; i++) _steps[i].Panel.Visibility = i == _index ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = F("Setup_StepOf", _index + 1, _steps.Count, T(_steps[_index].TitleKey));
        Progress.Value = (double)(_index + 1) / _steps.Count;
        BackButton.IsEnabled = _index > 0;
        NextButton.Content = T(_index == _steps.Count - 1 ? "Setup_Done" : "Setup_Next");
        if (_steps[_index].OnEnter is { } enter)
        {
            try { await enter(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { StepText.Text = StepText.Text + "  -  " + ex.Message; }
        }
    }
}
