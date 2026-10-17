using System.Text;

namespace Remotely.Tests;

internal enum TokenKind { Ident, String, Punct, Other }

/// <summary>One token of a C# source file. For a string token, <see cref="Value"/> is the text between the quotes with escapes left as written and interpolation holes removed.</summary>
internal readonly record struct CsToken(TokenKind Kind, string Value, int Line, int Offset);

/// <summary>
/// A small C# lexer, enough to find string literals and the calls around them without being fooled by comments. It is not a parser: it does not know types.
/// Handles // and /* */ comments, "regular", @"verbatim", $"interpolated" (nested holes), $@"...", raw """...""" strings and char literals.
/// </summary>
internal static class CSharpTokens
{
    public static List<CsToken> Lex(string source)
    {
        var tokens = new List<CsToken>();
        var i = 0;
        var line = 1;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) { if (source[i] == '\n') line++; i++; }
                i += 2;
                continue;
            }
            var start = i;
            var startLine = line;
            if (TryString(source, ref i, ref line, out var text, out var holes))
            {
                tokens.Add(new CsToken(TokenKind.String, text, startLine, start));
                // Strings inside interpolation holes ($"{(ok ? "yes" : "no")}" or $"{Loc.Get("Key")}") are tokens of their own, after the outer one.
                foreach (var hole in holes)
                    foreach (var inner in Lex(hole).Where(t => t.Kind == TokenKind.String))
                        tokens.Add(inner with { Line = startLine, Offset = start });
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != '\'') { if (source[i] == '\\') i++; i++; }
                i++;
                tokens.Add(new CsToken(TokenKind.Other, "'", startLine, start));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '@' && i + 1 < source.Length && (char.IsLetter(source[i + 1]) || source[i + 1] == '_'))
            {
                i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                tokens.Add(new CsToken(TokenKind.Ident, source[start..i].TrimStart('@'), startLine, start));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '.')) i++;
                tokens.Add(new CsToken(TokenKind.Other, source[start..i], startLine, start));
                continue;
            }
            // '==', '=>', '>=', '<=', '!=' must not read as a plain '='.
            if (c == '=' && i + 1 < source.Length && (source[i + 1] == '=' || source[i + 1] == '>')) { i += 2; tokens.Add(new CsToken(TokenKind.Punct, source.Substring(start, 2), startLine, start)); continue; }
            if ((c == '!' || c == '<' || c == '>') && i + 1 < source.Length && source[i + 1] == '=') { i += 2; tokens.Add(new CsToken(TokenKind.Punct, source.Substring(start, 2), startLine, start)); continue; }
            i++;
            tokens.Add(new CsToken(TokenKind.Punct, c.ToString(), startLine, start));
        }
        return tokens;
    }

    private static bool TryString(string s, ref int i, ref int line, out string text, out List<string> holes)
    {
        text = "";
        holes = [];
        var p = i;
        var interpolated = false;
        var verbatim = false;
        while (p < s.Length && (s[p] == '$' || s[p] == '@'))
        {
            if (s[p] == '$') interpolated = true; else verbatim = true;
            p++;
            if (p - i > 2) return false;
        }
        if (p >= s.Length || s[p] != '"') return false;

        if (!verbatim && p + 2 < s.Length && s[p + 1] == '"' && s[p + 2] == '"')
        {
            var quotes = 0;
            while (p + quotes < s.Length && s[p + quotes] == '"') quotes++;
            var close = new string('"', quotes);
            var end = s.IndexOf(close, p + quotes, StringComparison.Ordinal);
            if (end < 0) end = s.Length;
            text = s[(p + quotes)..end];
            line += text.Count(ch => ch == '\n');
            i = Math.Min(s.Length, end + quotes);
            return true;
        }

        p++;
        var sb = new StringBuilder();
        while (p < s.Length)
        {
            var c = s[p];
            if (c == '\n') line++;
            if (!verbatim && c == '\\' && p + 1 < s.Length) { sb.Append(c).Append(s[p + 1]); p += 2; continue; }
            if (c == '"')
            {
                if (verbatim && p + 1 < s.Length && s[p + 1] == '"') { sb.Append('"'); p += 2; continue; }
                p++;
                break;
            }
            if (interpolated && c == '{')
            {
                if (p + 1 < s.Length && s[p + 1] == '{') { sb.Append('{'); p += 2; continue; }
                var holeEnd = SkipHole(s, p, ref line);
                holes.Add(s[(p + 1)..Math.Max(p + 1, holeEnd - 1)]);
                p = holeEnd;
                continue;
            }
            if (interpolated && c == '}' && p + 1 < s.Length && s[p + 1] == '}') { sb.Append('}'); p += 2; continue; }
            sb.Append(c);
            p++;
        }
        text = sb.ToString();
        i = p;
        return true;
    }

    /// <summary>Skips an interpolation hole, which may itself contain strings and braces.</summary>
    private static int SkipHole(string s, int p, ref int line)
    {
        var depth = 0;
        while (p < s.Length)
        {
            var c = s[p];
            if (c == '\n') line++;
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return p + 1; }
            else if (c == '"') { var q = p; if (TryString(s, ref q, ref line, out _, out _)) { p = q; continue; } }
            p++;
        }
        return p;
    }
}
