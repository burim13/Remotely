using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.NetworkSetup;
using static Remotely.ServerManager.Setup.SetupUi;

namespace Remotely.ServerManager.Setup;

public sealed partial class SetupWizardPage
{
    private static readonly TimeSpan OutsideWait = TimeSpan.FromMinutes(3);

    private InfoBar _outsideBar = null!;
    private StackPanel _outsideInstructions = null!;
    private CancellationTokenSource? _outsideCancel;

    private UIElement BuildOutsideStep()
    {
        var panel = Column();
        panel.Children.Add(Heading(T("Setup_Outside_Heading")));
        panel.Children.Add(Body(T("Setup_Outside_Intro")));
        _outsideBar = Bar();
        panel.Children.Add(ActionButton(T("Setup_Outside_Start"), RunOutsideTestAsync, _outsideBar));
        _outsideInstructions = Column(8);
        panel.Children.Add(_outsideInstructions);
        panel.Children.Add(_outsideBar);
        return panel;
    }

    private async Task RunOutsideTestAsync()
    {
        _outsideCancel?.Cancel();
        _outsideCancel = new CancellationTokenSource();
        var ct = _outsideCancel.Token;
        _outsideInstructions.Children.Clear();
        (OutsideCheckTicket Ticket, string Url)? started;
        try { started = await _wizard!.StartOutsideTestAsync(_wanBox.Text, ct); }
        catch (NotSupportedException)
        {
            Show(_outsideBar, InfoBarSeverity.Warning, T("Setup_Outside_Unavailable"), T("Setup_Outside_UnavailableBody"));
            return;
        }
        if (started is null)
        {
            Show(_outsideBar, InfoBarSeverity.Warning, T("Setup_Outside_NeedName"), T("Setup_Outside_NeedNameBody"));
            return;
        }

        var (ticket, url) = started.Value;
        _outsideInstructions.Children.Add(Label(T("Setup_Outside_StepsHeading")));
        foreach (var (line, number) in new[] { "Setup_Outside_Step1", "Setup_Outside_Step2", "Setup_Outside_Step3", "Setup_Outside_Step4" }.Select((k, i) => (k, i + 1)))
            _outsideInstructions.Children.Add(Body(number + ". " + T(line)));
        _outsideInstructions.Children.Add(ValueLine(T("Setup_Outside_Address"), url));
        Show(_outsideBar, InfoBarSeverity.Informational, T("Setup_Outside_Waiting"), T("Setup_Outside_WaitingBody"));

        try
        {
            var (verdict, hit) = await _wizard.WaitForOutsideAsync(ticket, OutsideWait, ct: ct);
            switch (verdict)
            {
                case OutsideVerdict.ReachableFromOutside: Show(_outsideBar, InfoBarSeverity.Success, T("Setup_Outside_Works"), F("Setup_Outside_WorksBody", hit!.Source.ToString())); break;
                case OutsideVerdict.SeenFromInside: Show(_outsideBar, InfoBarSeverity.Warning, T("Setup_Outside_Inside"), T("Setup_Outside_InsideBody")); break;
                case OutsideVerdict.Unclear: Show(_outsideBar, InfoBarSeverity.Warning, T("Setup_Outside_Unclear"), F("Setup_Outside_UnclearBody", hit!.Source.ToString())); break;
                default: Show(_outsideBar, InfoBarSeverity.Error, T("Setup_Outside_Nothing"), T("Setup_Outside_NothingBody")); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (NotSupportedException) { Show(_outsideBar, InfoBarSeverity.Warning, T("Setup_Outside_Unavailable"), T("Setup_Outside_UnavailableBody")); }
    }
}
