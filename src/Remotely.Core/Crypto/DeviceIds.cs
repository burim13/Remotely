using System.Security.Cryptography;

namespace Remotely.Core.Crypto;

/// <summary>A device id is the lowercase hex SHA-256 of its public key (SubjectPublicKeyInfo DER).</summary>
public static class DeviceIds
{
    public const int ByteLength = 32;

    public static string FromPublicKey(ReadOnlySpan<byte> spki) =>
        Convert.ToHexStringLower(SHA256.HashData(spki));

    public static byte[] ToBytes(string deviceId)
    {
        if (deviceId.Length != ByteLength * 2) throw new FormatException("Device id must be 64 hex characters.");
        return Convert.FromHexString(deviceId);
    }

    /// <summary>First 12 hex characters in groups of four, for people to compare by eye.</summary>
    public static string Short(string deviceId) =>
        deviceId.Length < 12 ? deviceId : $"{deviceId[..4]}-{deviceId[4..8]}-{deviceId[8..12]}";
}
