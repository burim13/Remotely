using System.Runtime.InteropServices;
using Remotely.UI.Localization;

namespace Remotely.App.Shell;

/// <summary>
/// The user-visible half of crash handling (the log file is written by <c>App.LogCrash</c>). An unhandled UI exception normally ends the process;
/// once the window is up we keep the app alive and show "Something went wrong" with the details and a Copy button. We stay out of the way
/// (the process crashes as before, so CI notices) for self-tests, <c>--show</c> launches, startup failures before the window is ready, and after five
/// of these in one run so a repeating failure cannot become an endless stream of dialogs.
/// </summary>
internal static class CrashReporter
{
    private const int MaxHandled = 5;
    private static int _handled;
    private static int _showing;

    /// <returns>true when the exception was turned into a dialog and the app should carry on.</returns>
    public static bool TryHandle(Exception? ex)
    {
        App.LogCrash(ex);
        if (App.Options.Show is not null || App.Options.IsSelfTest || App.Main is not { IsReady: true } main) return false;
        if (Interlocked.Increment(ref _handled) > MaxHandled) return false;
        var report = CrashReport.Format(ex, typeof(CrashReporter).Assembly.GetName().Version?.ToString(3) ?? "?",
            RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), DateTimeOffset.Now);
        main.DispatcherQueue.TryEnqueue(async () =>
        {
            if (Interlocked.Exchange(ref _showing, 1) == 1) return;
            try
            {
                main.ShowAndFocus();
                await App.Services.ShowErrorAsync(Loc.Get("Crash_Title"), Loc.Get("Crash_Message"), report);
            }
            catch (Exception shown) { App.LogCrash(shown); }
            finally { Interlocked.Exchange(ref _showing, 0); }
        });
        return true;
    }
}
