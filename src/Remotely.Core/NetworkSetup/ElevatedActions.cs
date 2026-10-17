using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Remotely.Core.NetworkSetup;

public sealed record ActionResult(bool Ok, string? ErrorKey = null, string? Detail = null)
{
    public static ActionResult Success(string? detail = null) => new(true, null, detail);
    public static ActionResult Failure(string errorKey, string? detail = null) => new(false, errorKey, detail);
}

public enum FirewallRuleState { Missing, Present, PresentForOtherPort, Unknown }

/// <summary>
/// The things the wizard needs that require administrator rights. The Server Manager window itself never runs elevated: its host (agent A11) implements this interface
/// by starting a small elevated helper (one UAC prompt) that runs <see cref="NetshElevatedActions"/>. Passwords must not travel on a command line; pass the PFX password
/// through standard input or a pipe to the helper.
/// </summary>
public interface IElevatedActions
{
    Task<FirewallRuleState> GetFirewallRuleAsync(int port, CancellationToken ct = default);

    /// <summary>Replaces the "Remotely Server" inbound TCP rule so it allows exactly <paramref name="port"/>.</summary>
    Task<ActionResult> AddFirewallRuleAsync(int port, CancellationToken ct = default);

    Task<ActionResult> RemoveFirewallRuleAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates the PFX, backs up the server's current certificate and installs the new one as the server's <c>server.pfx</c>. The server reads the file when it starts,
    /// so it must be restarted afterwards (not verified here: nothing in the server watches the file).
    /// </summary>
    Task<ActionResult> InstallCertificateAsync(string pfxPath, string? password, CancellationToken ct = default);

    /// <summary>
    /// Makes a new certificate over the same private key as the server's current one (the old file is kept as a backup), valid for <paramref name="validity"/>.
    /// The pin does not change, so paired devices keep working. The server must be restarted to use it.
    /// </summary>
    Task<ActionResult> RenewCertificateAsync(TimeSpan validity, CancellationToken ct = default);
}

public sealed record ProcessOutput(int ExitCode, string Text);

public interface IProcessRunner
{
    Task<ProcessOutput> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessOutput> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new ProcessOutput(process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}

/// <summary>
/// Runs with administrator rights. Uses the same rule name and netsh arguments as <c>Remotely.Server.exe install</c>, so the two can replace each other's rule
/// and <c>uninstall</c> removes it. The netsh output format of "show rule" is parsed by its English label; on other Windows languages the state is reported as Unknown.
/// </summary>
public sealed class NetshElevatedActions(IProcessRunner runner, string dataDir) : IElevatedActions
{
    public const string RuleName = "Remotely Server";

    internal static string[] AddArguments(int port) =>
        ["advfirewall", "firewall", "add", "rule", "name=" + RuleName, "dir=in", "action=allow", "protocol=TCP", "localport=" + port.ToString(CultureInfo.InvariantCulture)];

    internal static readonly string[] DeleteArguments = ["advfirewall", "firewall", "delete", "rule", "name=" + RuleName];
    internal static readonly string[] ShowArguments = ["advfirewall", "firewall", "show", "rule", "name=" + RuleName];

    public async Task<FirewallRuleState> GetFirewallRuleAsync(int port, CancellationToken ct = default)
    {
        ProcessOutput result;
        try { result = await runner.RunAsync("netsh.exe", ShowArguments, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return FirewallRuleState.Unknown; }
        return ParseShowRule(result, port);
    }

    public static FirewallRuleState ParseShowRule(ProcessOutput result, int port)
    {
        if (result.ExitCode != 0) return result.Text.Contains(RuleName, StringComparison.Ordinal) && result.Text.Contains("Rule Name", StringComparison.OrdinalIgnoreCase) ? FirewallRuleState.Unknown : FirewallRuleState.Missing;
        var ports = Regex.Matches(result.Text, @"^\s*LocalPort:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToList();
        if (ports.Count == 0) return FirewallRuleState.Unknown;
        var wanted = port.ToString(CultureInfo.InvariantCulture);
        return ports.Any(p => p.Split(',').Select(x => x.Trim()).Contains(wanted)) ? FirewallRuleState.Present : FirewallRuleState.PresentForOtherPort;
    }

    public async Task<ActionResult> AddFirewallRuleAsync(int port, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535) return ActionResult.Failure("Setup_Err_PortInvalid");
        try
        {
            await runner.RunAsync("netsh.exe", DeleteArguments, ct).ConfigureAwait(false);   // fails harmlessly when there is no rule yet
            var add = await runner.RunAsync("netsh.exe", AddArguments(port), ct).ConfigureAwait(false);
            return add.ExitCode == 0 ? ActionResult.Success() : ActionResult.Failure("Setup_Err_FirewallFailed", add.Text.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ActionResult.Failure("Setup_Err_FirewallFailed", ex.Message); }
    }

    public async Task<ActionResult> RemoveFirewallRuleAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await runner.RunAsync("netsh.exe", DeleteArguments, ct).ConfigureAwait(false);
            return result.ExitCode == 0 ? ActionResult.Success() : ActionResult.Failure("Setup_Err_FirewallFailed", result.Text.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ActionResult.Failure("Setup_Err_FirewallFailed", ex.Message); }
    }

    public async Task<ActionResult> InstallCertificateAsync(string pfxPath, string? password, CancellationToken ct = default)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(pfxPath, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ActionResult.Failure("Setup_Pfx_CannotRead", ex.Message); }
        return await InstallCertificateBytesAsync(bytes, password, ct).ConfigureAwait(false);
    }

    /// <summary>Same as <see cref="InstallCertificateAsync"/> for a file the caller has already read, so the elevated side never opens a path it was handed.</summary>
    public async Task<ActionResult> InstallCertificateBytesAsync(byte[] pfx, string? password, CancellationToken ct = default)
    {
        var validation = PfxValidator.Load(pfx, password, DateTimeOffset.UtcNow, out var certificate);
        if (!validation.IsValid || certificate is null) return ActionResult.Failure(validation.ErrorKey ?? "Setup_Pfx_NotAPfx");
        using (certificate)
        {
            try
            {
                await WriteAsync(Path.Combine(dataDir, "server.pfx"), certificate, ct).ConfigureAwait(false);
                return ActionResult.Success(CertificateInspector.Pin(certificate));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ActionResult.Failure("Setup_Pfx_CannotWrite", ex.Message); }
        }
    }

    public async Task<ActionResult> RenewCertificateAsync(TimeSpan validity, CancellationToken ct = default)
    {
        var target = Path.Combine(dataDir, "server.pfx");
        try
        {
            if (!File.Exists(target)) return ActionResult.Failure("Setup_Pfx_NoCurrent");
            using var current = X509CertificateLoader.LoadPkcs12(await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false), password: null, X509KeyStorageFlags.Exportable);
            using var renewed = SelfSignedCertificates.RenewKeepingKey(current, validity, DateTimeOffset.UtcNow);
            await WriteAsync(target, renewed, ct).ConfigureAwait(false);
            return ActionResult.Success(CertificateInspector.Pin(renewed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ActionResult.Failure("Setup_Pfx_CannotWrite", ex.Message); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException) { return ActionResult.Failure("Setup_Pfx_NoCurrent", ex.Message); }
    }

    private async Task WriteAsync(string target, X509Certificate2 certificate, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDir);
        if (File.Exists(target))
            File.Copy(target, Path.Combine(dataDir, "server.pfx.bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)), overwrite: false);
        // The server opens server.pfx without a password, so store it without one.
        var temp = target + ".new";
        await File.WriteAllBytesAsync(temp, certificate.Export(X509ContentType.Pfx), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, target, overwrite: true);
        if (OperatingSystem.IsWindows())
            await runner.RunAsync("icacls.exe", [dataDir, "/grant", @"NT AUTHORITY\LocalService:(OI)(CI)M", "/T", "/Q"], ct).ConfigureAwait(false);
    }
}

/// <summary>The words the elevated helper is started with. Secrets (a PFX password) are deliberately not part of it.</summary>
public sealed record ElevatedCommand(string Name, int? Port, string? Path)
{
    public const string FirewallAdd = "firewall-add";
    public const string FirewallRemove = "firewall-remove";
    public const string FirewallShow = "firewall-show";
    public const string CertInstall = "cert-install";
    public const string CertRenew = "cert-renew";

    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string> { "--elevated", Name };
        if (Port is { } p) { args.Add("--port"); args.Add(p.ToString(CultureInfo.InvariantCulture)); }
        if (Path is not null) { args.Add("--path"); args.Add(Path); }
        return args;
    }

    public static bool TryParse(IReadOnlyList<string> args, out ElevatedCommand? command)
    {
        command = null;
        if (args.Count < 2 || args[0] != "--elevated" || args[1] is not (FirewallAdd or FirewallRemove or FirewallShow or CertInstall or CertRenew)) return false;
        int? port = null;
        string? path = null;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535) { port = p; i++; }
            else if (args[i] == "--path" && i + 1 < args.Count) { path = args[++i]; }
            else return false;
        }
        if (args[1] is FirewallAdd or FirewallShow && port is null) return false;
        if (args[1] == CertInstall && path is null) return false;
        command = new ElevatedCommand(args[1], port, path);
        return true;
    }
}

/// <summary>
/// How a PFX and its password travel from the Manager to the elevated helper: over a private named pipe, never on a command line.
/// Layout: bool hasPassword, string password (if any), int32 length, the PFX bytes.
/// </summary>
public static class PfxTransfer
{
    public const int MaxBytes = 1_000_000;

    public static void Write(Stream stream, byte[] pfx, string? password)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(password is not null);
        if (password is not null) writer.Write(password);
        writer.Write(pfx.Length);
        writer.Write(pfx);
        writer.Flush();
    }

    /// <summary>Returns null when the data is malformed or larger than <see cref="MaxBytes"/>.</summary>
    public static (byte[] Pfx, string? Password)? Read(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var password = reader.ReadBoolean() ? reader.ReadString() : null;
            var length = reader.ReadInt32();
            if (length is < 1 or > MaxBytes) return null;
            var bytes = reader.ReadBytes(length);
            return bytes.Length == length ? (bytes, password) : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException) { return null; }
    }
}
