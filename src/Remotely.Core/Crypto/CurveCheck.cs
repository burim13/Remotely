using System.Security.Cryptography;

namespace Remotely.Core.Crypto;

internal static class CurveCheck
{
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    public static void RequireP256(ECParameters parameters)
    {
        if (parameters.Curve.Oid?.Value != NistP256Oid)
            throw new CryptographicException("Key is not on curve P-256.");
    }

    /// <summary>
    /// What importing a key that came from another party can throw. A key whose curve identifier is damaged is reported as
    /// <see cref="PlatformNotSupportedException"/> by the Linux (OpenSSL) backend instead of <see cref="CryptographicException"/>, so both count.
    /// </summary>
    public static bool IsKeyError(Exception ex) => ex is CryptographicException or PlatformNotSupportedException;
}
