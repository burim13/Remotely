using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Remotely.Tests;

internal sealed record LiteralFinding(string File, int Line, string Kind, string Text)
{
    public override string ToString() => $"{File}:{Line}  [{Kind}]  \"{Text}\"";
}

/// <summary>
/// Finds user-visible text that is written into the XAML or the code instead of being looked up from Resources.resw.
/// XAML: attributes that carry text (Text, Content, Header, PlaceholderText, ToolTipService.ToolTip, AutomationProperties.Name and friends) and text nodes.
/// Code: assignments to those properties, and calls that put text in front of the user (SetName, SetToolTip, toasts, banners, dialogs, helper builders).
/// A literal counts as visible when it contains a letter. Strings shaped like resource keys (Setup_Copy) are not text.
/// </summary>
internal static class LocalizationScanner
{
    private static readonly HashSet<string> XamlAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "PlaceholderText", "ToolTipService.ToolTip", "AutomationProperties.Name", "AutomationProperties.HelpText",
        "AutomationProperties.LocalizedLandmarkType", "AutomationProperties.FullDescription", "AutomationProperties.ItemStatus",
        "Title", "Subtitle", "Label", "Description", "Message", "OnContent", "OffContent", "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText",
        "ErrorMessage", "DismissLabel", "DeviceName", "Detail", "StatusText", "PaneTitle", "ToolTip",
    };

    /// <summary>Properties of controls that hold text, assigned in code.</summary>
    private static readonly HashSet<string> CodeProperties = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "Message", "PlaceholderText", "Label", "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText",
        "OnContent", "OffContent", "Description", "Subtitle", "ErrorMessage", "DismissLabel", "StatusText", "DeviceName", "Detail", "ToolTip",
    };

    /// <summary>Methods whose string arguments are shown to the user, with the argument positions that hold text (null: any argument that contains a space).</summary>
    private static readonly Dictionary<string, int[]?> Sinks = new(StringComparer.Ordinal)
    {
        ["SetName"] = [1], ["SetToolTip"] = [1], ["SetHelpText"] = [1], ["SetFullDescription"] = [1],
        ["Toast"] = [0, 1], ["TryShowToast"] = [0, 1], ["ShowNotice"] = [1], ["ShowError"] = [0, 1],
        ["ConfirmAsync"] = [0, 1, 2, 3], ["ShowErrorAsync"] = [0, 1, 2],
        ["Begin"] = [0], ["End"] = [0],
        ["SmallButton"] = [0], ["ActionButton"] = [0], ["Body"] = [0], ["Heading"] = [0], ["Secondary"] = [0],
        ["MessageDialog"] = [0, 1],
        ["Show"] = null,
    };

    /// <summary>Calls that turn a key into text: their string argument is a key, not text.</summary>
    private static readonly HashSet<string> LookupCalls = new(StringComparer.Ordinal) { "Get", "T", "F", "Format", "Text", "Label", "GetString", "Lookup", "ToString", "Parse", "TryParse" };

    private static readonly Regex KeyShape = new(@"^[A-Za-z][A-Za-z0-9]*(_[A-Za-z0-9]*)+$", RegexOptions.Compiled);

    public static bool LooksVisible(string text)
    {
        var t = text.Trim();
        // One letter is a format specifier ("g", "d") or a symbol, not a sentence.
        return t.Count(char.IsLetter) >= 2 && !KeyShape.IsMatch(t);
    }

    /// <summary>A sentence: two words of letters, or a label ending in an ellipsis. Used for the sweep over code that no sink or property rule covers.</summary>
    private static readonly Regex Sentence = new(@"\p{L}{2,}[\s\u00A0]+\p{L}{2,}|\p{L}{2,}…", RegexOptions.Compiled);

    /// <summary>Lines whose strings are for logs, exceptions and tests of the code itself, not for the user.</summary>
    private static readonly Regex NotForUsers = new(@"\bthrow\b|Exception\s*\(|\bLog\w*\s*\(|Debug\.|Trace\.|WriteLine|AppendAllText|\bRequire\s*\(|\bWaitAsync\s*\(|\bProblems?\.Add|problems\.Add|\bAssert\b|FontFamily|\[(Obsolete|DllImport)", RegexOptions.Compiled);

    public static List<LiteralFinding> ScanXaml(string path)
    {
        var rel = LocRepo.Relative(path);
        var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
        var found = new List<LiteralFinding>();
        foreach (var element in doc.Descendants())
        {
            var local = element.Name.LocalName;
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;
                if (attribute.IsNamespaceDeclaration || !XamlAttributes.Contains(name)) continue;
                // Window.Title and friends are plain attributes; "Setter Property=Text Value=..." is handled below.
                var value = attribute.Value;
                if (value.StartsWith('{')) continue;
                if (LooksVisible(value)) found.Add(new LiteralFinding(rel, Line(attribute), "xaml:" + name, value));
            }
            // Text written between the tags (<Button>Save</Button>, <TextBlock>Hello</TextBlock>).
            if (!local.Contains('.'))
                foreach (var node in element.Nodes().OfType<XText>())
                    if (LooksVisible(node.Value)) found.Add(new LiteralFinding(rel, (node as IXmlLineInfo)?.LineNumber ?? Line(element), "xaml:text-node", node.Value.Trim()));
            if (local == "Setter" && XamlAttributes.Contains((string?)element.Attribute("Property") ?? "") && element.Attribute("Value") is { } sv && !sv.Value.StartsWith('{') && LooksVisible(sv.Value))
                found.Add(new LiteralFinding(rel, Line(sv), "xaml:Setter", sv.Value));
        }
        return found;
    }

    private static int Line(XObject node) => (node as IXmlLineInfo)?.LineNumber ?? 0;

    public static List<LiteralFinding> ScanCode(string path)
    {
        var rel = LocRepo.Relative(path);
        var tokens = CSharpTokens.Lex(File.ReadAllText(path));
        var found = new List<LiteralFinding>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.Ident) continue;

            // Control.Text = "..." / new TextBlock { Text = "..." }
            if (CodeProperties.Contains(token.Value) && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Punct && tokens[i + 1].Value == "="
                && !IsDeclaration(tokens, i))
            {
                foreach (var literal in LiteralsUntilEndOfExpression(tokens, i + 2))
                    if (LooksVisible(literal.Value)) found.Add(new LiteralFinding(rel, literal.Line, "code:" + token.Value + " =", literal.Value));
                continue;
            }

            // SetName(x, "..."), Toast("...", "..."), ...
            if (Sinks.TryGetValue(token.Value, out var positions) && i + 1 < tokens.Count && tokens[i + 1].Value == "(" && tokens[i + 1].Kind == TokenKind.Punct
                && !(i > 0 && tokens[i - 1].Kind == TokenKind.Ident && tokens[i - 1].Value is "void" or "string" or "Task" or "bool" or "static"))
            {
                var args = SplitArguments(tokens, i + 2);
                for (var a = 0; a < args.Count; a++)
                {
                    if (positions is not null && !positions.Contains(a)) continue;
                    foreach (var literal in DirectLiterals(args[a]))
                    {
                        if (!LooksVisible(literal.Value)) continue;
                        if (positions is null && !literal.Value.Contains(' ') && !literal.Value.EndsWith('…')) continue;
                        found.Add(new LiteralFinding(rel, literal.Line, "code:" + token.Value + "()", literal.Value));
                    }
                }
            }
        }

        // Sweep: any other sentence in the code is probably shown to someone (return values, switch arms, helper calls).
        var lines = File.ReadAllLines(path);
        foreach (var t in tokens)
        {
            if (t.Kind != TokenKind.String || !Sentence.IsMatch(t.Value) || KeyShape.IsMatch(t.Value.Trim()) || t.Value.TrimStart().StartsWith('<')) continue;
            if (found.Any(f => f.Line == t.Line && f.Text == t.Value)) continue;
            if (t.Line - 1 < lines.Length && NotForUsers.IsMatch(lines[t.Line - 1])) continue;
            found.Add(new LiteralFinding(rel, t.Line, "code:sentence", t.Value));
        }
        return found;
    }

    /// <summary>True for <c>public string Text =&gt; ...</c>-style declarations and <c>string Text = "..."</c> field initialisers, which are not control properties.</summary>
    private static bool IsDeclaration(List<CsToken> tokens, int i) =>
        i > 0 && tokens[i - 1].Kind == TokenKind.Ident && tokens[i - 1].Value is "string" or "var" or "const" or "readonly" or "static" or "object";

    private static List<CsToken> LiteralsUntilEndOfExpression(List<CsToken> tokens, int start)
    {
        var result = new List<CsToken>();
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Punct)
            {
                if (t.Value is "(" or "[" or "{") depth++;
                else if (t.Value is ")" or "]" or "}") { depth--; if (depth < 0) break; }
                else if (depth == 0 && t.Value is ";" or ",") break;
            }
            if (t.Kind == TokenKind.String && !IsKeyArgument(tokens, i)) result.Add(t);
        }
        return result;
    }

    /// <summary>A string that is the first argument of Get/T/F/Format is a resource key.</summary>
    private static bool IsKeyArgument(List<CsToken> tokens, int i) =>
        i >= 2 && tokens[i - 1].Value == "(" && tokens[i - 2].Kind == TokenKind.Ident && LookupCalls.Contains(tokens[i - 2].Value);

    private static List<List<CsToken>> SplitArguments(List<CsToken> tokens, int start)
    {
        var args = new List<List<CsToken>> { new() };
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Punct)
            {
                if (t.Value is "(" or "[" or "{") depth++;
                else if (t.Value is ")" or "]" or "}") { if (depth == 0) break; depth--; }
                else if (depth == 0 && t.Value == ",") { args.Add([]); continue; }
                else if (depth == 0 && t.Value == ";") break;
            }
            args[^1].Add(t);
        }
        return args;
    }

    /// <summary>String tokens of an argument that are not inside a nested call (a nested Loc.Get("key") is a lookup, not text).</summary>
    private static IEnumerable<CsToken> DirectLiterals(List<CsToken> argument)
    {
        var depth = 0;
        foreach (var t in argument)
        {
            if (t.Kind == TokenKind.Punct)
            {
                if (t.Value is "(" or "[" or "{") depth++;
                else if (t.Value is ")" or "]" or "}") depth--;
            }
            else if (t.Kind == TokenKind.String && depth == 0) yield return t;
        }
    }
}
