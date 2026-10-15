using System.Security.Cryptography;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;

namespace Remotely.Core.Lan;

/// <summary>
/// The token-bound handshake for a direct connection on the local network (RMTL1). Both sides make a fresh ECDH (P-256) key, and the session keys are
/// HKDF(ECDH secret + the token's stretched key, ...). Someone without the token ends up with different keys, so the controller's first encrypted
/// message (the "proof", sent before the host says anything else) fails to decrypt and the host closes the connection. It is not a formal PAKE: it
/// relies on a short token that is stretched (PBKDF2, 300k rounds), is single-use and expires, and is dropped after 3 wrong tries.
/// Controller -> Hello1 (ephemeral key, nonce, the host's 4-character label). Host -> Hello2 (ephemeral key, nonce, the token's salt).
/// </summary>
internal static class LanHandshakeCommon
{
    public static readonly byte[] Magic = "RMTL"u8.ToArray();
    public const byte TypeHello1 = 1;
    public const byte TypeHello2 = 2;
    public const int NonceLength = 32;

    public static (byte[] ControllerToHost, byte[] HostToController) DeriveKeys(byte[] ecdh, byte[] tokenKey, byte[] nonceC, byte[] nonceH, byte[] hello1, byte[] hello2)
    {
        var transcript = SHA256.HashData([.. hello1, .. hello2]);
        var salt = SHA256.HashData([.. nonceC, .. nonceH]);
        var info = new FieldWriter().Field("RMTL1-keys"u8).Field(transcript).ToArray();
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, [.. ecdh, .. tokenKey], 64, salt, info);
        return (okm[..32], okm[32..]);
    }

    public static byte[] Agree(ECDiffieHellman mine, ReadOnlySpan<byte> peerSpki)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerSpki, out _);
        CurveCheck.RequireP256(peer.ExportParameters(false));
        return mine.DeriveRawSecretAgreement(peer.PublicKey);
    }
}

/// <summary>The controller's side (the PC where the token is typed).</summary>
public sealed class LanInitiatorHandshake(LanToken token) : IDisposable
{
    private ECDiffieHellman? _ephemeral;
    private byte[]? _nonce;
    private byte[]? _hello1;

    public byte[] CreateHello1()
    {
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _nonce = RandomNumberGenerator.GetBytes(LanHandshakeCommon.NonceLength);
        _hello1 = new FieldWriter().Raw(LanHandshakeCommon.Magic).Byte(LanHandshakeCommon.TypeHello1)
            .Field(_ephemeral.ExportSubjectPublicKeyInfo()).Field(_nonce).Field(System.Text.Encoding.ASCII.GetBytes(token.Id)).ToArray();
        return _hello1;
    }

    public SecureChannel ProcessHello2(ReadOnlySpan<byte> hello2)
    {
        if (_ephemeral is null || _nonce is null || _hello1 is null) throw new InvalidOperationException("Call CreateHello1 first.");
        try
        {
            var r = new FieldReader(hello2);
            if (!r.Raw(4).SequenceEqual(LanHandshakeCommon.Magic) || r.Byte() != LanHandshakeCommon.TypeHello2) throw new HandshakeException("That is not a Remotely PC.");
            var ephemeral = r.Field().ToArray();
            var nonceH = r.Field().ToArray();
            var salt = r.Field().ToArray();
            if (!r.AtEnd || nonceH.Length != LanHandshakeCommon.NonceLength || salt.Length != 16) throw new HandshakeException("Bad reply from the other PC.");
            var tokenKey = token.DeriveKey(salt);
            var (c2h, h2c) = LanHandshakeCommon.DeriveKeys(LanHandshakeCommon.Agree(_ephemeral, ephemeral), tokenKey, _nonce, nonceH, _hello1, hello2.ToArray());
            return new SecureChannel(sendKey: c2h, receiveKey: h2c);
        }
        catch (FormatException ex) { throw new HandshakeException(ex.Message); }
        catch (CryptographicException ex) { throw new HandshakeException(ex.Message); }
    }

    public void Dispose() => _ephemeral?.Dispose();
}

/// <summary>The host's side (the PC that shows the token).</summary>
public sealed class LanResponderHandshake(LanTokenState state)
{
    /// <summary>Returns Hello2 and the keyed channel. Throws <see cref="HandshakeException"/> if the hello is not for this token's label.</summary>
    public byte[] ProcessHello1(ReadOnlySpan<byte> hello1, out SecureChannel channel)
    {
        try
        {
            var r = new FieldReader(hello1);
            if (!r.Raw(4).SequenceEqual(LanHandshakeCommon.Magic) || r.Byte() != LanHandshakeCommon.TypeHello1) throw new HandshakeException("Not a Remotely connection.");
            var ephemeral = r.Field().ToArray();
            var nonceC = r.Field().ToArray();
            var id = r.Field().ToArray();
            if (!r.AtEnd || nonceC.Length != LanHandshakeCommon.NonceLength) throw new HandshakeException("Bad hello.");
            if (!System.Text.Encoding.ASCII.GetString(id).Equals(state.Token.Id, StringComparison.Ordinal)) throw new HandshakeException("That token is for a different PC.");

            using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var nonceH = RandomNumberGenerator.GetBytes(LanHandshakeCommon.NonceLength);
            var hello2 = new FieldWriter().Raw(LanHandshakeCommon.Magic).Byte(LanHandshakeCommon.TypeHello2)
                .Field(eph.ExportSubjectPublicKeyInfo()).Field(nonceH).Field(state.Salt).ToArray();
            var (c2h, h2c) = LanHandshakeCommon.DeriveKeys(LanHandshakeCommon.Agree(eph, ephemeral), state.Key, nonceC, nonceH, hello1.ToArray(), hello2);
            channel = new SecureChannel(sendKey: h2c, receiveKey: c2h);
            return hello2;
        }
        catch (FormatException ex) { throw new HandshakeException(ex.Message); }
        catch (CryptographicException ex) { throw new HandshakeException(ex.Message); }
    }
}
