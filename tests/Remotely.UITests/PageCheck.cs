using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using Remotely.UITests.Imaging;

namespace Remotely.UITests;

internal sealed record Outcome(string Name, bool Passed, string Detail);

/// <summary>
/// Starts the app once per page with <c>--show Page</c> and proves, for each: the process stays alive, no crash log appears, a visible window appears, the UI Automation tree of that
/// window holds real content (and the page, when it can be recognised), and the screen capture is not blank. Then saves a screenshot of that window only.
/// </summary>
internal sealed class PageCheck(AppKind kind, string exe, string outDir, string maskedDir, TimeSpan windowTimeout, TimeSpan settle, bool strictContent)
{
    private static readonly string[] CaptionNames = ["Minimize", "Maximize", "Restore", "Close", "System", "Remotely", "Remotely Server Manager"];

    /// <summary>Text that differs from run to run (addresses, ids, tokens, dates, the machine name) and is painted over in the copy compared with baselines.</summary>
    private static readonly Regex Dynamic = new(
        @"\b\d{1,3}(\.\d{1,3}){3}\b|\b[0-9a-fA-F]{24,}\b|\b[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}\b|\b\d{1,2}[/.]\d{1,2}[/.]\d{2,4}\b|\b\d{1,2}:\d{2}(:\d{2})?\b|" + "Version \\d|Remotely \\d+\\.\\d+",
        RegexOptions.Compiled);

    public List<Outcome> Run(IEnumerable<string> pages)
    {
        var results = new List<Outcome>();
        Directory.CreateDirectory(outDir);
        foreach (var page in pages)
        {
            Outcome outcome;
            try { outcome = One(page); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = new Outcome(page, false, "the check itself failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            Console.WriteLine($"PAGE {(outcome.Passed ? "OK" : "FAIL")}: {page} - {outcome.Detail}");
            results.Add(outcome);
        }
        return results;
    }

    /// <summary>--show with a name that is no page silently shows the first page, so a typo in a list would pass; when the source tree is around, require a XAML file of that name.</summary>
    private static string? UnknownPage(string page)
    {
        string src;
        try { src = Path.Combine(Strings.FindRepoRoot(), "src"); }
        catch (DirectoryNotFoundException) { return null; }
        return Directory.EnumerateFiles(src, page + ".xaml", SearchOption.AllDirectories).Any() ? null : $"no {page}.xaml exists under src (the app would silently show its first page instead)";
    }

    private Outcome One(string page)
    {
        if (UnknownPage(page) is { } unknown) return new Outcome(page, false, unknown);
        AppSession.KillAll(kind);
        kind.ClearCrashLog();
        using var session = AppSession.Start(kind, exe, ["--show", page]);
        var hwnd = session.WaitForMainWindow(windowTimeout, out var why);
        if (hwnd == IntPtr.Zero)
        {
            return new Outcome(page, false, why + CrashText());
        }

        AppSession.BringToFront(hwnd);
        using var uia = new Uia();
        var root = uia.FromHandle(hwnd);

        // The page builds after the window shows; wait for the tree to hold real content, then let animations finish.
        List<UNode> nodes = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string contentNote;
        while (true)
        {
            nodes = uia.Snapshot(root);
            contentNote = Judge(page, nodes, out var found, out var pageSeen);
            if (found && (pageSeen || !strictContent)) break;
            if (session.HasExited) return new Outcome(page, false, $"the process exited with code {session.ExitCode} while the page was loading" + CrashText());
            if (DateTime.UtcNow > deadline) break;
            Thread.Sleep(500);
        }
        Thread.Sleep(settle);
        AppSession.BringToFront(hwnd);
        Thread.Sleep(300);
        nodes = uia.Snapshot(root);
        contentNote = Judge(page, nodes, out var hasContent, out var pageRecognised);

        var folder = outDir;
        Directory.CreateDirectory(Path.Combine(folder, "tree"));
        File.WriteAllText(Path.Combine(folder, "tree", page + ".txt"), Uia.Dump(nodes));

        var problems = new List<string>();
        if (session.HasExited) problems.Add($"the process exited with code {session.ExitCode}");
        if (!hasContent) problems.Add("the window's UI Automation tree has no page content (" + contentNote + ")");
        else if (!pageRecognised && strictContent) problems.Add("the page " + page + " was not found in the UI Automation tree (" + contentNote + ")");
        var crash = CrashText();
        if (crash.Length > 0) problems.Add("crash log not empty" + crash);
        var others = session.OtherWindows(hwnd);
        if (others.Count > 0) problems.Add("an unexpected extra window: " + string.Join("; ", others));

        var dpi = Native.GetDpiForWindow(hwnd);
        string shot = "";
        if (!session.HasExited)
        {
            var image = Capture.Window(hwnd);
            var (rendered, colours) = Capture.LooksRendered(image);
            if (!rendered) problems.Add($"the screenshot looks blank ({colours} distinct colours)");
            var frame = Capture.FrameOf(hwnd);
            var masks = nodes.Where(n => n.Name.Length > 0 && Dynamic.IsMatch(n.Name) && !n.Rect.IsEmpty)
                .Select(n => Rectangle.Inflate(new Rectangle(n.Rect.X - frame.X, n.Rect.Y - frame.Y, n.Rect.Width, n.Rect.Height), 2, 2)).ToList();
            Capture.Save(image.DownscaleToWidth(Capture.MaxWidth), Path.Combine(folder, page + ".png"));
            Capture.Save(Capture.Mask(image, masks).DownscaleToWidth(Capture.MaxWidth), Path.Combine(maskedDir, page + ".png"));
            shot = $"{image.Width}x{image.Height}px, {masks.Count} dynamic text region(s) masked in the baseline copy";
        }
        var named = nodes.Count(n => n.Name.Length > 0);
        var detail = $"window dpi {dpi} ({dpi * 100 / 96}%), {nodes.Count} UIA elements ({named} named), {contentNote}; {shot}";
        return problems.Count == 0 ? new Outcome(page, true, detail) : new Outcome(page, false, string.Join(" | ", problems) + " [" + detail + "]");
    }

    /// <summary>
    /// Decides whether the tree has content. "found" needs at least three named elements that are not window chrome. "pageSeen" is true when an element's class name or automation id is
    /// the page's class name; a build whose UI Automation tree does not expose that is reported, and only fails the run when strict page matching is on.
    /// </summary>
    private static string Judge(string page, IReadOnlyList<UNode> nodes, out bool found, out bool pageSeen)
    {
        var content = nodes.Where(n => n.Depth > 0 && n.Name.Length > 0 && !CaptionNames.Contains(n.Name) && !n.Offscreen).ToList();
        found = content.Count >= 3;
        var hit = nodes.FirstOrDefault(n => n.Class == page || n.Class.EndsWith("." + page, StringComparison.Ordinal) || n.Id == page);
        pageSeen = hit is not null;
        return pageSeen ? $"page found as {hit!.Type} class=\"{hit.Class}\" id=\"{hit.Id}\"" : $"{content.Count} content elements, page class name not exposed in the tree";
    }

    private string CrashText()
    {
        var text = kind.ReadCrashLog();
        if (text.Length == 0) return "";
        var sb = new StringBuilder(" -- ").Append(Path.GetFileName(kind.CrashLog)).Append(": ");
        sb.Append(text.Length > 900 ? text[..900] + "..." : text);
        return sb.ToString().Replace("\r", " ").Replace("\n", " ");
    }
}
