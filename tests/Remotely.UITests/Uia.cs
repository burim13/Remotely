using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Remotely.UITests;

/// <summary>One element of the UI Automation tree, read once (the properties are cross-process calls).</summary>
internal sealed record UNode(AutomationElement Element, string Name, string Id, string Class, string Type, Rectangle Rect, bool Offscreen, int Depth);

/// <summary>Reading and driving the UI Automation tree of a window (raw view, so nothing is hidden by a control-view filter).</summary>
internal sealed class Uia : IDisposable
{
    private readonly UIA3Automation _automation = new();

    public AutomationElement FromHandle(IntPtr hwnd) => _automation.FromHandle(hwnd);

    public List<UNode> Snapshot(AutomationElement root, int maxNodes = 3000)
    {
        var list = new List<UNode>();
        var walker = _automation.TreeWalkerFactory.GetRawViewWalker();
        void Walk(AutomationElement element, int depth)
        {
            if (list.Count >= maxNodes || depth > 60) return;
            list.Add(Read(element, depth));
            AutomationElement? child;
            try { child = walker.GetFirstChild(element); }
            catch (Exception ex) when (IsGone(ex)) { return; }
            while (child is not null && list.Count < maxNodes)
            {
                Walk(child, depth + 1);
                try { child = walker.GetNextSibling(child); }
                catch (Exception ex) when (IsGone(ex)) { return; }
            }
        }
        Walk(root, 0);
        return list;
    }

    private static UNode Read(AutomationElement e, int depth)
    {
        string S(Func<string?> f) { try { return f() ?? ""; } catch (Exception ex) when (IsGone(ex)) { return ""; } }
        var rect = Rectangle.Empty;
        var off = false;
        try { rect = e.Properties.BoundingRectangle.ValueOrDefault; } catch (Exception ex) when (IsGone(ex)) { }
        try { off = e.Properties.IsOffscreen.ValueOrDefault; } catch (Exception ex) when (IsGone(ex)) { }
        var type = "";
        try { type = e.Properties.ControlType.ValueOrDefault.ToString(); } catch (Exception ex) when (IsGone(ex)) { }
        return new UNode(e, S(() => e.Properties.Name.ValueOrDefault), S(() => e.Properties.AutomationId.ValueOrDefault), S(() => e.Properties.ClassName.ValueOrDefault), type, rect, off, depth);
    }

    private static bool IsGone(Exception ex) => ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException or TimeoutException or InvalidOperationException;

    public static string Dump(IEnumerable<UNode> nodes)
    {
        var text = new StringBuilder();
        foreach (var n in nodes)
            text.Append(' ', n.Depth * 2).Append(n.Type).Append(" name=\"").Append(n.Name.Replace("\n", "\\n")).Append("\" id=\"").Append(n.Id).Append("\" class=\"").Append(n.Class)
                .Append("\" rect=").Append(n.Rect.IsEmpty ? "-" : $"{n.Rect.X},{n.Rect.Y},{n.Rect.Width}x{n.Rect.Height}").Append(n.Offscreen ? " offscreen" : "").AppendLine();
        return text.ToString();
    }

    /// <summary>First node whose automation id equals one of the ids, or whose name equals one of the names (ids win).</summary>
    public static UNode? Find(IReadOnlyList<UNode> nodes, string[]? ids = null, string[]? names = null, string? type = null)
    {
        bool TypeOk(UNode n) => type is null || n.Type == type;
        if (ids is not null)
            foreach (var id in ids)
                if (nodes.FirstOrDefault(n => n.Id == id && TypeOk(n)) is { } byId) return byId;
        if (names is not null)
            foreach (var name in names)
                if (nodes.FirstOrDefault(n => n.Name == name && TypeOk(n)) is { } byName) return byName;
        return null;
    }

    public static UNode? FindMatching(IReadOnlyList<UNode> nodes, Regex pattern) => nodes.FirstOrDefault(n => n.Name.Length > 0 && pattern.IsMatch(n.Name));

    public static UNode? FindContaining(IReadOnlyList<UNode> nodes, params string[] fragments) =>
        nodes.FirstOrDefault(n => n.Name.Length > 0 && fragments.Any(f => n.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));

    public void Dispose() => _automation.Dispose();
}
