using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.NetworkSetup;
using static Remotely.ServerManager.Setup.SetupUi;

namespace Remotely.ServerManager.Setup;

public sealed partial class SetupWizardPage
{
    private InfoBar _portBar = null!;
    private InfoBar _firewallBar = null!;
    private InfoBar _insideBar = null!;
    private TextBlock _insideDetails = null!;
    private TextBlock _portIntro = null!;

    private UIElement BuildPortStep()
    {
        var panel = Column();
        panel.Children.Add(Heading(T("Setup_Port_Heading")));
        _portIntro = Body("");
        panel.Children.Add(_portIntro);
        _portBar = Bar();
        panel.Children.Add(_portBar);
        panel.Children.Add(ActionButton(T("Setup_Port_Check"), RunPortChecksAsync, _portBar));

        panel.Children.Add(Label(T("Setup_Firewall_Heading")));
        panel.Children.Add(Body(T("Setup_Firewall_Intro")));
        _firewallBar = Bar();
        panel.Children.Add(_firewallBar);
        panel.Children.Add(ActionButton(T("Setup_Firewall_Add"), AddFirewallRuleAsync, _firewallBar));
        return panel;
    }

    private async Task RunPortChecksAsync()
    {
        var wizard = _wizard!;
        _portIntro.Text = F("Setup_Port_Intro", wizard.Port);
        var step = await wizard.CheckPortAsync();
        switch (step.Verdict)
        {
            case PortStepVerdict.Free: Show(_portBar, InfoBarSeverity.Success, F("Setup_Port_Free", wizard.Port), T("Setup_Port_FreeBody")); break;
            case PortStepVerdict.UsedByThisServer: Show(_portBar, InfoBarSeverity.Success, F("Setup_Port_Ours", wizard.Port), T("Setup_Port_OursBody")); break;
            case PortStepVerdict.UsedBySomethingElse: Show(_portBar, InfoBarSeverity.Error, F("Setup_Port_Taken", wizard.Port), T("Setup_Port_TakenBody")); break;
            case PortStepVerdict.NotAllowed: Show(_portBar, InfoBarSeverity.Error, F("Setup_Port_Denied", wizard.Port), T("Setup_Port_DeniedBody")); break;
            case PortStepVerdict.Invalid: Show(_portBar, InfoBarSeverity.Error, T("Setup_Err_PortTitle"), T("Setup_Err_PortInvalid")); break;
            default: Show(_portBar, InfoBarSeverity.Warning, T("Setup_Port_Unknown"), null); break;
        }

        switch (await wizard.GetFirewallAsync())
        {
            case FirewallRuleState.Present: Show(_firewallBar, InfoBarSeverity.Success, F("Setup_Firewall_Present", wizard.Port), T("Setup_Firewall_PresentBody")); break;
            case FirewallRuleState.PresentForOtherPort: Show(_firewallBar, InfoBarSeverity.Warning, T("Setup_Firewall_OtherPort"), F("Setup_Firewall_OtherPortBody", wizard.Port)); break;
            case FirewallRuleState.Missing: Show(_firewallBar, InfoBarSeverity.Warning, T("Setup_Firewall_Missing"), F("Setup_Firewall_MissingBody", wizard.Port)); break;
            default: Show(_firewallBar, InfoBarSeverity.Informational, T("Setup_Firewall_Unknown"), T("Setup_Firewall_UnknownBody")); break;
        }
    }

    private async Task AddFirewallRuleAsync()
    {
        var wizard = _wizard!;
        Show(_firewallBar, InfoBarSeverity.Informational, T("Setup_Firewall_Asking"), T("Setup_Firewall_AskingBody"));
        var result = await _deps!.Services.Elevated.AddFirewallRuleAsync(wizard.Port);
        if (result.Ok) Show(_firewallBar, InfoBarSeverity.Success, F("Setup_Firewall_Added", wizard.Port), T("Setup_Firewall_AddedBody"));
        else Show(_firewallBar, InfoBarSeverity.Error, T("Setup_Firewall_Failed"), string.IsNullOrEmpty(result.Detail) ? T(result.ErrorKey ?? "Setup_Err_Unexpected") : T(result.ErrorKey ?? "Setup_Err_Unexpected") + " " + result.Detail);
    }

    private UIElement BuildInsideStep()
    {
        var panel = Column();
        panel.Children.Add(Heading(T("Setup_Inside_Heading")));
        panel.Children.Add(Body(T("Setup_Inside_Intro")));
        _insideBar = Bar();
        panel.Children.Add(_insideBar);
        _insideDetails = Body("");
        panel.Children.Add(ActionButton(T("Setup_Inside_Run"), RunInsideTestAsync, _insideBar));
        panel.Children.Add(_insideDetails);
        return panel;
    }

    private static string Describe(ReachabilityResult r) => F("Setup_Inside_Line", r.Host, r.Port, T(r.Outcome switch
    {
        ReachabilityOutcome.Reachable => "Setup_Outcome_Reachable",
        ReachabilityOutcome.WrongServer => "Setup_Outcome_WrongServer",
        ReachabilityOutcome.Refused => "Setup_Outcome_Refused",
        ReachabilityOutcome.TimedOut => "Setup_Outcome_TimedOut",
        ReachabilityOutcome.NameNotFound => "Setup_Outcome_NameNotFound",
        ReachabilityOutcome.NotTls => "Setup_Outcome_NotTls",
        _ => "Setup_Outcome_Failed",
    }));

    private async Task RunInsideTestAsync()
    {
        Show(_insideBar, InfoBarSeverity.Informational, T("Setup_Inside_Running"), null);
        var report = await _wizard!.TestInsideAsync();
        var lines = new List<string> { Describe(report.Loopback) };
        if (report.NetworkAddress is not null) lines.Add(Describe(report.NetworkAddress));
        if (report.PublicName is not null) lines.Add(Describe(report.PublicName));
        if (report.PublicName is { Outcome: not ReachabilityOutcome.Reachable }) lines.Add(T("Setup_Inside_NameNote"));
        _insideDetails.Text = string.Join(Environment.NewLine, lines);
        switch (report.Verdict)
        {
            case InsideVerdict.Works: Show(_insideBar, InfoBarSeverity.Success, T("Setup_Inside_Works"), T("Setup_Inside_WorksBody")); break;
            case InsideVerdict.BlockedOnNetworkAddress: Show(_insideBar, InfoBarSeverity.Error, T("Setup_Inside_Blocked"), T("Setup_Inside_BlockedBody")); break;
            case InsideVerdict.WrongServer: Show(_insideBar, InfoBarSeverity.Error, T("Setup_Inside_Wrong"), T("Setup_Inside_WrongBody")); break;
            default: Show(_insideBar, InfoBarSeverity.Error, T("Setup_Inside_Down"), T("Setup_Inside_DownBody")); break;
        }
    }
}
