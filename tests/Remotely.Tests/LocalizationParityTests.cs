using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Remotely.Tests;

/// <summary>
/// The string tables of both apps: every key exists in every language with the same placeholders, every key the code asks for exists, and unused or untranslated keys are reported.
/// English is the default language; Albanian (sq-AL) is the second; qps-ploc is the generated pseudo-locale (see LocalizationPseudoTests).
/// </summary>
public class LocalizationParityTests(ITestOutputHelper output)
{
    public static TheoryData<string> AppNames => new(LocRepo.Apps);

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Every_key_exists_in_every_language(string app)
    {
        var en = LocRepo.Table(app, LocRepo.English).Keys.ToHashSet();
        foreach (var language in new[] { LocRepo.Albanian, LocRepo.Pseudo })
        {
            var other = LocRepo.Table(app, language).Keys.ToHashSet();
            Assert.True(en.Except(other).Count() == 0, $"{app} {language} is missing: {string.Join(", ", en.Except(other).Order())}");
            Assert.True(other.Except(en).Count() == 0, $"{app} {language} has keys English does not: {string.Join(", ", other.Except(en).Order())}");
        }
    }

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Placeholders_are_identical_in_every_language(string app)
    {
        var en = LocRepo.Table(app, LocRepo.English);
        var problems = new List<string>();
        foreach (var language in new[] { LocRepo.Albanian, LocRepo.Pseudo })
            foreach (var (key, value) in LocRepo.Table(app, language))
                if (en.TryGetValue(key, out var english) && LocRepo.Placeholders(english) != LocRepo.Placeholders(value))
                    problems.Add($"{app} {language} {key}: English has [{LocRepo.Placeholders(english)}], this has [{LocRepo.Placeholders(value)}]");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Values_work_as_format_strings_and_are_not_empty_or_duplicated(string app)
    {
        var problems = new List<string>();
        foreach (var language in new[] { LocRepo.English, LocRepo.Albanian, LocRepo.Pseudo })
        {
            var rows = LocRepo.Load(app, language);
            problems.AddRange(rows.GroupBy(r => r.Key).Where(g => g.Count() > 1).Select(g => $"{app} {language}: key {g.Key} is defined {g.Count()} times"));
            foreach (var (key, value) in rows)
            {
                if (string.IsNullOrWhiteSpace(value)) problems.Add($"{app} {language} {key}: empty value");
                try
                {
                    var count = Regex.Matches(value, @"\{(\d+)").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
                    _ = string.Format(value, new object[count]);
                }
                catch (FormatException ex) { problems.Add($"{app} {language} {key}: not a valid format string ({ex.Message})"); }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Key_names_are_well_formed(string app)
    {
        var bad = LocRepo.Load(app, LocRepo.English).Select(r => r.Key).Where(k => !Regex.IsMatch(k, "^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$|^App_Name$")).ToList();
        Assert.True(bad.Count == 0, $"{app}: keys should look like Area_Name: {string.Join(", ", bad)}");
    }

    // ---- the code against the tables ----

    /// <summary>Keys the sources ask for, and the prefixes they build keys from ("History_Outcome_" + outcome).</summary>
    internal static (HashSet<string> Literals, HashSet<string> Prefixes, HashSet<string> Suffixes) KeyLiterals(params string[] projects)
    {
        var literals = new HashSet<string>();
        var prefixes = new HashSet<string>();
        var suffixes = new HashSet<string>();
        var shape = new Regex("^[A-Z][a-z][A-Za-z0-9]*(_[A-Za-z0-9]*)+$");
        void Take(string text)
        {
            if (Regex.IsMatch(text, "^_[A-Z][A-Za-z0-9]*$")) suffixes.Add(text);   // T(errorKey + "_Help")
            if (!shape.IsMatch(text)) return;
            if (text.EndsWith('_')) prefixes.Add(text); else literals.Add(text);
        }
        foreach (var file in LocRepo.SourceFiles(".cs", projects))
            foreach (var token in CSharpTokens.Lex(File.ReadAllText(file)).Where(t => t.Kind == TokenKind.String)) Take(token.Value);
        foreach (var file in LocRepo.SourceFiles(".xaml", projects))
            foreach (var attribute in System.Xml.Linq.XDocument.Load(file).Descendants().SelectMany(e => e.Attributes()))
                Take(attribute.Value);
        return (literals, prefixes, suffixes);
    }

    /// <summary>The app, the shared UI project and the libraries that hand resource keys to the UI (state machines, validators, error results).</summary>
    private static string[] ProjectsOf(string app) => [app, "Remotely.UI", "Remotely.Core", "Remotely.Client", "Remotely.HostCore", "Remotely.ViewerCore"];

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Every_key_the_code_asks_for_exists_in_English(string app)
    {
        var keys = LocRepo.Table(app, LocRepo.English).Keys.ToHashSet();
        var families = keys.Select(k => k[..k.IndexOf('_')]).ToHashSet();
        var (literals, _, _) = KeyLiterals(app, "Remotely.UI");
        // A literal that looks like Area_Name and whose Area is a known family of this app but is not defined is a typo or a forgotten string.
        var missing = literals.Where(l => families.Contains(l[..l.IndexOf('_')]) && !keys.Contains(l)).Order().ToList();
        Assert.True(missing.Count == 0, $"{app}: the code uses these resource keys but Resources.resw (en-US) does not define them: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_key_the_libraries_hand_to_the_apps_exists_in_one_of_them()
    {
        var all = LocRepo.Apps.SelectMany(a => LocRepo.Table(a, LocRepo.English).Keys).ToHashSet();
        var families = all.Select(k => k[..k.IndexOf('_')]).ToHashSet();
        var (literals, _, _) = KeyLiterals("Remotely.Core", "Remotely.Client", "Remotely.HostCore", "Remotely.ViewerCore");
        // Keys built by interpolation (for example $"Settings_{Id}_Label") show up here as "Settings__Label"; SettingsTests checks those per setting.
        var missing = literals.Where(l => !l.Contains("__") && families.Contains(l[..l.IndexOf('_')]) && !all.Contains(l)).Order().ToList();
        Assert.True(missing.Count == 0, "Library code returns these resource keys but neither app defines them: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Unused_keys_are_reported(string app)
    {
        var keys = LocRepo.Table(app, LocRepo.English).Keys;
        var (literals, prefixes, suffixes) = KeyLiterals(ProjectsOf(app));
        var unused = keys.Where(k => !literals.Contains(k) && !prefixes.Any(k.StartsWith) && !suffixes.Any(x => k.EndsWith(x) && literals.Contains(k[..^x.Length]))).Order().ToList();
        output.WriteLine($"{app}: {keys.Count} keys, {unused.Count} not referenced by any string literal or key prefix in the sources:");
        foreach (var key in unused) output.WriteLine("  " + key);
        // Informational on purpose: an agent may add a key a commit before the code that uses it. The list is printed in the test output and in tools/loc/report.py.
        Assert.NotNull(unused);
    }

    [Theory]
    [MemberData(nameof(AppNames))]
    public void Albanian_strings_equal_to_English_are_reported(string app)
    {
        var en = LocRepo.Table(app, LocRepo.English);
        var same = LocRepo.Table(app, LocRepo.Albanian).Where(p => en.TryGetValue(p.Key, out var e) && e == p.Value && Regex.Replace(p.Value, @"\{[^}]*\}", "").Count(char.IsLetter) >= 2).Select(p => p.Key).Order().ToList();
        output.WriteLine($"{app}: {same.Count} Albanian values with letters are identical to English (brand names and units are fine; anything else is untranslated):");
        foreach (var key in same) output.WriteLine($"  {key} = {en[key]}");
        Assert.NotNull(same);
    }
}
