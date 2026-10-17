namespace Remotely.App.Views.Onboarding;

/// <summary>A page inside the first-run flow. The flow calls <see cref="CommitAsync"/> when the person presses Next or Finish; false keeps them on the page (the page has shown why).</summary>
internal interface IOnboardingStep
{
    Task<bool> CommitAsync();
}
