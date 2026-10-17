using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Remotely.Tests;

internal sealed record A11yFinding(string File, int Line, string Rule, string Text)
{
    public override string ToString() => $"{File}:{Line}  [{Rule}]  {Text}";
}

/// <summary>
/// A static check over the XAML and the code-behind. It cannot see what Narrator will say (the runtime scan, UiaScan, does that); it finds the mistakes that are visible in the source:
/// interactive controls with no name, switched-off focus visuals, positive TabIndex, mouse-only handlers, hard-coded colours (which ignore high contrast),
/// text that cannot scale, and animations that do not go through Motion (which respects the Windows animation setting).
/// </summary>
internal static class AccessibilityAnalyzer
{
    private static readonly HashSet<string> Interactive = new(StringComparer.Ordinal)
    {
        "Button", "AppBarButton", "AppBarToggleButton", "ToggleButton", "HyperlinkButton", "RepeatButton", "DropDownButton", "SplitButton", "ToggleSplitButton",
        "CheckBox", "RadioButton", "RadioButtons", "ToggleSwitch", "TextBox", "PasswordBox", "RichEditBox", "AutoSuggestBox", "ComboBox", "Slider", "NumberBox",
        "ListView", "GridView", "Expander", "MenuFlyoutItem", "ToggleMenuFlyoutItem", "RadioMenuFlyoutItem", "NavigationViewItem", "CalendarDatePicker", "DatePicker",
        "TimePicker", "ColorPicker", "TabViewItem", "TokenBox",
    };

    /// <summary>Elements that say what they are with text of their own.</summary>
    private static readonly string[] NameAttributes = ["AutomationProperties.Name", "AutomationProperties.LabeledBy", "Content", "Label", "Header", "Text", "Title"];

    private static readonly HashSet<string> TextHolders = new(StringComparer.Ordinal) { "TextBlock", "Run", "TextBox", "Button", "ComboBox" };

    private static readonly HashSet<string> NotFocusable = new(StringComparer.Ordinal) { "Border", "Grid", "StackPanel", "Canvas", "TextBlock", "Image", "Ellipse", "Rectangle", "Path", "FontIcon", "SymbolIcon" };

    private static readonly string[] MouseOnlyEvents = ["Tapped", "DoubleTapped", "RightTapped", "PointerPressed", "PointerReleased", "ManipulationCompleted"];

    private static readonly string[] ColourAttributes = ["Foreground", "Background", "Fill", "Stroke", "BorderBrush", "Color", "SelectionHighlightColor"];

    private static readonly Regex NamedColour = new("^(White|Black|Red|Green|Blue|Yellow|Orange|Gray|Grey|Silver|Gold|Transparent|Purple|Pink|Brown)$", RegexOptions.Compiled);

    public static List<A11yFinding> AnalyzeXaml(string path)
    {
        var rel = LocRepo.Relative(path);
        var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
        var code = PairedCode(path);
        var found = new List<A11yFinding>();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var element in doc.Descendants())
        {
            var type = element.Name.LocalName;
            if (type.Contains('.')) continue;
            if (element.Ancestors().Any(a => a.Name.LocalName is "ControlTemplate" or "Style")) continue;
            var line = (element as IXmlLineInfo)?.LineNumber ?? 0;
            string Id()
            {
                var n = (string?)element.Attribute(x + "Name");
                if (n is not null) return $"{type} x:Name={n}";
                var click = (string?)element.Attribute("Click");
                return click is not null ? $"{type} Click={click}" : $"{type}@{line}";
            }

            if (Interactive.Contains(type) && IsInteractive(element) && !HasName(element, type, code, x))
                found.Add(new A11yFinding(rel, line, "unnamed", Id()));

            if (Interactive.Contains(type))
            {
                if (Is(element, "UseSystemFocusVisuals", "False")) found.Add(new A11yFinding(rel, line, "focus", Id() + " UseSystemFocusVisuals=False"));
                if (Is(element, "IsTabStop", "False")) found.Add(new A11yFinding(rel, line, "focus", Id() + " IsTabStop=False (cannot be reached with Tab)"));
                if (Is(element, "IsEnabled", "False") == false && element.Attribute("FocusVisualPrimaryThickness") is { Value: "0" }) found.Add(new A11yFinding(rel, line, "focus", Id() + " FocusVisualPrimaryThickness=0"));
            }

            if (element.Attribute("TabIndex") is { } tab && int.TryParse(tab.Value, out var index) && index > 0)
                found.Add(new A11yFinding(rel, line, "tabindex", $"{Id()} TabIndex={index} (positive values override the natural order)"));

            if (Is(element, "IsTextScaleFactorEnabled", "False")) found.Add(new A11yFinding(rel, line, "textscale", Id() + " IsTextScaleFactorEnabled=False"));
            if (type is "TextBlock" or "TextBox" or "Button" or "ComboBox" && (element.Attribute("Height") is not null || element.Attribute("MaxHeight") is not null) && !Is(element, "TextTrimming", "None") && element.Attribute("TextTrimming") is null)
                found.Add(new A11yFinding(rel, line, "textscale", Id() + " has a fixed Height/MaxHeight: text at 200% may be clipped"));

            foreach (var ev in MouseOnlyEvents)
                if (element.Attribute(ev) is not null && NotFocusable.Contains(type))
                    found.Add(new A11yFinding(rel, line, "mouse-only", $"{Id()} handles {ev} but cannot take keyboard focus"));

            foreach (var name in ColourAttributes)
                if (element.Attribute(name) is { } c && !c.Value.StartsWith('{') && (c.Value.StartsWith('#') || NamedColour.IsMatch(c.Value)) && c.Value != "Transparent")
                    found.Add(new A11yFinding(rel, line, "colour", $"{Id()} {name}={c.Value} (a fixed colour ignores high contrast)"));

            if (type is "Storyboard" or "DoubleAnimation" or "ColorAnimation" or "DoubleAnimationUsingKeyFrames" or "ObjectAnimationUsingKeyFrames" || type.EndsWith("ThemeTransition", StringComparison.Ordinal) || type.EndsWith("Transition", StringComparison.Ordinal) && type != "Transition")
                found.Add(new A11yFinding(rel, line, "motion", $"{type}: animation in XAML does not follow the Windows animation setting; use Remotely.UI.Motion"));
        }
        return found;
    }

    private static bool Is(XElement e, string attribute, string value) => string.Equals((string?)e.Attribute(attribute), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>A list that only shows things is not interactive; one that selects or reacts to clicks is.</summary>
    private static bool IsInteractive(XElement e)
    {
        var type = e.Name.LocalName;
        if (type is not ("ListView" or "GridView")) return true;
        return (string?)e.Attribute("SelectionMode") is { } mode && mode != "None" || Is(e, "IsItemClickEnabled", "True") || e.Attribute("ItemClick") is not null;
    }

    private static bool HasName(XElement e, string type, string code, XNamespace x)
    {
        foreach (var attribute in NameAttributes)
            if (e.Attribute(attribute) is { } a && a.Value.Length > 0 && !(attribute is "Text" or "Title" && type is not ("MenuFlyoutItem" or "ToggleMenuFlyoutItem" or "RadioMenuFlyoutItem" or "NavigationViewItem"))) return true;
        if (e.Attribute("AutomationProperties.Name") is not null) return true;

        // Text between the tags or in a child that has text.
        foreach (var d in e.Descendants())
        {
            if (d.Name.LocalName.Contains('.')) continue;
            if (d.Name.LocalName is "TextBlock" or "Run" && ((string?)d.Attribute("Text") is { Length: > 0 } || (string?)d.Attribute("Text") is null && d.Attribute(x + "Name") is { } tn && Assigned(code, tn.Value, "Text"))) return true;
            if (d.Attribute("AutomationProperties.Name") is not null) return true;
        }
        if (e.Nodes().OfType<XText>().Any(t => t.Value.Trim().Length > 0)) return true;

        // Named in the code-behind.
        if ((string?)e.Attribute(x + "Name") is { } name)
            foreach (var property in new[] { "Content", "Label", "Header", "Text", "Title" })
                if (Assigned(code, name, property)) return true;
        if ((string?)e.Attribute(x + "Name") is { } n2 && Regex.IsMatch(code, $@"SetName\(\s*{Regex.Escape(n2)}\b|SetLabeledBy\(\s*{Regex.Escape(n2)}\b|Describe\(\s*{Regex.Escape(n2)}\b")) return true;
        return false;
    }

    private static bool Assigned(string code, string name, string property) => Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\.{property}\s*=(?!=)");

    /// <summary>The code-behind of a XAML file: Name.xaml.cs and any Name.Something.cs partial next to it.</summary>
    private static string PairedCode(string xamlPath)
    {
        var dir = Path.GetDirectoryName(xamlPath)!;
        var stem = Path.GetFileName(xamlPath)[..^".xaml".Length];
        return string.Join("\n", Directory.EnumerateFiles(dir, stem + ".*cs").Concat(Directory.EnumerateFiles(dir, stem + ".xaml.cs")).Distinct().Select(File.ReadAllText));
    }

    // ---- code ----

    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "HyperlinkButton", "CheckBox", "RadioButton", "ToggleSwitch", "TextBox", "PasswordBox", "ComboBox", "Slider", "NumberBox", "ListView", "GridView", "AutoSuggestBox",
    };

    private static readonly string[] NamingProperties = ["Content", "Header", "Text", "PlaceholderText"];

    /// <summary>Controls built in code: each needs Content or Header in its initialiser, or a SetName/Header/Content assignment on the variable it is stored in.</summary>
    public static List<A11yFinding> AnalyzeCode(string path)
    {
        var rel = LocRepo.Relative(path);
        var source = File.ReadAllText(path);
        var tokens = CSharpTokens.Lex(source);
        var found = new List<A11yFinding>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Ident && t.Value == "new" && tokens[i + 1].Kind == TokenKind.Ident && InteractiveTypes.Contains(tokens[i + 1].Value))
            {
                var type = tokens[i + 1].Value;
                var j = i + 2;
                var depth = 0;
                var initialiser = new List<CsToken>();
                // skip "()" then read the "{ ... }" initialiser if there is one
                if (j < tokens.Count && tokens[j].Value == "(") { var d = 0; for (; j < tokens.Count; j++) { if (tokens[j].Value == "(") d++; else if (tokens[j].Value == ")" && --d == 0) { j++; break; } } }
                if (j < tokens.Count && tokens[j].Value == "{")
                    for (; j < tokens.Count; j++)
                    {
                        if (tokens[j].Value == "{") depth++;
                        else if (tokens[j].Value == "}" && --depth == 0) break;
                        initialiser.Add(tokens[j]);
                    }
                var named = initialiser.Any(x => x.Kind == TokenKind.Ident && NamingProperties.Contains(x.Value) || x.Value == "AutomationProperties");
                if (!named && i >= 2 && tokens[i - 1].Value == "=" && tokens[i - 2].Kind == TokenKind.Ident)
                {
                    var variable = tokens[i - 2].Value;
                    named = Regex.IsMatch(source, $@"SetName\(\s*{Regex.Escape(variable)}\b|\b{Regex.Escape(variable)}\.(Header|Content)\s*=(?!=)");
                }
                if (!named) found.Add(new A11yFinding(rel, t.Line, "unnamed", $"new {type} has no Content, Header or AutomationProperties name"));
            }

            if (t.Kind == TokenKind.Ident && MouseOnlyEvents.Contains(t.Value) && tokens[i + 1].Value == "+" && i + 2 < tokens.Count && tokens[i + 2].Value == "=")
                found.Add(new A11yFinding(rel, t.Line, "mouse-only", $"{t.Value} += handler in code: check that the same action is available from the keyboard"));

            if (t.Kind == TokenKind.Ident && t.Value is "Storyboard" or "DoubleAnimation" or "ColorAnimation" or "ThemeTransition" && !rel.EndsWith("Remotely.UI/Motion.cs"))
                found.Add(new A11yFinding(rel, t.Line, "motion", $"{t.Value} used outside Remotely.UI/Motion.cs (animations must follow the Windows animation setting)"));

            if (t.Kind == TokenKind.Ident && t.Value == "Color" && i + 2 < tokens.Count && tokens[i + 1].Value == "." && tokens[i + 2].Value == "FromArgb" || t.Kind == TokenKind.Ident && t.Value == "Colors" && i + 2 < tokens.Count && tokens[i + 1].Value == "." && tokens[i + 2].Value is not "Transparent")
                found.Add(new A11yFinding(rel, t.Line, "colour", "hard-coded colour in code (a fixed colour ignores high contrast; use a theme resource or Palette)"));
        }
        return found;
    }
}
