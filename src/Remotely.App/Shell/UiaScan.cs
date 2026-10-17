using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Remotely.App.Shell;

/// <summary>
/// Run-time accessibility scan (<c>Remotely.exe --uia-scan RESULT [PAGES]</c>, see docs/cli.md). Opens each page in a plain window, walks the visual tree and asks each element's
/// automation peer, the object UI Automation clients such as Narrator talk to, for its control type and name. Every interactive control whose name is empty is reported.
/// Limits, so nobody reads more into a PASS than it says: only elements that exist after the page has loaded (no content that appears later, no open dialogs or flyouts, only the
/// rows a virtualised list has realised), only Visibility.Visible subtrees, and "has a name" means non-empty, not "reads well".
/// </summary>
internal static class UiaScan
{
    internal sealed record Unnamed(string Page, string ControlType, string ClassName, string ElementName);

    private static readonly HashSet<AutomationControlType> InteractiveTypes =
    [
        AutomationControlType.Button, AutomationControlType.CheckBox, AutomationControlType.ComboBox, AutomationControlType.Edit, AutomationControlType.Hyperlink,
        AutomationControlType.ListItem, AutomationControlType.MenuItem, AutomationControlType.RadioButton, AutomationControlType.Slider, AutomationControlType.Spinner,
        AutomationControlType.SplitButton, AutomationControlType.TabItem, AutomationControlType.TreeItem, AutomationControlType.DataItem,
    ];

    /// <summary>Page class names from a list file (one short name per line, # comments), or every concrete page class of this assembly when no file is given.</summary>
    internal static IReadOnlyList<Type> ResolvePages(string? pagesFile)
    {
        var all = typeof(UiaScan).Assembly.GetTypes().Where(t => typeof(Page).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null).ToList();
        if (pagesFile is null) return all.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var result = new List<Type>();
        foreach (var raw in File.ReadAllLines(pagesFile))
        {
            var name = raw.Trim();
            if (name.Length == 0 || name[0] == '#') continue;
            result.Add(all.FirstOrDefault(t => t.Name == name) ?? throw new InvalidOperationException($"No page class named {name} in {pagesFile}"));
        }
        return result;
    }

    /// <summary>Walks the tree under <paramref name="root"/> and returns the interactive elements without an accessible name. Must run on the UI thread, after layout.</summary>
    internal static List<Unnamed> Scan(DependencyObject root, string page)
    {
        var found = new List<Unnamed>();
        Walk(root, page, found);
        return found;
    }

    private static void Walk(DependencyObject node, string page, List<Unnamed> found)
    {
        if (node is UIElement { Visibility: Visibility.Collapsed }) return;
        if (node is FrameworkElement element)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
            if (peer is not null && peer.IsControlElement() && InteractiveTypes.Contains(peer.GetAutomationControlType()) && string.IsNullOrWhiteSpace(peer.GetName()))
                found.Add(new Unnamed(page, peer.GetAutomationControlType().ToString(), element.GetType().Name, element.Name));
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i), page, found);
    }

    internal static string Format(IReadOnlyCollection<Unnamed> unnamed, IReadOnlyCollection<string> errors, int pages)
    {
        var sb = new StringBuilder();
        sb.AppendLine(unnamed.Count == 0 && errors.Count == 0 ? $"PASS pages={pages}" : $"FAIL pages={pages} unnamed={unnamed.Count} errors={errors.Count}");
        foreach (var u in unnamed) sb.AppendLine($"UNNAMED {u.Page} {u.ControlType} {u.ClassName} x:Name={(u.ElementName.Length == 0 ? "(none)" : u.ElementName)}");
        foreach (var e in errors) sb.AppendLine("ERROR " + e);
        return sb.ToString();
    }

    /// <summary>Opens each page in its own window, scans it, writes the result file and returns the process exit code (0 when nothing is unnamed and every page opened).</summary>
    internal static async Task<int> RunAsync(string resultFile, string? pagesFile)
    {
        var unnamed = new List<Unnamed>();
        var errors = new List<string>();
        var pageCount = 0;
        try
        {
            var pages = ResolvePages(pagesFile);
            var frame = new Frame();
            string? navigationError = null;
            frame.NavigationFailed += (_, e) => { navigationError = e.Exception?.Message ?? "navigation failed"; e.Handled = true; };
            var window = new Window { Content = frame, Title = "Remotely UIA scan" };
            App.ApplyTheme(window);
            window.Activate();
            foreach (var type in pages)
            {
                navigationError = null;
                try
                {
                    frame.Navigate(type);
                    await Task.Delay(1500);   // the page's Loaded handlers and first refresh
                    if (navigationError is not null) { errors.Add($"{type.Name}: {navigationError}"); continue; }
                    if (frame.Content is FrameworkElement page)
                    {
                        page.UpdateLayout();
                        unnamed.AddRange(Scan(page, type.Name));
                        pageCount++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{type.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            window.Close();
        }
        catch (Exception ex)
        {
            errors.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        var text = Format(unnamed, errors, pageCount);
        try { await File.WriteAllTextAsync(resultFile, text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogCrash(ex); return 2; }
        App.LogInfo("uia-scan: " + text.Split('\n')[0].Trim());
        return unnamed.Count == 0 && errors.Count == 0 ? 0 : 1;
    }
}
