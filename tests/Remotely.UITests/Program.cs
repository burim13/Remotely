using System.Globalization;
using System.Text;
using Remotely.UITests.Imaging;

namespace Remotely.UITests;

/// <summary>
/// Commands (all options are --name value):
///   pages     --exe EXE --list FILE --out DIR [--masked DIR] [--theme dark|light|system] [--manager] [--settle 2] [--timeout 45] [--strict-content true|false] [--scale 100]
///   flows     --exe EXE --out DIR [--masked DIR] [--theme dark] [--repo DIR] [--lang en-US] [--scale 100]
///   diff      --actual DIR --baseline DIR [--diffs DIR] [--report FILE] [--channel 16] [--pixels 24] [--fraction 0.0002]
///   baseline  --actual DIR --baseline DIR
///   system    [--info] [--scale 150] [--res 1920x1080] [--hc on|off]
/// Exit code 0 = everything passed, 1 = a check failed, 2 = a system change was refused or the command line is wrong.
/// </summary>
internal static class Program
{
    private static int Main(string[] argv)
    {
        Native.MakeDpiAware();
        if (argv.Length == 0) { Console.Error.WriteLine("usage: Remotely.UITests pages|flows|diff|baseline|system --option value ... (see Program.cs)"); return 2; }
        var options = ParseOptions(argv.Skip(1));
        try
        {
            return argv[0] switch
            {
                "pages" => Pages(options),
                "flows" => RunFlows(options),
                "diff" => Diff(options),
                "baseline" => Baseline(options),
                "system" => SystemChange(options),
                _ => Fail("unknown command " + argv[0]),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unexpected argument " + list[i]);
            var key = list[i][2..];
            map[key] = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal) ? list[++i] : "true";
        }
        return map;
    }

    private static string Req(Dictionary<string, string> o, string key) => o.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");
    private static string Opt(Dictionary<string, string> o, string key, string fallback) => o.TryGetValue(key, out var v) ? v : fallback;
    private static double Num(Dictionary<string, string> o, string key, double fallback) => o.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static int Pages(Dictionary<string, string> o)
    {
        var kind = o.ContainsKey("manager") ? AppKind.Manager : AppKind.Viewer;
        var exe = Req(o, "exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("the app was not found: " + exe);
        var pages = File.ReadAllLines(Req(o, "list")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var outDir = Req(o, "out");
        var masked = Opt(o, "masked", outDir.TrimEnd('/', '\\') + "-masked");
        Directory.CreateDirectory(masked);
        var scale = (int)Num(o, "scale", Display.CurrentScalePercent() is > 0 and var s ? s : 100);

        var backup = Backup(kind);
        try
        {
            kind.WriteSettings(Opt(o, "theme", "dark"), onboardingDone: true, scale);
            var check = new PageCheck(kind, exe, outDir, masked, TimeSpan.FromSeconds(Num(o, "timeout", 45)), TimeSpan.FromSeconds(Num(o, "settle", 2)),
                strictContent: Opt(o, "strict-content", "false") == "true");
            var results = check.Run(pages);
            WriteResults(Path.Combine(outDir, "results.md"), kind.Name + " pages", results);
            var failed = results.Count(r => !r.Passed);
            Console.WriteLine($"{results.Count - failed} of {results.Count} page(s) passed");
            return failed == 0 ? 0 : 1;
        }
        finally { Restore(kind, backup); AppSession.KillAll(kind); }
    }

    private static int RunFlows(Dictionary<string, string> o)
    {
        var exe = Req(o, "exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("the app was not found: " + exe);
        var repo = Opt(o, "repo", "") is { Length: > 0 } r ? r : Strings.FindRepoRoot();
        var strings = Strings.Load(repo, Opt(o, "lang", "en-US"));
        var outDir = Req(o, "out");
        var masked = Opt(o, "masked", outDir.TrimEnd('/', '\\') + "-masked");
        Directory.CreateDirectory(masked);
        var scale = (int)Num(o, "scale", Display.CurrentScalePercent() is > 0 and var s ? s : 100);
        var backup = Backup(AppKind.Viewer);
        try
        {
            var results = new Flows(exe, outDir, masked, strings, Opt(o, "theme", "dark"), scale).RunAll();
            WriteResults(Path.Combine(outDir, "results.md"), "UI flows", results);
            return results.All(x => x.Passed) ? 0 : 1;
        }
        finally { Restore(AppKind.Viewer, backup); AppSession.KillAll(AppKind.Viewer); }
    }

    private static int Diff(Dictionary<string, string> o)
    {
        var actual = Req(o, "actual");
        var baseline = Req(o, "baseline");
        var diffs = Opt(o, "diffs", "");
        var options = new DiffOptions((int)Num(o, "channel", 16), (int)Num(o, "pixels", 24), Num(o, "fraction", 0.0002));
        if (!Directory.Exists(actual)) throw new DirectoryNotFoundException("no screenshots at " + actual);
        var report = new StringBuilder("| Image | Result |\n|---|---|\n");
        int failed = 0, same = 0, fresh = 0;
        foreach (var file in Directory.EnumerateFiles(actual, "*.png", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(actual, file).Replace('\\', '/');
            var reference = Path.Combine(baseline, rel);
            if (!File.Exists(reference)) { fresh++; report.AppendLine($"| {rel} | NEW, no baseline yet |"); Console.WriteLine($"DIFF NEW: {rel} (no baseline)"); continue; }
            var result = ImageDiff.Compare(Capture.Load(reference), Capture.Load(file), options);
            if (result.Passed) { same++; Console.WriteLine($"DIFF OK: {rel} ({result.Describe()})"); continue; }
            failed++;
            report.AppendLine($"| {rel} | DRIFT: {result.Describe()} |");
            Console.WriteLine($"DIFF DRIFT: {rel}: {result.Describe()}");
            if (diffs.Length > 0 && result.Highlight is not null) Capture.Save(result.Highlight, Path.Combine(diffs, rel));
        }
        var missing = Directory.Exists(baseline)
            ? Directory.EnumerateFiles(baseline, "*.png", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(baseline, f).Replace('\\', '/')).Where(r => !File.Exists(Path.Combine(actual, r))).ToList()
            : [];
        foreach (var m in missing) { report.AppendLine($"| {m} | baseline has no matching screenshot in this run |"); Console.WriteLine($"DIFF MISSING: {m}"); }
        var summary = $"{same} unchanged, {failed} drifted, {fresh} new (no baseline), {missing.Count} baseline image(s) without a screenshot";
        Console.WriteLine(summary);
        if (Opt(o, "report", "") is { Length: > 0 } reportFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportFile))!);
            File.WriteAllText(reportFile, $"### Screenshot comparison with the baselines\n\n{summary}\n\n{report}");
        }
        return failed == 0 ? 0 : 1;
    }

    private static int Baseline(Dictionary<string, string> o)
    {
        var actual = Req(o, "actual");
        var baseline = Req(o, "baseline");
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(actual, "*.png", SearchOption.AllDirectories))
        {
            var target = Path.Combine(baseline, Path.GetRelativePath(actual, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        Console.WriteLine($"{count} baseline image(s) written to {baseline}");
        return 0;
    }

    private static int SystemChange(Dictionary<string, string> o)
    {
        var code = 0;
        void Report(string what, (bool Ok, string Message) r)
        {
            Console.WriteLine($"SYSTEM {(r.Ok ? "OK" : "REFUSED")}: {what}: {r.Message}");
            if (!r.Ok) code = 2;
        }
        if (o.ContainsKey("info")) Console.WriteLine("SYSTEM INFO: " + Display.Describe());
        if (o.TryGetValue("res", out var res))
        {
            var parts = res.Split('x');
            Report("resolution", Display.SetLargestResolution(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
        if (o.TryGetValue("scale", out var scale)) Report("scaling " + scale + "%", Display.SetScale(int.Parse(scale, CultureInfo.InvariantCulture)));
        if (o.TryGetValue("hc", out var hc)) Report("high contrast " + hc, Display.SetHighContrast(hc == "on"));
        Console.WriteLine("SYSTEM NOW: " + Display.Describe());
        return code;
    }

    // The settings file of the app is the machine's real one; keep a copy so a local run does not destroy a developer's settings.
    private static string? Backup(AppKind kind)
    {
        if (!File.Exists(kind.SettingsPath)) return null;
        var copy = kind.SettingsPath + ".uitests-backup";
        File.Copy(kind.SettingsPath, copy, overwrite: true);
        return copy;
    }

    private static void Restore(AppKind kind, string? backup)
    {
        try
        {
            if (backup is not null) File.Move(backup, kind.SettingsPath, overwrite: true);
        }
        catch (IOException) { }
    }

    private static void WriteResults(string path, string title, List<Outcome> results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var text = new StringBuilder($"### {title}\n\n| Check | Result | Detail |\n|---|---|---|\n");
        foreach (var r in results) text.AppendLine($"| {r.Name} | {(r.Passed ? "pass" : "FAIL")} | {r.Detail.Replace("|", "/").Replace("\n", " ")} |");
        File.WriteAllText(path, text.ToString());
    }
}
