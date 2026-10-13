using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Remotely.Server;

/// <summary>
/// A self-generated ECDSA P-256 TLS certificate, created on first run and reused. Clients pin the SHA-256 of its public key,
/// so no domain name or public certificate authority is involved.
/// The .pfx has no password; protect the data directory with file permissions.
/// </summary>
public static class ServerCertificate
{
    public static X509Certificate2 LoadOrCreate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "server.pfx");
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Remotely", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(path, created.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Lowercase hex SHA-256 of the certificate's SubjectPublicKeyInfo: the value clients pin.</summary>
    public static string Pin(X509Certificate2 cert) =>
        Convert.ToHexStringLower(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
}
