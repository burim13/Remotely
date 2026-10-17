using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.NetworkSetup;
using static Remotely.ServerManager.Setup.SetupUi;

namespace Remotely.ServerManager.Setup;

public sealed partial class SetupWizardPage
{
    private StackPanel _routerValues = null!;
    private StackPanel _routerSteps = null!;
    private StackPanel _routerNotes = null!;
    private TextBox _wanBox = null!;
    private InfoBar _wanBar = null!;
    private ToggleSwitch _autoMap = null!;
    private InfoBar _mapBar = null!;
    private TextBlock _mapList = null!;
    private Button _mapCreate = null!;

    private UIElement BuildRouterStep()
    {
        var panel = Column();
        panel.Children.Add(Heading(T("Setup_Router_Heading")));
        panel.Children.Add(Body(T("Setup_Router_Intro")));

        panel.Children.Add(Label(T("Setup_Router_ValuesHeading")));
        _routerValues = Column(6);
        panel.Children.Add(_routerValues);

        panel.Children.Add(Label(T("Setup_Router_StepsHeading")));
        _routerSteps = Column(6);
        panel.Children.Add(_routerSteps);

        panel.Children.Add(Label(T("Setup_Router_WanHeading")));
        panel.Children.Add(Body(T("Setup_Router_WanIntro")));
        _wanBox = new TextBox { Header = T("Setup_Router_WanBox"), PlaceholderText = "203.0.113.5", MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_wanBox, T("Setup_Router_WanBox"));
        _wanBar = Bar();
        panel.Children.Add(_wanBox);
        panel.Children.Add(ActionButton(T("Setup_Router_WanCheck"), () => { CheckWan(); return Task.CompletedTask; }, _wanBar));
        panel.Children.Add(_wanBar);

        panel.Children.Add(Label(T("Setup_Router_NotesHeading")));
        _routerNotes = Column(6);
        panel.Children.Add(_routerNotes);

        panel.Children.Add(Label(T("Setup_Upnp_Heading")));
        panel.Children.Add(Body(T("Setup_Upnp_Explain")));
        _autoMap = new ToggleSwitch { Header = T("Setup_Upnp_Toggle"), IsOn = false, OnContent = T("Setup_Upnp_OnLabel"), OffContent = T("Setup_Upnp_OffLabel") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_autoMap, T("Setup_Upnp_Toggle"));
        _autoMap.Toggled += async (_, _) => await OnAutoMapToggledAsync();
        panel.Children.Add(_autoMap);
        _mapBar = Bar();
        _mapList = Body("");
        _mapCreate = ActionButton(T("Setup_Upnp_Create"), CreateMappingAsync, _mapBar);
        _mapCreate.IsEnabled = false;
        panel.Children.Add(_mapCreate);
        panel.Children.Add(_mapBar);
        panel.Children.Add(_mapList);
        return panel;
    }

    private Task RefreshRouterStepAsync()
    {
        _routerValues.Children.Clear();
        _routerSteps.Children.Clear();
        _routerNotes.Children.Clear();
        var guide = _wizard!.GetRouterInstructions();
        if (guide is null)
        {
            _routerSteps.Children.Add(Body(T("Setup_Router_NeedAddress")));
            return Task.CompletedTask;
        }
        _routerValues.Children.Add(ValueLine(T("Setup_Router_Protocol"), guide.Protocol));
        _routerValues.Children.Add(ValueLine(T("Setup_Router_ExternalPort"), guide.ExternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _routerValues.Children.Add(ValueLine(T("Setup_Router_InternalAddress"), guide.InternalAddress));
        _routerValues.Children.Add(ValueLine(T("Setup_Router_InternalPort"), guide.InternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var number = 1;
        foreach (var step in guide.Steps) _routerSteps.Children.Add(Body(number++ + ". " + F(step)));
        foreach (var note in guide.Notes) _routerNotes.Children.Add(Body(F(note)));
        RenderMappings();
        return Task.CompletedTask;
    }

    private void CheckWan()
    {
        switch (WanAssessment.Assess(_wanBox.Text))
        {
            case WanVerdict.PublicAddress: Show(_wanBar, InfoBarSeverity.Success, T("Setup_Wan_Public"), T("Setup_Wan_PublicBody")); break;
            case WanVerdict.CarrierGradeNat: Show(_wanBar, InfoBarSeverity.Error, T("Setup_Wan_Cgnat"), T("Setup_Wan_CgnatBody")); break;
            case WanVerdict.BehindAnotherRouter: Show(_wanBar, InfoBarSeverity.Error, T("Setup_Wan_Behind"), T("Setup_Wan_BehindBody")); break;
            default: Show(_wanBar, InfoBarSeverity.Warning, T("Setup_Wan_Invalid"), T("Setup_Wan_InvalidBody")); break;
        }
    }

    private async Task OnAutoMapToggledAsync()
    {
        var mapping = _deps!.Services.Mapping;
        mapping.AutomaticMappingEnabled = _autoMap.IsOn;
        _mapCreate.IsEnabled = _autoMap.IsOn;
        if (_autoMap.IsOn) { Show(_mapBar, InfoBarSeverity.Informational, T("Setup_Upnp_NothingYet"), T("Setup_Upnp_NothingYetBody")); return; }

        var left = await mapping.RemoveAllAsync();
        _mapBar.IsOpen = false;
        if (left.Count > 0) Show(_mapBar, InfoBarSeverity.Warning, T("Setup_Upnp_RemoveFailed"), T("Setup_Upnp_RemoveFailedBody"));
        RenderMappings();
    }

    private async Task CreateMappingAsync()
    {
        Show(_mapBar, InfoBarSeverity.Informational, T("Setup_Upnp_Asking"), null);
        var attempt = await _wizard!.CreateAutomaticMappingAsync();
        if (attempt.Ok)
        {
            Show(_mapBar, InfoBarSeverity.Success, T("Setup_Upnp_Done"), T("Setup_Upnp_DoneBody"));
            if (attempt.Mapping!.ExternalAddress is { } external && _wanBox.Text.Length == 0)
            {
                _wanBox.Text = external.ToString();
                CheckWan();
            }
        }
        else Show(_mapBar, InfoBarSeverity.Error, T("Setup_Upnp_Failed"), T(attempt.ErrorKey ?? "Setup_Upnp_NoAnswer") + (attempt.Detail is null ? "" : " (" + attempt.Detail + ")"));
        RenderMappings();
    }

    private void RenderMappings()
    {
        var list = _deps!.Services.Mapping.Mappings;
        _mapList.Text = list.Count == 0
            ? T("Setup_Upnp_None")
            : string.Join(Environment.NewLine, list.Select(m => F(PortMappingSession.Describe(m)))) + Environment.NewLine + T("Setup_Upnp_RemovedWhen");
    }
}
