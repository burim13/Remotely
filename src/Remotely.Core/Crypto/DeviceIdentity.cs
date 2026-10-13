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
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        CurveCheck.RequireP256(key.ExportParameters(false));
        return new DeviceIdentity(key);
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
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose() => _key.Dispose();
}
