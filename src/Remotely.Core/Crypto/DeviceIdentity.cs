using System.Security.Cryptography;

namespace Remotely.Core.Crypto;

/// <summary>A device's long-term ECDSA P-256 identity. Persisting the private key is the caller's job (DPAPI, Keychain).</summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    private DeviceIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        DeviceId = DeviceIds.FromPublicKey(PublicKey);
    }

    public byte[] PublicKey { get; }
    public string DeviceId { get; }

    public static DeviceIdentity Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static DeviceIdentity FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            CurveCheck.RequireP256(key.ExportParameters(false));
            return new DeviceIdentity(key);
        }
        catch (PlatformNotSupportedException ex)
        {
            key.Dispose();
            throw new CryptographicException(ex.Message, ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public byte[] ExportPkcs8() => _key.ExportPkcs8PrivateKey();

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>Verifies a signature made by <see cref="Sign"/> against a peer's public key. Returns false for any malformed input.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKeySpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            CurveCheck.RequireP256(key.ExportParameters(false));
            // A device id is the hash of these exact bytes. The importer accepts a few other encodings of the same key (for example a non-zero "unused bits"
            // byte in the BIT STRING), which would let one key appear under several ids. Only the canonical encoding counts.
            if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKeySpki)) return false;
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (CurveCheck.IsKeyError(ex))
        {
            return false;
        }
    }

    public void Dispose() => _key.Dispose();
}
