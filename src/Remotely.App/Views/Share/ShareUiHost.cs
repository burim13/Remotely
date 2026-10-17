using Microsoft.UI.Dispatching;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Share;

/// <summary>
/// Everything the person sees while sharing that is not the Share screen itself: the question window, the always-on-top banner and the notifications.
/// It lives as long as the app and works whichever page is open (the events come from <see cref="ShareCoordinator"/>, on any thread; windows are made on the UI thread).
/// </summary>
public sealed class ShareUiHost : IDisposable
{
    private readonly ShareService _share;
    private readonly DispatcherQueue _ui;
    private SessionBannerWindow? _banner;
    private ConsentWindow? _question;

    internal ShareUiHost(ShareService share, DispatcherQueue ui)
    {
        _share = share;
        _ui = ui;
        share.Coordinator.SessionStarted += OnSessionStarted;
        share.Coordinator.SessionEnded += OnSessionEnded;
        share.Coordinator.Refused += OnRefused;
    }

    /// <summary>True while the banner window exists (a session is running). Safe to read from any thread.</summary>
    public bool BannerOpen => Volatile.Read(ref _banner) is not null;
    /// <summary>The last failure while making or closing a window (null if none). A window that cannot be made must never take the app down.</summary>
    public string? LastError { get; private set; }
    public bool QuestionOpen => Volatile.Read(ref _question) is not null;

    /// <summary>Called from the UI thread or any other: shows the question and returns the person's answer. Cancelling closes the window.</summary>
    public Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken ct)
    {
        var result = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(() =>
        {
            try
            {
                var window = new ConsentWindow(request, _share.Coordinator.Options.PromptTimeout);
                Volatile.Write(ref _question, window);
                window.Answer.ContinueWith(t =>
                {
                    Volatile.Write(ref _question, null);
                    result.TrySetResult(t.IsCompletedSuccessfully ? t.Result : ConsentDecision.Deny);
                }, TaskScheduler.Default);
                ct.Register(() => _ui.TryEnqueue(window.CloseWithoutAnswer));
                window.Activate();
                Toast("Toast_Question_Title", "Toast_Question_Body", request.ControllerName, request.Address);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LastError = ex.ToString();
                App.LogCrash(ex);
                result.TrySetResult(ConsentDecision.Deny);   // no window, no answer: nobody is let in
            }
        })) result.TrySetResult(ConsentDecision.Deny);
        return result.Task;
    }

    private void OnSessionStarted(LanSessionInfo info)
    {
        _ui.TryEnqueue(() =>
        {
            try
            {
                var banner = new SessionBannerWindow(_share, info);
                Volatile.Write(ref _banner, banner);
                banner.Activate();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LastError = ex.ToString();
                App.LogCrash(ex);
            }
        });
        Toast("Toast_Connected_Title", "Toast_Connected_Body", info.ControllerName);
    }

    private void OnSessionEnded(LanSessionInfo info, string reason)
    {
        _ui.TryEnqueue(() =>
        {
            var banner = Interlocked.Exchange(ref _banner, null);
            try { banner?.CloseForReal(); } catch (Exception ex) when (ex is not OutOfMemoryException) { App.LogCrash(ex); }
        });
        Toast("Toast_Left_Title", reason == SessionEndReasons.ControllerLeft ? "Toast_Left_Body" : "Toast_Stopped_Body", info.ControllerName);
    }

    private void OnRefused(ConsentRequest request, ConsentResult result) =>
        Toast("Toast_Refused_Title", "Toast_Refused_Body", request.ControllerName, request.Address);

    private void Toast(string titleKey, string bodyKey, params object[] args)
    {
        try { _share.ToastSink(ShareText.Format(titleKey, args), ShareText.Format(bodyKey, args)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.LogCrash(ex); }
    }

    public void Dispose()
    {
        _share.Coordinator.SessionStarted -= OnSessionStarted;
        _share.Coordinator.SessionEnded -= OnSessionEnded;
        _share.Coordinator.Refused -= OnRefused;
    }
}
