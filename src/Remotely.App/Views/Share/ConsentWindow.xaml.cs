using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Remotely.Core.Lan.Consent;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Share;

/// <summary>
/// The question: "Anna's laptop wants to connect. Allow once / Always allow this PC / Deny." It is a window of its own (not a dialog inside the app window), so it is seen
/// even when the app is minimised. The highlighted button is Deny, so a stray Enter key never lets anyone in. Closing the window counts as Deny.
/// </summary>
public sealed partial class ConsentWindow : Window
{
    private readonly TaskCompletionSource<ConsentDecision> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherQueueTimer _tick;
    private readonly DateTimeOffset _deadline;

    public ConsentWindow(ConsentRequest request, TimeSpan timeout)
    {
        InitializeComponent();
        Title = Loc.Get("Consent_Title");
        App.ApplyTheme(this);
        TitleText.Text = Loc.Get("Consent_Title");
        BodyText.Text = ShareText.Format("Consent_Body", request.ControllerName, request.Address);
        NoteText.Text = Loc.Get(request.CanRemember ? "Consent_AlwaysHelp" : "Consent_NoRemember");
        OnceButton.Content = Loc.Get("Consent_Once");
        AlwaysButton.Content = Loc.Get("Consent_Always");
        AlwaysButton.IsEnabled = request.CanRemember;
        DenyButton.Content = Loc.Get("Consent_Deny");
        _deadline = DateTimeOffset.UtcNow + timeout;
        UpdateCountdown();

        ShareWindows.PlaceTop(this, 560, 300, center: true);
        AppWindow.Closing += (_, _) => _answer.TrySetResult(ConsentDecision.Deny);
        _tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => UpdateCountdown();
        _tick.Start();
    }

    public Task<ConsentDecision> Answer => _answer.Task;

    private void UpdateCountdown()
    {
        var left = _deadline - DateTimeOffset.UtcNow;
        CountdownText.Text = ShareText.Format("Consent_Countdown", Math.Max(0, (int)Math.Ceiling(left.TotalSeconds)));
    }

    private void Finish(ConsentDecision decision)
    {
        _answer.TrySetResult(decision);
        _tick.Stop();
        Close();
    }

    /// <summary>Called when nobody answered in time, or the connection went away.</summary>
    public void CloseWithoutAnswer() => Finish(ConsentDecision.Deny);

    private void OnOnce(object sender, RoutedEventArgs e) => Finish(ConsentDecision.AllowOnce);
    private void OnAlways(object sender, RoutedEventArgs e) => Finish(ConsentDecision.AlwaysAllow);
    private void OnDeny(object sender, RoutedEventArgs e) => Finish(ConsentDecision.Deny);
}
