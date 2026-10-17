using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Onboarding;

public sealed partial class OnboardingSharePage : Page, IOnboardingStep
{
    public OnboardingSharePage()
    {
        InitializeComponent();
        Heading.Text = Loc.Get("Onboarding_Share_Title");
        Body.Text = Loc.Get("Onboarding_Share_Body");
        ShareSwitch.Header = Loc.Get("Onboarding_Share_Switch");
        ShareSwitch.OnContent = Loc.Get("Common_On");
        ShareSwitch.OffContent = Loc.Get("Common_Off");
        AutomationProperties.SetName(ShareSwitch, Loc.Get("Onboarding_Share_Switch"));
        ShareSwitch.IsOn = App.Settings.ShareOnStart || App.Share.IsSharing;
    }

    public async Task<bool> CommitAsync()
    {
        Bar.IsOpen = false;
        if (ShareSwitch.IsOn)
        {
            if (!App.Share.IsSharing && App.Share.Start(_ => { }) is { } error)
            {
                Bar.Message = error;
                Bar.IsOpen = true;
                return false;
            }
        }
        else if (App.Share.IsSharing) await App.Share.StopAsync();

        App.Settings.ShareOnStart = ShareSwitch.IsOn;
        try { App.Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogCrash(ex); }
        App.Services.RaiseShareStateChanged();
        return true;
    }
}
