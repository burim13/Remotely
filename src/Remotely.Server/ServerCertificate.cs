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
    public static readonly TimeSpan Validity = TimeSpan.FromDays(3650);

    public static string PathIn(string dataDir) => Path.Combine(dataDir, "server.pfx");

    public static X509Certificate2 LoadOrCreate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = PathIn(dataDir);
        if (File.Exists(path)) return Load(path);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var created = Create(key);
        WritePfx(path, created);
        return Load(path);
    }

    private static X509Certificate2 Load(string path) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);

    private static X509Certificate2 Create(ECDsa key)
    {
        var request = new CertificateRequest("CN=Remotely", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.Add(Validity));
    }

    private static void WritePfx(string path, X509Certificate2 cert)
    {
        WriteSecret(path, cert.Export(X509ContentType.Pfx));
    }

    /// <summary>Temp file created owner-only on Unix (never briefly world-readable), then moved into place.</summary>
    private static void WriteSecret(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Makes a certificate with a fresh validity period and writes it over server.pfx (the old file stays as server.pfx.prev).
    /// With <paramref name="newKey"/> false the key is kept, so the pin that devices hold does not change; with true it does, and every device must join again.
    /// </summary>
    public static X509Certificate2 Renew(string dataDir, bool newKey)
    {
        var path = PathIn(dataDir);
        using var current = LoadOrCreate(dataDir);
        using var key = newKey ? ECDsa.Create(ECCurve.NamedCurves.nistP256)
            : current.GetECDsaPrivateKey() ?? throw new InvalidOperationException("The current certificate has no usable ECDSA key.");
        using var created = Create(key);
        File.Copy(path, path + ".prev", overwrite: true);
        WritePfx(path, created);
        return Load(path);
    }

    /// <summary>Lowercase hex SHA-256 of the certificate's SubjectPublicKeyInfo: the value clients pin.</summary>
    public static string Pin(X509Certificate2 cert) =>
        Convert.ToHexStringLower(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>Checks a .pfx without installing it (used when restoring a backup).</summary>
    public static X509Certificate2 LoadFromBytes(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);

    public static void Install(string dataDir, byte[] pfx)
    {
        var path = PathIn(dataDir);
        if (File.Exists(path)) File.Copy(path, path + ".prev", overwrite: true);
        WriteSecret(path, pfx);
    }
}

/// <summary>
/// The certificate the public port presents right now. Renewing or restoring through the admin API swaps it at once. If server.pfx is replaced by something else
/// (the Server Manager's helper does this), the file is noticed within <see cref="Recheck"/> and used for new connections; no restart.
/// </summary>
public sealed class CertificateHolder
{
    public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(5);

    private readonly string? _path;
    private volatile X509Certificate2 _current;
    private DateTime _fileTime;
    private long _nextCheck;

    public CertificateHolder(X509Certificate2 initial, string? pfxPath = null)
    {
        _current = initial;
        _path = pfxPath;
        _fileTime = pfxPath is not null && File.Exists(pfxPath) ? File.GetLastWriteTimeUtc(pfxPath) : default;
    }

    public X509Certificate2 Current
    {
        get
        {
            MaybeReload();
            return _current;
        }
    }

    public string Pin => ServerCertificate.Pin(Current);

    public void Replace(X509Certificate2 next)
    {
        _current = next;
        if (_path is not null && File.Exists(_path)) _fileTime = File.GetLastWriteTimeUtc(_path);
    }

    private void MaybeReload()
    {
        if (_path is null) return;
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextCheck)) return;
        Interlocked.Exchange(ref _nextCheck, now + (long)Recheck.TotalMilliseconds);
        try
        {
            if (!File.Exists(_path)) return;
            var time = File.GetLastWriteTimeUtc(_path);
            if (time == _fileTime) return;
            _current = ServerCertificate.LoadFromBytes(File.ReadAllBytes(_path));
            _fileTime = time;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // half-written or unreadable: keep the certificate in use and look again later
        }
    }
}
