using System.Security.Cryptography;
using Remotely.Core.Protocol;

namespace Remotely.Core.Crypto;

public sealed class HandshakeException(string message) : Exception(message);

/// <summary>
/// Mutually authenticated ephemeral ECDH (P-256) over any byte transport, including an untrusted relay.
/// Initiator -> Hello1 (identity, ephemeral key, nonce, the responder id it expects, signature).
/// Responder -> Hello2 (identity, ephemeral key, nonce, signature over a hash of Hello1).
/// Session keys = HKDF-SHA256(ECDH secret, salt = H(nonceI||nonceR), info = label || H(Hello1||Hello2 body)).
/// </summary>
internal static class HandshakeCommon
{
    public static readonly byte[] Magic = "RMT1"u8.ToArray();
    public const byte TypeHello1 = 1;
    public const byte TypeHello2 = 2;
    public const int NonceLength = 32;

    public static byte[] SignatureInput(string label, params byte[][] fields)
    {
        var w = new FieldWriter().Field(System.Text.Encoding.ASCII.GetBytes(label));
        foreach (var f in fields) w.Field(f);
        return w.ToArray();
    }

    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] Agree(ECDiffieHellman mine, ReadOnlySpan<byte> peerEphemeralSpki)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerEphemeralSpki, out _);
        CurveCheck.RequireP256(peer.ExportParameters(false));
        return mine.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>Returns (initiator-to-responder key, responder-to-initiator key).</summary>
    public static (byte[] I2R, byte[] R2I) DeriveKeys(byte[] secret, byte[] nonceI, byte[] nonceR, byte[] transcript)
    {
        var salt = SHA256.HashData([.. nonceI, .. nonceR]);
        var info = new FieldWriter().Field("RMT1-keys"u8).Field(transcript).ToArray();
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, info);
        return (okm[..32], okm[32..]);
    }

    public static byte[] Hash(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }
}

public sealed class InitiatorHandshake(DeviceIdentity self, string expectedResponderId) : IDisposable
{
    private ECDiffieHellman? _ephemeral;
    private byte[]? _nonce;
    private byte[]? _hello1;

    public byte[] CreateHello1()
    {
        _ephemeral = HandshakeCommon.NewEphemeral();
        var ephemeralPublic = _ephemeral.ExportSubjectPublicKeyInfo();
        _nonce = RandomNumberGenerator.GetBytes(HandshakeCommon.NonceLength);
        var expected = DeviceIds.ToBytes(expectedResponderId);
        var signature = self.Sign(HandshakeCommon.SignatureInput("RMT1-I", ephemeralPublic, _nonce, expected));
        _hello1 = new FieldWriter()
            .Raw(HandshakeCommon.Magic).Byte(HandshakeCommon.TypeHello1)
            .Field(self.PublicKey).Field(ephemeralPublic).Field(_nonce).Field(expected).Field(signature)
            .ToArray();
        return _hello1;
    }

    public SecureChannel ProcessHello2(ReadOnlySpan<byte> hello2)
    {
        if (_ephemeral is null || _nonce is null || _hello1 is null) throw new InvalidOperationException("Call CreateHello1 first.");
        try
        {
            var r = new FieldReader(hello2);
            if (!r.Raw(4).SequenceEqual(HandshakeCommon.Magic) || r.Byte() != HandshakeCommon.TypeHello2)
                throw new HandshakeException("Not a Hello2 message.");
            var identity = r.Field().ToArray();
            var ephemeral = r.Field().ToArray();
            var nonceR = r.Field().ToArray();
            var signature = r.Field().ToArray();
            if (!r.AtEnd) throw new HandshakeException("Trailing data.");
            var bodyLength = hello2.Length - (4 + signature.Length);
            var body = hello2[..bodyLength].ToArray();

            if (DeviceIds.FromPublicKey(identity) != expectedResponderId)
                throw new HandshakeException("The device answering is not the one expected.");
            var signed = HandshakeCommon.SignatureInput("RMT1-R", ephemeral, nonceR, SHA256.HashData(_hello1));
            if (!DeviceIdentity.Verify(identity, signed, signature))
                throw new HandshakeException("Responder signature is invalid.");

            var secret = HandshakeCommon.Agree(_ephemeral, ephemeral);
            var transcript = HandshakeCommon.Hash(_hello1, body);
            var (i2r, r2i) = HandshakeCommon.DeriveKeys(secret, _nonce, nonceR, transcript);
            return new SecureChannel(sendKey: i2r, receiveKey: r2i);
        }
        catch (FormatException ex) { throw new HandshakeException(ex.Message); }
        catch (CryptographicException ex) { throw new HandshakeException(ex.Message); }
    }

    public void Dispose() => _ephemeral?.Dispose();
}

public sealed class ResponderHandshake(DeviceIdentity self) : IDisposable
{
    /// <summary>Verifies Hello1 and returns Hello2 plus the keyed channel. <paramref name="isTrusted"/> decides whether the initiator may connect.</summary>
    public byte[] ProcessHello1(ReadOnlySpan<byte> hello1, Func<string, bool> isTrusted, out string initiatorId, out SecureChannel channel)
    {
        try
        {
            var r = new FieldReader(hello1);
            if (!r.Raw(4).SequenceEqual(HandshakeCommon.Magic) || r.Byte() != HandshakeCommon.TypeHello1)
                throw new HandshakeException("Not a Hello1 message.");
            var identity = r.Field().ToArray();
            var ephemeral = r.Field().ToArray();
            var nonceI = r.Field().ToArray();
            var expected = r.Field().ToArray();
            var signature = r.Field().ToArray();
            if (!r.AtEnd) throw new HandshakeException("Trailing data.");
            if (nonceI.Length != HandshakeCommon.NonceLength) throw new HandshakeException("Bad nonce.");

            if (!expected.AsSpan().SequenceEqual(DeviceIds.ToBytes(self.DeviceId)))
                throw new HandshakeException("This hello was meant for a different device.");
            var signed = HandshakeCommon.SignatureInput("RMT1-I", ephemeral, nonceI, expected);
            if (!DeviceIdentity.Verify(identity, signed, signature))
                throw new HandshakeException("Initiator signature is invalid.");
            initiatorId = DeviceIds.FromPublicKey(identity);
            if (!isTrusted(initiatorId)) throw new HandshakeException("Initiator is not trusted by this device.");

            using var eph = HandshakeCommon.NewEphemeral();
            var ephPublic = eph.ExportSubjectPublicKeyInfo();
            var nonceR = RandomNumberGenerator.GetBytes(HandshakeCommon.NonceLength);
            var sig = self.Sign(HandshakeCommon.SignatureInput("RMT1-R", ephPublic, nonceR, SHA256.HashData(hello1)));
            var body = new FieldWriter()
                .Raw(HandshakeCommon.Magic).Byte(HandshakeCommon.TypeHello2)
                .Field(self.PublicKey).Field(ephPublic).Field(nonceR)
                .ToArray();

            var secret = HandshakeCommon.Agree(eph, ephemeral);
            var transcript = HandshakeCommon.Hash(hello1.ToArray(), body);
            var (i2r, r2i) = HandshakeCommon.DeriveKeys(secret, nonceI, nonceR, transcript);
            channel = new SecureChannel(sendKey: r2i, receiveKey: i2r);
            return [.. body, .. new FieldWriter().Field(sig).ToArray()];
        }
        catch (FormatException ex) { throw new HandshakeException(ex.Message); }
        catch (CryptographicException ex) { throw new HandshakeException(ex.Message); }
    }

    public void Dispose() { }
}
