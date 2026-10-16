using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Remotely.Tests;

/// <summary>Checks the design tokens (Remotely.UI/Themes) with plain XML parsing, so they run on any machine and fail the build when a colour pair becomes unreadable.</summary>
public class DesignTokenTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Remotely.slnx not found above " + AppContext.BaseDirectory);
        }
    }

    private static string Themes => Path.Combine(Root, "src", "Remotely.UI", "Themes");

    /// <summary>theme name ("Default", "Light", "HighContrast") to (brush key to colour text).</summary>
    private static Dictionary<string, Dictionary<string, string>> Load(string file)
    {
        var doc = XDocument.Load(Path.Combine(Themes, file));
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var theme in doc.Descendants(Xaml + "ResourceDictionary.ThemeDictionaries").Elements(Xaml + "ResourceDictionary"))
        {
            var map = new Dictionary<string, string>();
            foreach (var brush in theme.Elements(Xaml + "SolidColorBrush"))
                map[(string)brush.Attribute(X + "Key")!] = (string)brush.Attribute("Color")!;
            result[(string)theme.Attribute(X + "Key")!] = map;
        }
        return result;
    }

    private static (double R, double G, double B) Rgb(string hex)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        double Channel(int i) => int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return (Channel(0), Channel(1), Channel(2));
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        static double Lin(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    /// <summary>WCAG 2.x contrast ratio.</summary>
    internal static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static readonly string[] Backgrounds = ["Portico.Surface", "Portico.Card", "Portico.CardRaised", "Portico.Field"];
    private static readonly string[] TextOnBackgrounds =
    [
        "Portico.Text", "Portico.TextSecondary", "Portico.TextTertiary", "Portico.AccentText",
        "Portico.Connected", "Portico.Error", "Portico.Warning", "Portico.Info",
    ];

    public static IEnumerable<object[]> Themes_() => [["Default"], ["Light"]];

    [Theory, MemberData(nameof(Themes_))]
    public void Text_tokens_are_readable_on_every_background(string theme)
    {
        var t = Load("Tokens.xaml")[theme];
        var failures = new List<string>();
        foreach (var fg in TextOnBackgrounds)
            foreach (var bg in Backgrounds)
            {
                var ratio = Contrast(t[fg], t[bg]);
                if (ratio < 4.5) failures.Add($"{theme}: {fg} {t[fg]} on {bg} {t[bg]} = {ratio:0.00}");
            }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory, MemberData(nameof(Themes_))]
    public void Status_text_is_readable_on_its_own_surface(string theme)
    {
        var t = Load("Tokens.xaml")[theme];
        var failures = new List<string>();
        foreach (var kind in new[] { "Connected", "Error", "Warning", "Info" })
        {
            var ratio = Contrast(t["Portico." + kind], t["Portico." + kind + "Surface"]);
            if (ratio < 4.5) failures.Add($"{theme}: {kind} on {kind}Surface = {ratio:0.00}");
        }
        var neutral = Contrast(t["Portico.NeutralText"], t["Portico.NeutralSurface"]);
        if (neutral < 4.5) failures.Add($"{theme}: NeutralText on NeutralSurface = {neutral:0.00}");
        // Body text sits on banners too.
        foreach (var surface in new[] { "ConnectedSurface", "ErrorSurface", "WarningSurface", "InfoSurface", "NeutralSurface" })
        {
            var ratio = Contrast(t["Portico.Text"], t["Portico." + surface]);
            if (ratio < 4.5) failures.Add($"{theme}: Text on {surface} = {ratio:0.00}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory, MemberData(nameof(Themes_))]
    public void Accent_button_text_is_readable_in_every_state(string theme)
    {
        var t = Load("Tokens.xaml")[theme];
        foreach (var fill in new[] { "Accent", "AccentHover", "AccentPressed" })
            Assert.True(Contrast(t["Portico.OnAccent"], t["Portico." + fill]) >= 4.5, $"{theme}: OnAccent on {fill}");
    }

    [Theory, MemberData(nameof(Themes_))]
    public void Portico_theme_text_pairs_are_readable(string theme)
    {
        var p = Load("Portico.xaml")[theme];
        var failures = new List<string>();
        foreach (var fg in new[] { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "AccentTextFillColorPrimaryBrush" })
            foreach (var bg in new[] { "ApplicationPageBackgroundThemeBrush", "CardBackgroundFillColorDefaultBrush" })
            {
                var ratio = Contrast(p[fg], p[bg]);
                if (ratio < 4.5) failures.Add($"{theme}: {fg} on {bg} = {ratio:0.00}");
            }
        foreach (var bg in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed" })
        {
            var ratio = Contrast(p["AccentButtonForeground"], p[bg]);
            if (ratio < 4.5) failures.Add($"{theme}: AccentButtonForeground on {bg} = {ratio:0.00}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory, MemberData(nameof(Themes_))]
    public void Portico_keys_agree_with_the_tokens(string theme)
    {
        var t = Load("Tokens.xaml")[theme];
        var p = Load("Portico.xaml")[theme];
        Assert.Equal(t["Portico.Surface"], p["ApplicationPageBackgroundThemeBrush"]);
        Assert.Equal(t["Portico.Card"], p["CardBackgroundFillColorDefaultBrush"]);
        Assert.Equal(t["Portico.Text"], p["TextFillColorPrimaryBrush"]);
        Assert.Equal(t["Portico.TextSecondary"], p["TextFillColorSecondaryBrush"]);
        Assert.Equal(t["Portico.TextTertiary"], p["TextFillColorTertiaryBrush"]);
        Assert.Equal(t["Portico.AccentText"], p["AccentTextFillColorPrimaryBrush"]);
        Assert.Equal(t["Portico.OnAccent"], p["AccentButtonForeground"]);
        Assert.Equal(t["Portico.Accent"], p["AccentButtonBackground"]);
    }

    [Fact]
    public void Every_theme_defines_the_same_keys()
    {
        foreach (var file in new[] { "Tokens.xaml", "Portico.xaml" })
        {
            var themes = Load(file);
            Assert.Equal(new[] { "Default", "HighContrast", "Light" }, themes.Keys.Order().ToArray());
            var reference = themes["Default"].Keys.Order().ToArray();
            foreach (var (name, map) in themes)
                Assert.True(reference.SequenceEqual(map.Keys.Order()), $"{file}: {name} has different keys than Default");
        }
    }

    [Fact]
    public void Spacing_scale_is_4_8_12_16_24_32()
    {
        var doc = XDocument.Load(Path.Combine(Themes, "Tokens.xaml"));
        var spaces = doc.Root!.Elements(X + "Double").Where(e => ((string)e.Attribute(X + "Key")!).StartsWith("Portico.Space", StringComparison.Ordinal))
            .Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).Order().ToArray();
        Assert.Equal(new double[] { 4, 8, 12, 16, 24, 32 }, spaces);
    }

    [Fact]
    public void No_colours_are_hard_coded_outside_the_theme_files()
    {
        var folders = new[] { Path.Combine(Root, "src", "Remotely.UI"), Path.Combine(Root, "src", "Remotely.App", "Views", "Gallery") };
        var hex = new Regex(@"#[0-9A-Fa-f]{6}\b|#[0-9A-Fa-f]{8}\b");
        var code = new Regex(@"Color\.From|Colors\.|new SolidColorBrush|new Color\b");
        var literal = new Regex(@"\b(Foreground|Background|Fill|Stroke|BorderBrush)=""(?!\{)(?!Transparent"")");
        var offenders = new List<string>();
        foreach (var folder in folders)
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains(Path.DirectorySeparatorChar + "Themes" + Path.DirectorySeparatorChar)
                                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            {
                var text = File.ReadAllText(file);
                if (hex.IsMatch(text) || code.IsMatch(text) || literal.IsMatch(text)) offenders.Add(Path.GetRelativePath(Root, file));
            }
        Assert.True(offenders.Count == 0, "Hard-coded colours in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Icon_paths_are_well_formed()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "Remotely.UI", "Icons.cs"));
        var entries = Regex.Matches(text, @"\[""(?<name>\w+)""\] = ""(?<path>[^""]*)""").Select(m => (m.Groups["name"].Value, m.Groups["path"].Value)).ToList();
        Assert.True(entries.Count >= 30, $"only {entries.Count} icons");
        Assert.Equal(entries.Count, entries.Select(e => e.Item1).Distinct().Count());
        foreach (var (name, path) in entries)
        {
            Assert.Matches(@"^(F1 )?M[MmLlHhVvAaCcZz0-9., \-]*z$", path);
            Assert.True(Regex.Matches(path, "[Mm]").Count == Regex.Matches(path, "[Zz]").Count, $"{name}: every sub-path must be closed");
        }
    }

    [Fact]
    public void Icon_files_cover_16_to_256_pixels()
    {
        var assets = Path.Combine(Root, "src", "Remotely.App", "Assets");
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, IcoSizes(Path.Combine(assets, "remotely.ico")));
        foreach (var tray in new[] { "tray-light.ico", "tray-dark.ico" })
            Assert.Contains(16, IcoSizes(Path.Combine(assets, tray)));
        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
            Assert.True(File.Exists(Path.Combine(assets, $"remotely-{size}.png")), $"remotely-{size}.png");
    }

    private static int[] IcoSizes(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.Equal(1, BitConverter.ToUInt16(b, 2));   // type: icon
        var count = BitConverter.ToUInt16(b, 4);
        return Enumerable.Range(0, count).Select(i => b[6 + 16 * i] == 0 ? 256 : (int)b[6 + 16 * i]).Order().ToArray();
    }
}
