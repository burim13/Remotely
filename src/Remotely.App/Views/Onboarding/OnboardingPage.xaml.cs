using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Onboarding;

/// <summary>The first-run flow: name this PC, share on the network, connect to a server, finish. "Skip setup" leaves at any step; Back never loses what was already applied.</summary>
public sealed partial class OnboardingPage : Page
{
    private static readonly Type[] StepTypes = [typeof(OnboardingNamePage), typeof(OnboardingSharePage), typeof(OnboardingServerPage), typeof(OnboardingDonePage)];
    private int _index;
    private bool _busy;

    public OnboardingPage()
    {
        InitializeComponent();
        BackButton.Content = Loc.Get("Onboarding_Back");
        SkipButton.Content = Loc.Get("Onboarding_Skip");
        Progress.Maximum = StepTypes.Length;
        ShowStep(0);
    }

    /// <summary>Raised once, when the flow was finished or skipped (and is saved as done).</summary>
    public event Action? Finished;

    private void ShowStep(int index)
    {
        _index = index;
        StepFrame.Navigate(StepTypes[index]);
        var last = index == StepTypes.Length - 1;
        StepText.Text = string.Format(Loc.Get("Onboarding_StepOf"), index + 1, StepTypes.Length);
        Progress.Value = index + 1;
        BackButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = Loc.Get(last ? "Onboarding_Finish" : "Onboarding_Next");
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        NextButton.IsEnabled = BackButton.IsEnabled = SkipButton.IsEnabled = false;
        try
        {
            if (StepFrame.Content is IOnboardingStep step && !await step.CommitAsync()) return;
            if (_index == StepTypes.Length - 1) Complete();
            else ShowStep(_index + 1);
        }
        finally
        {
            _busy = false;
            NextButton.IsEnabled = BackButton.IsEnabled = SkipButton.IsEnabled = true;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (!_busy && _index > 0) ShowStep(_index - 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (!_busy) Complete();
    }

    private void Complete()
    {
        App.Settings.OnboardingDone = true;
        try { App.Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogCrash(ex); }
        Finished?.Invoke();
    }
}
