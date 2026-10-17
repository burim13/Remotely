using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Remotely.Tests;

/// <summary>
/// The pseudo-locale (qps-ploc) is generated from en-US by tools/loc/pseudo.py. These tests check that the committed files really are that: derived from the current
/// English text, accented, expanded, and with every placeholder intact. If one fails after an English change, run <c>python3 tools/loc/pseudo.py</c>.
/// </summary>
public class LocalizationPseudoTests
{
    public static TheoryData<string> AppNames => new(LocRepo.Apps);

    private static string StripAccents(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly Regex Shape = new(@"^\[(?<text>.*) ‹(?<filler>[^›]*)›\]$", RegexOptions.Singleline);

    [Theory]
    [MemberData(nameof(AppNames))]
    public void The_pseudo_file_is_derived_from_the_current_English_text(string app)
    {
        var en = LocRepo.Table(app, LocRepo.English);
        var ploc = LocRepo.Table(app, LocRepo.Pseudo);
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ploc.Keys.Order(StringComparer.Ordinal));
        var stale = new List<string>();
        foreach (var (key, english) in en)
        {
            var m = Shape.Match(ploc[key]);
            if (!m.Success) { stale.Add($"{key}: not in the form [text ‹filler›]"); continue; }
            if (StripAccents(m.Groups["text"].Value) != english) stale.Add($"{key}: text no longer matches English \"{english}\"");
        }
        Assert.True(stale.Count == 0, $"{app}: pseudo-locale is out of date, run python3 tools/loc/pseudo.py\n" + string.Join("\n", stale.Take(20)));
    }

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Pseudo_strings_are_accented_and_at_least_forty_percent_longer(string app)
    {
        var en = LocRepo.Table(app, LocRepo.English);
        var ploc = LocRepo.Table(app, LocRepo.Pseudo);
        var problems = new List<string>();
        foreach (var (key, english) in en)
        {
            var pseudo = ploc[key];
            if (pseudo.Length < Math.Ceiling(english.Length * 1.4)) problems.Add($"{key}: {pseudo.Length} characters for {english.Length} in English");
            var lettersOutsidePlaceholders = Regex.Replace(english, @"\{[^{}]*\}", "");
            // Every ASCII letter outside a placeholder must have been accented (q has no accented form in Unicode, so it is the one exception).
            var plainLetters = Regex.Matches(Regex.Replace(pseudo, @"\{[^{}]*\}", ""), "[A-PR-Za-pr-z]").Count;
            if (lettersOutsidePlaceholders.Any(char.IsLetter) && plainLetters > 0) problems.Add($"{key}: contains unaccented letters: {pseudo}");
        }
        Assert.True(problems.Count == 0, app + "\n" + string.Join("\n", problems.Take(20)));
    }

    [Fact]
    public void Placeholders_in_English_survive_untouched_in_the_pseudo_text()
    {
        foreach (var app in LocRepo.Apps)
        {
            var ploc = LocRepo.Table(app, LocRepo.Pseudo);
            foreach (var (key, english) in LocRepo.Table(app, LocRepo.English).Where(p => p.Value.Contains('{')))
                foreach (Match m in Regex.Matches(english, @"\{[^{}]*\}"))
                    Assert.True(ploc[key].Contains(m.Value, StringComparison.Ordinal), $"{app} {key}: placeholder {m.Value} was changed");
        }
    }
}
