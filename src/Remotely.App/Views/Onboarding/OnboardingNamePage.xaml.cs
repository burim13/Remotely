using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Onboarding;

public sealed partial class OnboardingNamePage : Page, IOnboardingStep
{
    public OnboardingNamePage()
    {
        InitializeComponent();
        Heading.Text = Loc.Get("Onboarding_Name_Title");
        Body.Text = Loc.Get("Onboarding_Name_Body");
        NameBox.Header = Loc.Get("Onboarding_Name_Header");
        AutomationProperties.SetName(NameBox, Loc.Get("Onboarding_Name_Header"));
        NameBox.Text = App.Settings.DeviceName;
        NameBox.SelectAll();
    }

    public Task<bool> CommitAsync()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Bar.Message = Loc.Get("Onboarding_Name_Empty");
            Bar.IsOpen = true;
            return Task.FromResult(false);
        }
        Bar.IsOpen = false;
        App.Settings.DeviceName = name;
        try { App.Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogCrash(ex); }
        return Task.FromResult(true);
    }
}
