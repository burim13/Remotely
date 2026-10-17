using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Remotely.Core.ServerManager;

namespace Remotely.Core.Diagnostics;

/// <summary>The real machine behind the export. Each program says where its own logs, settings and crash files are.</summary>
public sealed partial class SystemDiagnosticsSource(
    string programName, string version, IReadOnlyList<LogSource> logs, IReadOnlyList<SettingsSource> settings, IReadOnlyList<string> crashFiles,
    string? crashDumpDirectory, IReadOnlyList<string> firewallRules, IReadOnlyList<string> serviceNames, IProcessRunner? runner = null) : IDiagnosticsSource
{
    public static readonly string[] AppFirewallRules = ["Remotely same-network (TCP)", "Remotely same-network (UDP)"];
    public static readonly string[] ServerFirewallRules = ["Remotely Server"];

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();

    public string ProgramName => programName;
    public string? CrashDumpDirectory => crashDumpDirectory;

    public IReadOnlyDictionary<string, string> Versions() => new Dictionary<string, string>
    {
        ["Version"] = version,
        [".NET"] = RuntimeInformation.FrameworkDescription,
        ["Process architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
    };

    public string OperatingSystem() => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public IReadOnlyList<AdapterDetail> Adapters()
    {
        var list = new List<AdapterDetail>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                list.Add(new AdapterDetail(nic.Name, nic.NetworkInterfaceType.ToString(), nic.OperationalStatus.ToString(),
                    [.. nic.GetIPProperties().UnicastAddresses.Select(u => u.Address.ToString() + (u.IPv4Mask is { } m && !m.Equals(System.Net.IPAddress.Any) ? "/" + m : ""))]));
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException) { /* an empty list says so in the file */ }
        return list;
    }

    public async Task<IReadOnlyList<FirewallRuleReport>> FirewallRulesAsync(CancellationToken ct)
    {
        var result = new List<FirewallRuleReport>();
        foreach (var rule in firewallRules)
        {
            if (!OperatingSystem2.IsWindows) { result.Add(new(rule, "not checked", "the firewall is only queried on Windows")); continue; }
            try
            {
                var (code, output) = await _runner.RunAsync("netsh.exe", ["advfirewall", "firewall", "show", "rule", "name=" + rule], ct).ConfigureAwait(false);
                result.Add(Parse(rule, code, output));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                result.Add(new(rule, "unknown", "netsh could not be started"));
            }
        }
        return result;
    }

    public static FirewallRuleReport Parse(string rule, int exitCode, string output)
    {
        if (exitCode != 0) return new(rule, "not reported by netsh", $"exit code {exitCode}; the rule is missing or cannot be read");
        var enabled = EnabledLine().Match(output);
        var ports = string.Join(", ", PortLines().Matches(output).Select(m => m.Groups[1].Value));
        return new(rule, "present", (enabled.Success ? "enabled: " + enabled.Groups[1].Value : "enabled state not read") + (ports.Length > 0 ? "; local port " + ports : ""));
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ServiceStatesAsync(CancellationToken ct)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var name in serviceNames)
        {
            if (!OperatingSystem2.IsWindows) { result.Add(new(name, "not checked (not Windows)")); continue; }
            try
            {
                var (code, output) = await _runner.RunAsync("sc.exe", ["query", name], ct).ConfigureAwait(false);
                result.Add(new(name, ScOutput.Parse(code, output).ToString()));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                result.Add(new(name, "Unknown (sc.exe could not be started)"));
            }
        }
        return result;
    }

    public IReadOnlyList<LogSource> Logs() => logs;
    public IReadOnlyList<SettingsSource> Settings() => settings;
    public IReadOnlyList<string> CrashFiles() => crashFiles;

    [GeneratedRegex(@"^\s*Enabled:\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex EnabledLine();

    [GeneratedRegex(@"^\s*LocalPort:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PortLines();
}

internal static class OperatingSystem2
{
    public static bool IsWindows => System.OperatingSystem.IsWindows();
}
