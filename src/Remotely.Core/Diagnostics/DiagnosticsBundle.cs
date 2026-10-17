using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Remotely.Core.Diagnostics;

public sealed record AdapterDetail(string Name, string Type, string Status, IReadOnlyList<string> Addresses);
public sealed record FirewallRuleReport(string Rule, string State, string Detail);
/// <summary>A folder of log files. <paramref name="ServerStyle"/>: the server's naming (server.log plus renamed older files) instead of this assembly's.</summary>
public sealed record LogSource(string Name, string Directory, string Prefix, bool ServerStyle = false);

/// <summary>A JSON settings file and the only keys whose values may be exported. Every other key is listed by name with its value left out.</summary>
public sealed record SettingsSource(string Name, string Path, IReadOnlyCollection<string> AllowedKeys);

/// <summary>What the export reads. The real one asks the machine; tests give it planted values.</summary>
public interface IDiagnosticsSource
{
    string ProgramName { get; }
    IReadOnlyDictionary<string, string> Versions();
    string OperatingSystem();
    IReadOnlyList<AdapterDetail> Adapters();
    Task<IReadOnlyList<FirewallRuleReport>> FirewallRulesAsync(CancellationToken ct);
    Task<IReadOnlyList<KeyValuePair<string, string>>> ServiceStatesAsync(CancellationToken ct);
    IReadOnlyList<LogSource> Logs();
    IReadOnlyList<SettingsSource> Settings();
    /// <summary>Text files that hold crash reports or other last-resort logs, such as the Manager helper log (the last lines of each are exported).</summary>
    IReadOnlyList<string> CrashFiles();
    /// <summary>Folder of local crash dumps. Only names and sizes are listed; a dump is never exported.</summary>
    string? CrashDumpDirectory { get; }
}

/// <summary>
/// Builds the "Export diagnostics" zip. Contents: README.txt, summary.txt (versions, OS), network.txt (adapters with addresses), firewall.txt, services.txt, settings.txt
/// (allow-listed values only), logs/*.txt (last N lines), crash.txt (last lines of the crash logs, plus the names and sizes of crash dumps). Every text passes through
/// the <see cref="Redactor"/> a second time here, even though the log writer already did. Not included, by design: key files, tokens, invites, pairing codes, crash dumps,
/// file transfers, screen or clipboard content. Nothing is sent anywhere; the zip goes to the stream the caller gives.
/// </summary>
public static class DiagnosticsBundle
{
    public const int DefaultLogLines = 500;

    public static async Task WriteAsync(IDiagnosticsSource source, Stream output, Redactor? redactor = null, int logLines = DefaultLogLines, CancellationToken ct = default)
    {
        redactor ??= Redactor.Shared;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Add(string name, string text) => AddEntry(zip, name, redactor.Apply(text));

        Add("README.txt", $"""
            Remotely diagnostics for {source.ProgramName}, made {DateTimeOffset.UtcNow:u}.
            It was written on this PC and has not been sent anywhere. Read it before you share it.
            Contains: versions and operating system, network adapters and their addresses, firewall rules for Remotely, service state,
            settings that are safe to show, the last {logLines} lines of each log, the last crash report.
            Left out on purpose: keys, tokens, invites, pairing codes, crash dumps, files, screen and clipboard content.
            Text that looks like a secret is replaced by {Redactor.Mask}. Names of PCs and your IP addresses are NOT removed.
            """);

        var summary = new StringBuilder().AppendLine(source.ProgramName).AppendLine("OS: " + source.OperatingSystem());
        foreach (var (key, value) in source.Versions()) summary.AppendLine($"{key}: {value}");
        Add("summary.txt", summary.ToString());

        var network = new StringBuilder();
        foreach (var a in source.Adapters())
            network.AppendLine($"{a.Name} [{a.Type}, {a.Status}]: {(a.Addresses.Count == 0 ? "(no address)" : string.Join(", ", a.Addresses))}");
        Add("network.txt", network.Length == 0 ? "No network adapters were found.\n" : network.ToString());

        var firewall = new StringBuilder();
        foreach (var r in await source.FirewallRulesAsync(ct).ConfigureAwait(false)) firewall.AppendLine($"{r.Rule}: {r.State}{(r.Detail.Length > 0 ? " (" + r.Detail + ")" : "")}");
        Add("firewall.txt", firewall.ToString());

        var services = new StringBuilder();
        foreach (var (name, state) in await source.ServiceStatesAsync(ct).ConfigureAwait(false)) services.AppendLine($"{name}: {state}");
        Add("services.txt", services.ToString());

        var settings = new StringBuilder();
        foreach (var s in source.Settings()) settings.AppendLine(SettingsScrubber.Describe(s));
        Add("settings.txt", settings.ToString());

        foreach (var log in source.Logs())
        {
            var lines = log.ServerStyle ? RollingFileLog.TailServerStyle(log.Directory, log.Prefix, logLines) : RollingFileLog.Tail(log.Directory, log.Prefix, logLines);
            Add($"logs/{SafeName(log.Name)}.txt", lines.Count == 0 ? "(no log lines found)\n" : string.Join('\n', lines) + "\n");
        }

        var crash = new StringBuilder();
        foreach (var file in source.CrashFiles())
        {
            crash.AppendLine("--- " + Path.GetFileName(file));
            var lines = RollingFileLog.TailOfFile(file, 120);
            crash.AppendLine(lines.Count == 0 ? "(nothing recorded)" : string.Join('\n', lines));
        }
        if (source.CrashDumpDirectory is { } dumps)
            foreach (var (name, bytes) in CrashDumps.List(dumps)) crash.AppendLine($"dump file (not included): {name}, {bytes} bytes");
        Add("crash.txt", crash.Length == 0 ? "No crash report was found.\n" : crash.ToString());
        ct.ThrowIfCancellationRequested();
    }

    private static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static void AddEntry(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }
}

/// <summary>Settings without secrets: values of allow-listed keys only, and only short plain values; everything else is named and left out.</summary>
public static class SettingsScrubber
{
    public static string Describe(SettingsSource source)
    {
        var sb = new StringBuilder().AppendLine("--- " + source.Name);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(source.Path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return sb.AppendLine("(not a settings object)").ToString();
            var left = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var plain = p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                    || (p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString()?.Length ?? 0) <= 80);
                if (source.AllowedKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && plain) sb.AppendLine($"{p.Name}: {p.Value}");
                else left.Add(p.Name);
            }
            if (left.Count > 0) sb.AppendLine("values left out: " + string.Join(", ", left));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { sb.AppendLine("(not readable)"); }
        return sb.ToString();
    }
}
