using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Onboarding;

public sealed partial class OnboardingServerPage : Page, IOnboardingStep
{
    public OnboardingServerPage()
    {
        InitializeComponent();
        Heading.Text = Loc.Get("Onboarding_Server_Title");
        Body.Text = Loc.Get("Onboarding_Server_Body");
        InviteBox.Header = Loc.Get("Onboarding_Server_Header");
        InviteBox.PlaceholderText = Loc.Get("Onboarding_Server_Placeholder");
        AutomationProperties.SetName(InviteBox, Loc.Get("Onboarding_Server_Header"));
        if (App.Settings.IsConfigured) Show(InfoBarSeverity.Success, string.Format(Loc.Get("Onboarding_Server_Connected"), App.Settings.ServerUri));
    }

    private void Show(InfoBarSeverity severity, string text)
    {
        Bar.Severity = severity;
        Bar.Message = text;
        Bar.IsOpen = true;
    }

    public async Task<bool> CommitAsync()
    {
        var invite = InviteBox.Text.Trim();
        if (invite.Length == 0) return true;   // the server is optional
        Busy.IsActive = true;
        InviteBox.IsEnabled = false;
        Show(InfoBarSeverity.Informational, Loc.Get("Onboarding_Server_Working"));
        try
        {
            var (ok, message) = await Join.WithInviteAsync(invite, App.Settings.DeviceName);
            if (!ok)
            {
                Show(InfoBarSeverity.Error, message);
                return false;
            }
            InviteBox.Text = "";
            Show(InfoBarSeverity.Success, string.Format(Loc.Get("Onboarding_Server_Connected"), App.Settings.ServerUri));
            return true;
        }
        finally
        {
            Busy.IsActive = false;
            InviteBox.IsEnabled = true;
        }
    }
}
