using Microsoft.UI.Xaml.Controls;
using Remotely.App.Services;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Onboarding;

public sealed partial class OnboardingDonePage : Page, IOnboardingStep
{
    public OnboardingDonePage()
    {
        InitializeComponent();
        var s = App.Settings;
        Heading.Text = Loc.Get("Onboarding_Done_Title");
        Body.Text = Loc.Get("Onboarding_Done_Body");
        AddLine(string.Format(Loc.Get("Onboarding_Done_Name"), s.DeviceName));
        AddLine(Loc.Get(App.Share.IsSharing ? "Onboarding_Done_SharingOn" : "Onboarding_Done_SharingOff"));
        AddLine(s.IsConfigured ? string.Format(Loc.Get("Onboarding_Done_Server"), s.ServerUri) : Loc.Get("Onboarding_Done_NoServer"));
        StartWithWindows.Content = Loc.Get("Onboarding_Done_StartWithWindows");
        CloseToTray.Content = Loc.Get("Onboarding_Done_CloseToTray");
        StartWithWindows.IsChecked = WindowsStartup.IsEnabled;
        CloseToTray.IsChecked = s.CloseToTray;
    }

    private void AddLine(string text) => Summary.Children.Add(new TextBlock { Text = text, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });

    public Task<bool> CommitAsync()
    {
        var s = App.Settings;
        s.CloseToTray = CloseToTray.IsChecked == true;
        var wantStartup = StartWithWindows.IsChecked == true;
        if (wantStartup != WindowsStartup.IsEnabled)
        {
            if (WindowsStartup.Set(wantStartup)) s.StartWithWindows = wantStartup;
            else
            {
                Bar.Message = Loc.Get("Startup_Failed");
                Bar.IsOpen = true;
                StartWithWindows.IsChecked = WindowsStartup.IsEnabled;
                return Task.FromResult(false);   // stay once so the person sees why; Finish again continues
            }
        }
        try { s.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogCrash(ex); }
        return Task.FromResult(true);
    }
}
