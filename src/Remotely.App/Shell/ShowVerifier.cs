using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Remotely.App.Shell;

/// <summary>
/// Runtime proof for <c>--show PageName</c> (contract in docs/cli.md). The page must exist, navigate without exception (Frame.NavigationFailed),
/// raise Loaded, and have content and a non-zero size. Success writes <c>LOADED &lt;PageName&gt; &lt;w&gt;x&lt;h&gt;</c> and the app keeps running (CI photographs it);
/// any failure writes <c>FAILED &lt;PageName&gt; &lt;reason&gt;</c>, adds a crash-log entry and ends the process with exit code 3.
/// </summary>
internal static class ShowVerifier
{
    private const int TimeoutSeconds = 15;
    private static bool _finished;

    public static bool Active => App.Options.Show is not null;

    /// <summary>Call before the first navigation, so a page that fails while the window is being built is also caught.</summary>
    public static void Attach(Frame frame)
    {
        if (!Active) return;
        DeleteOldResult();
        frame.NavigationFailed += (_, e) =>
        {
            e.Handled = true;
            Fail("navigation to " + e.SourcePageType?.Name + " failed: " + e.Exception);
        };
    }

    /// <summary>Navigates to the page named by --show and starts the checks.</summary>
    public static void Navigate(Frame frame, DispatcherQueue ui)
    {
        var name = App.Options.Show!;
        var type = typeof(ShowVerifier).Assembly.GetTypes().FirstOrDefault(t => t.Name == name && typeof(Page).IsAssignableFrom(t));
        if (type is null) { Fail("no Page class with that name in this assembly"); return; }
        try
        {
            if (frame.CurrentSourcePageType != type) frame.Navigate(type);
        }
        catch (Exception ex)
        {
            Fail("navigation threw: " + ex);
            return;
        }
        if (frame.Content is not Page page || page.GetType() != type) { Fail("the frame does not show the page after navigation (shows " + frame.Content?.GetType().Name + ")"); return; }

        var timer = ui.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(TimeoutSeconds);
        timer.Tick += (_, _) => { timer.Stop(); Fail($"Loaded did not fire within {TimeoutSeconds} s"); };
        timer.Start();

        void Check()
        {
            timer.Stop();
            ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (page.Content is null) { Fail("the page has no content"); return; }
                if (page.ActualWidth < 1 || page.ActualHeight < 1) { Fail($"the page has no size ({page.ActualWidth}x{page.ActualHeight})"); return; }
                Succeed($"{(int)page.ActualWidth}x{(int)page.ActualHeight}");
            });
        }
        if (page.IsLoaded) Check();
        else page.Loaded += (_, _) => Check();
    }

    /// <summary>Called from every place that can see a failure; the first call wins.</summary>
    public static void Fail(string reason)
    {
        if (_finished) return;
        _finished = true;
        App.LogCrash(new InvalidOperationException($"--show {App.Options.Show}: {reason}"));
        WriteResult($"FAILED {App.Options.Show} {reason.Replace('\r', ' ').Replace('\n', ' ')}");
        Environment.Exit(3);
    }

    private static void Succeed(string size)
    {
        if (_finished) return;
        _finished = true;
        WriteResult($"LOADED {App.Options.Show} {size}");
        App.LogInfo($"--show {App.Options.Show}: loaded {size}");
    }

    private static string ResultPath => App.Options.ShowResult ?? Path.Combine(AppContext.BaseDirectory, "show-result.txt");

    private static void DeleteOldResult()
    {
        try { File.Delete(ResultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* writing it later reports the real problem */ }
    }

    private static void WriteResult(string line)
    {
        try { File.WriteAllText(ResultPath, line + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // e.g. Program Files: the default place is not writable. Keep the result where the app's own logs are.
            try { File.WriteAllText(Path.Combine(ViewerSettings.Dir, "logs", "show-result.txt"), line + Environment.NewLine); }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
        }
    }
}
