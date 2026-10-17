using System.ComponentModel;
using System.Diagnostics;

namespace Remotely.Core.Update;

public enum SignatureStatus
{
    /// <summary>The file carries no Authenticode signature (the installers are unsigned for now).</summary>
    NotSigned,

    /// <summary>Signed and the signature and certificate chain check out.</summary>
    Valid,

    /// <summary>Signed, but the file was changed after signing, or the certificate is not trusted, or Windows could not decide. Never accepted.</summary>
    Invalid,

    /// <summary>Not checked because this is not Windows.</summary>
    NotChecked,
}

public sealed record SignatureResult(SignatureStatus Status, string? Signer, string? Detail);

public interface IAuthenticodeVerifier
{
    Task<SignatureResult> VerifyAsync(string path, CancellationToken ct);
}

/// <summary>
/// Asks Windows PowerShell for the Authenticode status (<c>Get-AuthenticodeSignature</c>); it handles embedded and catalog signatures. The path goes in through an
/// environment variable, never into the command text. Status names are Microsoft's: Valid, NotSigned, HashMismatch, NotTrusted, UnknownError, and so on.
/// </summary>
public sealed class PowerShellAuthenticodeVerifier : IAuthenticodeVerifier
{
    private const string PathVariable = "REMOTELY_VERIFY_PATH";
    private const string Script = "$ErrorActionPreference='Stop'; $s = Get-AuthenticodeSignature -LiteralPath $env:" + PathVariable + "; 'STATUS=' + $s.Status; if ($s.SignerCertificate) { 'SIGNER=' + $s.SignerCertificate.Subject }";

    public async Task<SignatureResult> VerifyAsync(string path, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new SignatureResult(SignatureStatus.NotChecked, null, "Authenticode is checked on Windows only.");
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(Script);
        info.Environment[PathVariable] = Path.GetFullPath(path);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
            return Interpret(await output.ConfigureAwait(false), process.ExitCode, await error.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new SignatureResult(SignatureStatus.Invalid, null, "The signature could not be checked: " + ex.Message);
        }
    }

    /// <summary>Turns the script output into a result. Anything that is not clearly "Valid" or "NotSigned" is Invalid.</summary>
    public static SignatureResult Interpret(string output, int exitCode, string error)
    {
        string? status = null, signer = null;
        foreach (var line in output.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("STATUS=", StringComparison.Ordinal)) status = l["STATUS=".Length..];
            else if (l.StartsWith("SIGNER=", StringComparison.Ordinal)) signer = l["SIGNER=".Length..];
        }
        if (exitCode != 0 || status is null) return new SignatureResult(SignatureStatus.Invalid, null, "The signature could not be checked. " + error.Trim());
        return status switch
        {
            "Valid" => new SignatureResult(SignatureStatus.Valid, signer, null),
            "NotSigned" => new SignatureResult(SignatureStatus.NotSigned, null, null),
            _ => new SignatureResult(SignatureStatus.Invalid, signer, "Windows reports the signature as " + status + "."),
        };
    }
}
