using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Remotely.Core.NetworkSetup;

/// <summary>
/// What the user is shown about the server certificate. <see cref="PinHex"/> is the SHA-256 of the public key (what devices pin, same value as the server prints);
/// <see cref="FingerprintSha256"/> is the SHA-256 of the whole certificate (what browsers and certificate viewers show). Renewing a certificate with the
/// same key changes the fingerprint and keeps the pin.
/// </summary>
public sealed record CertificateSummary(string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string PinHex, string FingerprintSha256,
    bool IsSelfSigned, string KeyAlgorithm);

public enum ExpiryState { Ok, RenewSoon, Expired, NotYetValid }

public static class CertificateInspector
{
    /// <summary>Renewal is advised this long before the certificate expires. A choice made here, not a requirement of any standard.</summary>
    public static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(60);

    public static string Pin(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>The certificate fingerprint as pairs of upper-case hex digits separated by colons.</summary>
    public static string Fingerprint(X509Certificate2 cert) => string.Join(':', SHA256.HashData(cert.RawData).Select(b => b.ToString("X2")));

    public static CertificateSummary Describe(X509Certificate2 cert) =>
        new(cert.Subject, cert.Issuer, new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero), new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            Pin(cert), Fingerprint(cert), cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData), cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "?");

    public static ExpiryState Expiry(DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now)
    {
        if (now < notBefore) return ExpiryState.NotYetValid;
        if (now >= notAfter) return ExpiryState.Expired;
        return notAfter - now <= RenewalWindow ? ExpiryState.RenewSoon : ExpiryState.Ok;
    }
}

public enum PfxProblem
{
    None,
    /// <summary>The bytes are not a PKCS#12 (.pfx / .p12) file, or the file is damaged.</summary>
    NotAPfx,
    WrongPassword,
    /// <summary>The file holds certificates only. A server needs the private key that belongs to its certificate.</summary>
    NoPrivateKey,
    Expired,
    NotYetValid,
    /// <summary>The key is neither RSA nor ECDSA, so it cannot be used for a TLS server certificate by this server.</summary>
    UnsupportedKey,
}

public sealed record PfxValidation(PfxProblem Problem, CertificateSummary? Summary, IReadOnlyList<string> WarningKeys)
{
    public bool IsValid => Problem == PfxProblem.None;

    public string? ErrorKey => Problem switch
    {
        PfxProblem.None => null,
        PfxProblem.NotAPfx => "Setup_Pfx_NotAPfx",
        PfxProblem.WrongPassword => "Setup_Pfx_WrongPassword",
        PfxProblem.NoPrivateKey => "Setup_Pfx_NoPrivateKey",
        PfxProblem.Expired => "Setup_Pfx_Expired",
        PfxProblem.NotYetValid => "Setup_Pfx_NotYetValid",
        _ => "Setup_Pfx_UnsupportedKey",
    };
}

public static class PfxValidator
{
    /// <summary>Opens a PFX with the given password and decides whether the server could use it. Never throws for bad input.</summary>
    public static PfxValidation Validate(byte[] pfx, string? password, DateTimeOffset now)
    {
        var result = Load(pfx, password, now, out var certificate);
        certificate?.Dispose();
        return result;
    }

    /// <summary>Like <see cref="Validate"/> but also returns the certificate (with an exportable private key) when valid. The caller disposes it.</summary>
    public static PfxValidation Load(byte[] pfx, string? password, DateTimeOffset now, out X509Certificate2? certificate)
    {
        var v = ValidateCore(pfx, password, now, out certificate);
        if (!v.IsValid) { certificate?.Dispose(); certificate = null; }
        return v;
    }

    private static bool LooksLikePfx(byte[] data)
    {
        try
        {
            var outer = new System.Formats.Asn1.AsnReader(data, System.Formats.Asn1.AsnEncodingRules.BER).ReadSequence();
            return outer.ReadInteger() == 3 && outer.PeekTag().TagValue == 16;
        }
        catch (Exception ex) when (ex is System.Formats.Asn1.AsnContentException or InvalidOperationException) { return false; }
    }

    private static PfxValidation ValidateCore(byte[] pfx, string? password, DateTimeOffset now, out X509Certificate2? certificate)
    {
        certificate = null;
        if (pfx is null || pfx.Length == 0) return new(PfxProblem.NotAPfx, null, []);

        X509Certificate2Collection? all = null;
        var passwordsToTry = string.IsNullOrEmpty(password) ? new string?[] { null, "" } : new[] { password };
        foreach (var candidate in passwordsToTry)
        {
            try { all = X509CertificateLoader.LoadPkcs12Collection(pfx, candidate, X509KeyStorageFlags.Exportable); break; }
            catch (CryptographicException) { }
        }
        if (all is null)
        {
            // The loader does not say why it failed. A file whose outer structure is a PFX (version 3) that still will not open is reported as a password problem;
            // anything else is not a PFX. A damaged PFX also lands in the first group, which is why the message says "wrong password or damaged".
            return new(LooksLikePfx(pfx) ? PfxProblem.WrongPassword : PfxProblem.NotAPfx, null, []);
        }

        X509Certificate2? chosen = null;
        foreach (var c in all)
            if (chosen is null && c.HasPrivateKey) chosen = c;
        if (chosen is null)
        {
            foreach (var c in all) c.Dispose();
            return new(PfxProblem.NoPrivateKey, null, []);
        }
        foreach (var c in all)
            if (!ReferenceEquals(c, chosen)) c.Dispose();

        var summary = CertificateInspector.Describe(chosen);
        PfxProblem problem = PfxProblem.None;
        using (var rsa = chosen.GetRSAPublicKey())
        using (var ec = chosen.GetECDsaPublicKey())
            if (rsa is null && ec is null) problem = PfxProblem.UnsupportedKey;
        if (problem == PfxProblem.None)
            problem = CertificateInspector.Expiry(summary.NotBefore, summary.NotAfter, now) switch
            {
                ExpiryState.Expired => PfxProblem.Expired,
                ExpiryState.NotYetValid => PfxProblem.NotYetValid,
                _ => PfxProblem.None,
            };

        var warnings = new List<string>();
        if (problem == PfxProblem.None)
        {
            if (CertificateInspector.Expiry(summary.NotBefore, summary.NotAfter, now) == ExpiryState.RenewSoon) warnings.Add("Setup_Pfx_Warn_ExpiresSoon");
            var eku = chosen.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku is not null && !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1")) warnings.Add("Setup_Pfx_Warn_NotForServers");
        }

        if (problem == PfxProblem.None) certificate = chosen; else chosen.Dispose();
        return new(problem, summary, warnings);
    }
}

public static class SelfSignedCertificates
{
    public const string Subject = "CN=Remotely";

    /// <summary>A new self-signed ECDSA P-256 server certificate, like the one the server creates on first run.</summary>
    public static X509Certificate2 Create(TimeSpan validity, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Build(new CertificateRequest(Subject, key, HashAlgorithmName.SHA256), validity, now);
    }

    /// <summary>
    /// A new certificate over the SAME key as <paramref name="existing"/>. Devices pin the key, not the certificate, so the pin stays the same and no device has to
    /// be invited again. Throws <see cref="InvalidOperationException"/> when the certificate has no usable private key.
    /// </summary>
    public static X509Certificate2 RenewKeepingKey(X509Certificate2 existing, TimeSpan validity, DateTimeOffset now)
    {
        using var ec = existing.GetECDsaPrivateKey();
        if (ec is not null) return Build(new CertificateRequest(existing.Subject, ec, HashAlgorithmName.SHA256), validity, now);
        using var rsa = existing.GetRSAPrivateKey();
        if (rsa is not null) return Build(new CertificateRequest(existing.Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), validity, now);
        throw new InvalidOperationException("The certificate has no ECDSA or RSA private key to renew.");
    }

    private static X509Certificate2 Build(CertificateRequest request, TimeSpan validity, DateTimeOffset now)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(now.AddDays(-1), now + validity);
        // CreateSelfSigned returns a certificate bound to the caller's key object; round-trip through PFX so it owns an exportable key of its own.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }
}
