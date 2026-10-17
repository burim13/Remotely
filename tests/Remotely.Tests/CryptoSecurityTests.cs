using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Protocol;

namespace Remotely.Tests;

/// <summary>Tests behind the rows of docs/security.md that concern the channel, the two handshakes and the token. Test names are cited there.</summary>
public class CryptoSecurityTests
{
    // ---- helpers ----

    private static (SecureChannel A, SecureChannel B) DevicePair()
    {
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello2 = resp.ProcessHello1(init.CreateHello1(), _ => true, out _, out var responderChannel);
        return (init.ProcessHello2(hello2), responderChannel);
    }

    private static (SecureChannel Controller, SecureChannel Host, byte[] Hello1, byte[] Hello2, LanToken Token) LanPair(LanToken? controllerToken = null)
    {
        var token = LanToken.Create();
        var state = new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(10));
        using var init = new LanInitiatorHandshake(controllerToken ?? token);
        var hello1 = init.CreateHello1();
        var hello2 = new LanResponderHandshake(state).ProcessHello1(hello1, out var host);
        return (init.ProcessHello2(hello2), host, hello1, hello2, token);
    }

    public static IEnumerable<object[]> Transports() => [["device handshake"], ["token handshake"]];

    private static (SecureChannel A, SecureChannel B) Pair(string transport)
    {
        if (transport == "device handshake") return DevicePair();
        var (c, h, _, _, _) = LanPair();
        return (c, h);
    }

    // ---- the encrypted channel: replay, reorder, drop, reflection, other sessions ----

    [Theory]
    [MemberData(nameof(Transports))]
    public void Channel_refuses_replayed_dropped_reordered_reflected_and_foreign_frames(string transport)
    {
        var (a, b) = Pair(transport);
        var (otherA, otherB) = Pair(transport);
        var f0 = a.Seal("zero"u8);
        var f1 = a.Seal("one"u8);
        var f2 = a.Seal("two"u8);

        Assert.ThrowsAny<CryptographicException>(() => b.Open(f1));          // a frame was dropped: counter 1 arrives first
        Assert.ThrowsAny<CryptographicException>(() => a.Open(f0));          // reflected back to its sender: the other direction's key
        Assert.ThrowsAny<CryptographicException>(() => otherB.Open(f0));     // the same device pair or token, another session
        Assert.Equal("zero"u8.ToArray(), b.Open(f0));                        // none of the refusals damaged the channel
        Assert.ThrowsAny<CryptographicException>(() => b.Open(f0));          // replay of the last accepted frame
        Assert.ThrowsAny<CryptographicException>(() => b.Open(f2));          // skipped ahead
        Assert.Equal("one"u8.ToArray(), b.Open(f1));
        Assert.Equal("two"u8.ToArray(), b.Open(f2));
        Assert.ThrowsAny<CryptographicException>(() => b.Open(f1));          // replay of an older frame
        _ = (otherA, otherB);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public void Channel_refuses_a_frame_that_is_cut_extended_or_has_a_changed_counter_or_tag(string transport)
    {
        var (a, b) = Pair(transport);
        var frame = a.Seal("payload"u8);
        Assert.ThrowsAny<CryptographicException>(() => b.Open(frame[..^1]));
        Assert.ThrowsAny<CryptographicException>(() => b.Open([.. frame, 0]));
        Assert.ThrowsAny<CryptographicException>(() => b.Open(frame[..(SecureChannel.Overhead - 1)]));
        Assert.ThrowsAny<CryptographicException>(() => b.Open([]));
        var counter = (byte[])frame.Clone(); counter[7] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => b.Open(counter));
        var tag = (byte[])frame.Clone(); tag[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => b.Open(tag));
        Assert.Equal("payload"u8.ToArray(), b.Open(frame));
    }

    [Fact]
    public void Channel_counter_increases_by_one_per_frame_so_no_nonce_is_ever_used_twice()
    {
        var (a, b) = DevicePair();
        var counters = Enumerable.Range(0, 2000).Select(_ => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(a.Seal("x"u8))).ToList();
        Assert.Equal(Enumerable.Range(0, 2000).Select(i => (ulong)i), counters);
        _ = b;
    }

    [Fact]
    public void Channel_carries_empty_and_maximum_size_messages()
    {
        var (a, b) = DevicePair();
        Assert.Empty(b.Open(a.Seal([])));
        var big = RandomNumberGenerator.GetBytes(ProtocolInfo.MaxRelayBytes - SecureChannel.Overhead);
        var frame = a.Seal(big);
        Assert.Equal(ProtocolInfo.MaxRelayBytes, frame.Length);
        Assert.Equal(big, b.Open(frame));
    }

    [Fact]
    public void Channel_frame_layout_is_counter_then_AES_256_GCM_with_the_documented_nonce_and_associated_data()
    {
        // Independent check of the format written in SecureChannel's comment: nonce = 4 zero bytes || counter, associated data = "RMT1-data".
        var sendKey = Enumerable.Repeat((byte)1, 32).ToArray();
        var receiveKey = Enumerable.Repeat((byte)2, 32).ToArray();
        using var channel = new SecureChannel(sendKey, receiveKey);
        using var reference = new AesGcm(sendKey, 16);
        for (ulong counter = 0; counter < 3; counter++)
        {
            var plaintext = Encoding.ASCII.GetBytes("message " + counter);
            var nonce = new byte[12];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
            var expected = new byte[8 + plaintext.Length + 16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(expected, counter);
            reference.Encrypt(nonce, plaintext, expected.AsSpan(8, plaintext.Length), expected.AsSpan(8 + plaintext.Length, 16), "RMT1-data"u8);
            Assert.Equal(expected, channel.Seal(plaintext));
        }
    }

    [Fact]
    public void Channel_stops_instead_of_wrapping_its_counter()
    {
        var (a, _) = DevicePair();
        var field = typeof(SecureChannel).GetField("_sendCounter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(a, ulong.MaxValue - 1);
        a.Seal("last"u8);
        Assert.Throws<InvalidOperationException>(() => a.Seal("one too many"u8));
    }

    [Fact]
    public void Channel_cannot_be_used_after_it_is_disposed()
    {
        var (a, b) = DevicePair();
        var frame = a.Seal("x"u8);
        b.Dispose();
        a.Dispose();
        Assert.Throws<ObjectDisposedException>(() => a.Seal("y"u8));
        Assert.Throws<ObjectDisposedException>(() => b.Open(frame));
        a.Dispose();   // twice is fine
    }

    // ---- key material hygiene ----

    [Fact]
    public void Zeroing_helpers_overwrite_key_material_and_a_cleared_token_state_has_no_key_left()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var other = RandomNumberGenerator.GetBytes(16);
        HandshakeCommon.Zero(key, null, other);
        Assert.All(key, b => Assert.Equal(0, b));
        Assert.All(other, b => Assert.Equal(0, b));

        var state = new LanTokenState(LanToken.Create(), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Contains(state.Key, b => b != 0);
        state.Clear();
        Assert.All(state.Key, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Handshakes_still_work_after_the_intermediate_keys_are_wiped()
    {
        // The channel must not hold a reference to the arrays that are wiped; it has copied the keys into the cipher.
        var (a, b) = DevicePair();
        var (c, h, _, _, _) = LanPair();
        Assert.Equal("x"u8.ToArray(), b.Open(a.Seal("x"u8)));
        Assert.Equal("y"u8.ToArray(), h.Open(c.Seal("y"u8)));
        Assert.Equal("z"u8.ToArray(), c.Open(h.Seal("z"u8)));
    }

    [Fact]
    public void A_token_never_prints_its_secret()
    {
        var token = LanToken.Create();
        Assert.DoesNotContain(token.Secret, token.ToString());
        Assert.DoesNotContain(token.Secret, $"{token}");
        Assert.Contains(token.Id, token.ToString());
        Assert.DoesNotContain(token.Secret, new LanTokenState(token, DateTimeOffset.UtcNow).Token.ToString());
    }

    // ---- primitives and key stretching: published vectors and pinned parameters ----

    [Fact]
    public void The_platform_HKDF_and_PBKDF2_match_published_test_vectors()
    {
        // RFC 5869, appendix A.1 (HKDF-SHA256, test case 1).
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, Enumerable.Repeat((byte)0x0b, 22).ToArray(),
            42, Convert.FromHexString("000102030405060708090a0b0c"), Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9"));
        Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", Convert.ToHexStringLower(okm));
        // RFC 7914, section 11 (PBKDF2-HMAC-SHA-256, P = "passwd", S = "salt", c = 1, dkLen = 64).
        var dk = Rfc2898DeriveBytes.Pbkdf2("passwd"u8, "salt"u8, 1, HashAlgorithmName.SHA256, 64);
        Assert.Equal("55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc49ca9cccf179b645991664b39d77ef317c71b845b1e30bd509112041d3a19783", Convert.ToHexStringLower(dk));
    }

    [Fact]
    public void Token_key_stretching_is_PBKDF2_HMAC_SHA256_with_300000_rounds_and_a_32_byte_result()
    {
        var token = new LanToken("K7P4", "4NX2QMH9");
        var salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(Rfc2898DeriveBytes.Pbkdf2("4NX2QMH9"u8, salt, 300_000, HashAlgorithmName.SHA256, 32), token.DeriveKey(salt));
        Assert.NotEqual(token.DeriveKey(salt), new LanToken("K7P4", "4NX2QMH8").DeriveKey(salt));
        Assert.NotEqual(token.DeriveKey(salt), token.DeriveKey(salt.Reverse().ToArray()));
    }

    [Fact]
    public void Key_schedules_are_pinned_so_a_change_cannot_slip_in_unnoticed()
    {
        // Expected values come from a separate Python re-implementation of the documented schedule (HKDF-SHA256 over HMAC; salt = SHA-256(nonce1 || nonce2); info = length-prefixed label and transcript hash), not from this code.
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonceA = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var nonceB = Enumerable.Repeat((byte)0xB2, 32).ToArray();
        var transcript = SHA256.HashData("transcript"u8);
        var (i2r, r2i) = HandshakeCommon.DeriveKeys(secret, nonceA, nonceB, transcript);
        Assert.Equal("935ddb105470a7a521fc092890c95b97688aa9cd6615b3548a746e5061ec6024", Convert.ToHexStringLower(i2r));
        Assert.Equal("8d6461de785400fdc8573bb170f8a136185a2c5d11ca95f48d6943ed864cfb2c", Convert.ToHexStringLower(r2i));
        var tokenKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        var (c2h, h2c) = LanHandshakeCommon.DeriveKeys(secret, tokenKey, nonceA, nonceB, "hello1"u8.ToArray(), "hello2"u8.ToArray());
        Assert.Equal("5b68b1eb681c693ac6aedc55c529de87aecde409aa5ff89f5bba13788ed3543f", Convert.ToHexStringLower(c2h));
        Assert.Equal("baa94e7cc3ef1124972dd4c0f667ef29c97874af20c956b31f1a00bf6d3deb0d", Convert.ToHexStringLower(h2c));
    }

    // ---- the device handshake ----

    [Fact]
    public void A_hello_that_fails_authentication_never_reaches_the_trust_decision()
    {
        // The trust decision is where the host shows an approval prompt, so nobody can make a prompt appear with a forged or damaged hello.
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var other = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello1 = init.CreateHello1();
        var asked = 0;
        bool Trust(string _) { asked++; return true; }

        var damaged = (byte[])hello1.Clone(); damaged[^3] ^= 1;
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(damaged, Trust, out _, out _));
        using var addressedElsewhere = new InitiatorHandshake(i, other.DeviceId);
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(addressedElsewhere.CreateHello1(), Trust, out _, out _));
        Assert.Equal(0, asked);

        resp.ProcessHello1(hello1, Trust, out var who, out var channel);
        channel.Dispose();
        Assert.Equal(i.DeviceId, who);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_recorded_hello1_replayed_to_the_host_gives_the_replayer_no_working_channel()
    {
        // Documented limit: the host cannot tell a replayed Hello1 from a new one (it contributes no fresh value to what the initiator signs), so the trust
        // decision runs again. What the replayer cannot do is finish: the keys need the initiator's ephemeral private key.
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var original = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello1 = original.CreateHello1();
        var trustAsked = 0;

        var firstHello2 = resp.ProcessHello1(hello1, _ => { trustAsked++; return true; }, out _, out var firstChannel);
        var replayHello2 = resp.ProcessHello1(hello1, _ => { trustAsked++; return true; }, out _, out var replayChannel);
        Assert.Equal(2, trustAsked);
        Assert.NotEqual(firstHello2, replayHello2);   // fresh responder key and nonce each time

        // Only the initiator that really made Hello1 can use a reply to it ...
        var legit = original.ProcessHello2(replayHello2);
        Assert.Equal("ok"u8.ToArray(), replayChannel.Open(legit.Seal("ok"u8)));
        // ... another party (here: a new initiator with its own ephemeral key and its own Hello1) cannot.
        using var replayer = new InitiatorHandshake(i, r.DeviceId);
        replayer.CreateHello1();
        Assert.Throws<HandshakeException>(() => replayer.ProcessHello2(replayHello2));
        // And what the host sealed for the replayer is unreadable without the keys.
        Assert.ThrowsAny<CryptographicException>(() => firstChannel.Open(replayChannel.Seal("secret"u8)));
    }

    [Fact]
    public void No_single_changed_bit_in_a_signed_hello_is_ever_accepted()
    {
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello1 = init.CreateHello1();
        var hello2 = resp.ProcessHello1(hello1, _ => true, out _, out var channel);
        channel.Dispose();
        for (var bit = 0; bit < hello1.Length * 8; bit++)
        {
            var copy = (byte[])hello1.Clone();
            copy[bit / 8] ^= (byte)(1 << (bit % 8));
            try { resp.ProcessHello1(copy, _ => true, out _, out var accepted); accepted.Dispose(); }
            catch (HandshakeException) { continue; }
            Assert.Fail($"Hello1 with bit {bit} (byte {bit / 8} of {hello1.Length}) changed was accepted");
        }
        for (var bit = 0; bit < hello2.Length * 8; bit++)
        {
            var copy = (byte[])hello2.Clone();
            copy[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.Throws<HandshakeException>(() => init.ProcessHello2(copy));
        }
        init.ProcessHello2(hello2).Dispose();   // the unchanged reply is still fine
    }

    [Fact]
    public void Hellos_cut_short_at_any_length_are_refused()
    {
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello1 = init.CreateHello1();
        var hello2 = resp.ProcessHello1(hello1, _ => true, out _, out var channel);
        channel.Dispose();
        for (var n = 0; n < hello1.Length; n++) { var cut = hello1[..n]; Assert.Throws<HandshakeException>(() => resp.ProcessHello1(cut, _ => true, out _, out _)); }
        for (var n = 0; n < hello2.Length; n++) { var cut = hello2[..n]; Assert.Throws<HandshakeException>(() => init.ProcessHello2(cut)); }

        var token = LanToken.Create();
        var lanResponder = new LanResponderHandshake(new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(1)));
        using var lanInit = new LanInitiatorHandshake(token);
        var lan1 = lanInit.CreateHello1();
        var lan2 = lanResponder.ProcessHello1(lan1, out var lanChannel);
        lanChannel.Dispose();
        for (var n = 0; n < lan1.Length; n++) { var cut = lan1[..n]; Assert.Throws<HandshakeException>(() => lanResponder.ProcessHello1(cut, out _)); }
        for (var n = 0; n < lan2.Length; n++) { var cut = lan2[..n]; Assert.Throws<HandshakeException>(() => lanInit.ProcessHello2(cut)); }
    }

    [Fact]
    public void There_is_no_downgrade_path_any_other_protocol_or_message_type_is_refused_outright()
    {
        // There is one version of each handshake and no negotiation: a message that is not exactly the expected magic and type is an error, and nothing
        // is retried in a weaker mode. The magic ends in the version ("RMT1", "RMTL1" in the key labels).
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello1 = init.CreateHello1();
        var hello2 = resp.ProcessHello1(hello1, _ => true, out _, out var ch);
        ch.Dispose();
        var token = LanToken.Create();
        var lanResponder = new LanResponderHandshake(new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(1)));
        using var lanInit = new LanInitiatorHandshake(token);
        var lan1 = lanInit.CreateHello1();

        // Across protocols.
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(lan1, _ => true, out _, out _));
        Assert.Throws<HandshakeException>(() => lanResponder.ProcessHello1(hello1, out _));
        Assert.Throws<HandshakeException>(() => lanInit.ProcessHello2(hello2));

        // A different magic or version digit, and every message type other than the expected one.
        foreach (var wrongMagic in new[] { "RMT2"u8.ToArray(), "RMT0"u8.ToArray(), "rmt1"u8.ToArray(), "RMTL"u8.ToArray() })
        {
            var h1 = (byte[])hello1.Clone(); wrongMagic.CopyTo(h1, 0);
            Assert.Throws<HandshakeException>(() => resp.ProcessHello1(h1, _ => true, out _, out _));
        }
        for (var type = 0; type < 256; type++)
        {
            if (type == HandshakeCommon.TypeHello1) continue;
            var h1 = (byte[])hello1.Clone(); h1[4] = (byte)type;
            Assert.Throws<HandshakeException>(() => resp.ProcessHello1(h1, _ => true, out _, out _));
        }
        for (var type = 0; type < 256; type++)
        {
            if (type == HandshakeCommon.TypeHello2) continue;
            var h2 = (byte[])hello2.Clone(); h2[4] = (byte)type;
            Assert.Throws<HandshakeException>(() => init.ProcessHello2(h2));
        }
        for (var type = 0; type < 256; type++)
        {
            if (type == LanHandshakeCommon.TypeHello1) continue;
            var h = (byte[])lan1.Clone(); h[4] = (byte)type;
            Assert.Throws<HandshakeException>(() => lanResponder.ProcessHello1(h, out _));
        }
    }

    [Fact]
    public void One_key_cannot_be_presented_under_a_second_device_id_by_re_encoding_it()
    {
        // Found by the bit-flip test: setting the "unused bits" byte of the BIT STRING in a public key to 1 is accepted by the platform's importer when the last
        // bit of the key is 0, so the same key could appear with a different SHA-256 (a different device id). Only the canonical encoding is accepted now.
        DeviceIdentity identity;
        do identity = DeviceIdentity.Generate(); while ((identity.PublicKey[^1] & 1) != 0);
        using var attacker = identity;
        using var victim = DeviceIdentity.Generate();
        var alias = (byte[])attacker.PublicKey.Clone();
        Assert.Equal(0x00, alias[25]);
        alias[25] = 0x01;
        Assert.NotEqual(attacker.DeviceId, DeviceIds.FromPublicKey(alias));

        var signature = attacker.Sign("x"u8);
        Assert.True(DeviceIdentity.Verify(attacker.PublicKey, "x"u8, signature));
        Assert.False(DeviceIdentity.Verify(alias, "x"u8, signature));

        using var resp = new ResponderHandshake(victim);
        var expected = DeviceIds.ToBytes(victim.DeviceId);
        var ephemeral = HandshakeCommon.NewEphemeral().ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var sign = attacker.Sign(HandshakeCommon.SignatureInput("RMT1-I", ephemeral, nonce, expected));
        byte[] Hello(byte[] identityBytes) => new FieldWriter().Raw(HandshakeCommon.Magic).Byte(HandshakeCommon.TypeHello1)
            .Field(identityBytes).Field(ephemeral).Field(nonce).Field(expected).Field(sign).ToArray();
        resp.ProcessHello1(Hello(attacker.PublicKey), _ => true, out var id, out var channel);
        channel.Dispose();
        Assert.Equal(attacker.DeviceId, id);
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(Hello(alias), _ => true, out _, out _));
    }

    [Fact]
    public void A_public_key_with_a_damaged_curve_identifier_is_refused_cleanly_everywhere()
    {
        // Found by the fuzz tests: on Linux a key whose curve OID is damaged made the platform throw PlatformNotSupportedException, which no caller expected.
        using var attacker = DeviceIdentity.Generate();
        using var victim = DeviceIdentity.Generate();
        byte[] Damaged(byte[] spki)
        {
            var copy = (byte[])spki.Clone();
            var oid = new byte[] { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 };
            var at = copy.AsSpan().LastIndexOf(oid);
            Assert.True(at >= 0);
            copy[at + 2] ^= 0x01;
            return copy;
        }
        Assert.False(DeviceIdentity.Verify(Damaged(attacker.PublicKey), "x"u8, attacker.Sign("x"u8)));
        Assert.Throws<CryptographicException>(() =>
        {
            var pkcs8 = attacker.ExportPkcs8();
            var oid = new byte[] { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 };
            pkcs8[pkcs8.AsSpan().IndexOf(oid) + 2] ^= 0x01;
            DeviceIdentity.FromPkcs8(pkcs8);
        });

        using var resp = new ResponderHandshake(victim);
        var expected = DeviceIds.ToBytes(victim.DeviceId);
        var ephemeral = Damaged(HandshakeCommon.NewEphemeral().ExportSubjectPublicKeyInfo());
        var nonce = new byte[32];
        var hello1 = new FieldWriter().Raw(HandshakeCommon.Magic).Byte(HandshakeCommon.TypeHello1).Field(attacker.PublicKey).Field(ephemeral).Field(nonce).Field(expected)
            .Field(attacker.Sign(HandshakeCommon.SignatureInput("RMT1-I", ephemeral, nonce, expected))).ToArray();
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(hello1, _ => true, out _, out _));

        var token = LanToken.Create();
        var lanResponder = new LanResponderHandshake(new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(1)));
        var lanHello1 = new FieldWriter().Raw(LanHandshakeCommon.Magic).Byte(LanHandshakeCommon.TypeHello1).Field(ephemeral).Field(nonce).Field(Encoding.ASCII.GetBytes(token.Id)).ToArray();
        Assert.Throws<HandshakeException>(() => lanResponder.ProcessHello1(lanHello1, out _));
    }

    // ---- the token handshake ----

    [Fact]
    public void The_token_secret_and_the_stretched_key_never_appear_in_what_goes_on_the_wire()
    {
        var token = LanToken.Create();
        var state = new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(10));
        using var init = new LanInitiatorHandshake(token);
        var hello1 = init.CreateHello1();
        var hello2 = new LanResponderHandshake(state).ProcessHello1(hello1, out var host);
        var controller = init.ProcessHello2(hello2);
        var proof = controller.Seal([.. "RMTL1-proof:"u8.ToArray(), .. "viewer-pc"u8.ToArray()]);
        var answer = host.Seal("RMTL1-accepted"u8);

        foreach (var wire in new[] { hello1, hello2, proof, answer })
        {
            Assert.True(wire.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token.Secret)) < 0);
            Assert.True(wire.AsSpan().IndexOf(state.Key) < 0);
            Assert.True(wire.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token.Secret[..4])) < 0);
        }
        Assert.True(hello1.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token.Id)) >= 0, "the label is public by design");
    }

    [Fact]
    public void A_wrong_token_gives_channels_that_cannot_read_each_other()
    {
        var real = LanToken.Create();
        var wrong = new LanToken(real.Id, real.Secret == "AAAAAAAA" ? "BBBBBBBB" : "AAAAAAAA");
        var state = new LanTokenState(real, DateTimeOffset.UtcNow.AddMinutes(10));
        using var init = new LanInitiatorHandshake(wrong);
        var hello2 = new LanResponderHandshake(state).ProcessHello1(init.CreateHello1(), out var host);
        var controller = init.ProcessHello2(hello2);   // the controller cannot tell yet
        Assert.ThrowsAny<CryptographicException>(() => host.Open(controller.Seal("RMTL1-proof:x"u8)));
        Assert.ThrowsAny<CryptographicException>(() => controller.Open(host.Seal("RMTL1-accepted"u8)));
    }

    [Fact]
    public void Changing_any_part_of_the_token_hello2_in_transit_leaves_the_two_sides_unable_to_talk()
    {
        // The token handshake has no signatures; the transcript hash in the key derivation is what ties the keys to the exact bytes exchanged.
        var token = LanToken.Create();
        var state = new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(10));
        var rng = new Random(77);
        var positions = Enumerable.Range(0, 20).Select(_ => rng.Next(0, 160 * 8)).ToList();
        foreach (var bit in positions)
        {
            using var init = new LanInitiatorHandshake(token);
            var hello2 = new LanResponderHandshake(state).ProcessHello1(init.CreateHello1(), out var host);
            var changed = (byte[])hello2.Clone();
            var at = (bit / 8) % changed.Length;
            changed[at] ^= (byte)(1 << (bit % 8));
            try
            {
                var controller = init.ProcessHello2(changed);
                Assert.ThrowsAny<CryptographicException>(() => host.Open(controller.Seal("RMTL1-proof:x"u8)));
            }
            catch (HandshakeException) { /* refused outright is also fine */ }
        }
    }

    [Fact]
    public void An_active_attacker_posing_as_the_host_can_test_token_guesses_offline_this_is_the_documented_limit()
    {
        // This is NOT a password-authenticated key exchange. A party that answers the controller's Hello1 with its own Hello2 (it only needs the public label)
        // receives the controller's first encrypted message and can then check guesses for the 8-character secret on its own computer, each guess costing one
        // PBKDF2 run (300000 rounds). The test performs that attack with one wrong and one right guess to show it works; the cost is worked out in docs/security.md.
        var real = LanToken.Create();
        using var controller = new LanInitiatorHandshake(real);
        var hello1 = controller.CreateHello1();
        var reader = new FieldReader(hello1);
        reader.Raw(5);
        var controllerEphemeral = reader.Field().ToArray();
        var nonceC = reader.Field().ToArray();

        using var attackerKey = HandshakeCommon.NewEphemeral();
        var nonceH = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hello2 = new FieldWriter().Raw(LanHandshakeCommon.Magic).Byte(LanHandshakeCommon.TypeHello2)
            .Field(attackerKey.ExportSubjectPublicKeyInfo()).Field(nonceH).Field(salt).ToArray();
        var proof = controller.ProcessHello2(hello2).Seal([.. "RMTL1-proof:"u8.ToArray(), .. "viewer-pc"u8.ToArray()]);

        bool Guess(string secret)
        {
            var tokenKey = new LanToken(real.Id, secret).DeriveKey(salt);
            var (c2h, h2c) = LanHandshakeCommon.DeriveKeys(LanHandshakeCommon.Agree(attackerKey, controllerEphemeral), tokenKey, nonceC, nonceH, hello1, hello2);
            using var channel = new SecureChannel(sendKey: h2c, receiveKey: c2h);
            try { channel.Open(proof); return true; }
            catch (CryptographicException) { return false; }
        }
        Assert.False(Guess(real.Secret == "AAAAAAAA" ? "BBBBBBBB" : "AAAAAAAA"));
        Assert.True(Guess(real.Secret));
    }

    [Fact]
    public async Task However_many_connections_arrive_at_once_only_three_tries_are_ever_checked_against_one_token()
    {
        var real = LanToken.Create();
        var wrong = new LanToken(real.Id, real.Secret == "AAAAAAAA" ? "BBBBBBBB" : "AAAAAAAA");
        var state = new LanTokenState(real, DateTimeOffset.UtcNow.AddMinutes(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var outcomes = new ConcurrentBag<string>();
        const int attempts = 48;

        var serving = Task.Run(async () =>
        {
            var handlers = new List<Task>();
            for (var n = 0; n < attempts; n++)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token);
                handlers.Add(Task.Run(async () =>
                {
                    using (client)
                    {
                        var wire = new FramedStream(client.GetStream());
                        try { (await LanSecureSession.AcceptAsync(wire, state, cts.Token)).Session.Dispose(); outcomes.Add("accepted"); }
                        catch (LanAuthException ex) { outcomes.Add(ex.Message); }
                        catch (Exception ex) { outcomes.Add("other: " + ex.GetType().Name); }
                    }
                }));
            }
            await Task.WhenAll(handlers);
        });
        var clients = Enumerable.Range(0, attempts).Select(async _ =>
        {
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            try { await LanSecureSession.InitiateAsync(new FramedStream(raw.GetStream()), wrong, "x", cts.Token); }
            catch (Exception ex) when (ex is LanAuthException or HandshakeException or IOException or CryptographicException) { }
        }).ToList();
        await Task.WhenAll(clients);
        await serving;

        Assert.Equal(3, outcomes.Count(o => o == "Wrong token."));
        Assert.Equal(attempts - 3, outcomes.Count(o => o.Contains("tried too often")));
        Assert.DoesNotContain("accepted", outcomes);

        // The real token is also refused once the tries are used up: the host replaces the token after wrong tries, so the old one is dead either way.
        var late = new TcpListener(IPAddress.Loopback, 0);
        late.Start();
        var lateServer = Task.Run(async () =>
        {
            using var client = await late.AcceptTcpClientAsync(cts.Token);
            await Assert.ThrowsAsync<LanAuthException>(() => LanSecureSession.AcceptAsync(new FramedStream(client.GetStream()), state, cts.Token));
        });
        using var lateClient = new TcpClient();
        await lateClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)late.LocalEndpoint).Port, cts.Token);
        try { await LanSecureSession.InitiateAsync(new FramedStream(lateClient.GetStream()), real, "x", cts.Token); } catch (Exception ex) when (ex is LanAuthException or HandshakeException or IOException) { }
        await lateServer;
        late.Stop();
    }

    // ---- audit: comparisons of secret-dependent values ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_equality_check_in_the_security_code_is_on_a_public_value_or_constant_time_and_new_ones_need_review()
    {
        // The audit in docs/security.md ("Constant-time comparisons") is pinned here: any new SequenceEqual or Equals( in these files fails until it is
        // reviewed and added with its reason. Comparisons of keys, tags and checksums use CryptographicOperations.FixedTimeEquals or the AES-GCM tag check.
        var allowed = new (string File, string Fragment, string Why)[]
        {
            ("Crypto/Handshake.cs", "r.Raw(4).SequenceEqual(HandshakeCommon.Magic)", "public protocol magic, twice"),
            ("Crypto/Handshake.cs", "expected.AsSpan().SequenceEqual(DeviceIds.ToBytes(self.DeviceId))", "public device id the sender named"),
            ("Crypto/DeviceIdentity.cs", "ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKeySpki)", "two encodings of a public key"),
            ("Lan/LanHandshake.cs", "r.Raw(4).SequenceEqual(LanHandshakeCommon.Magic)", "public protocol magic, twice"),
            ("Lan/LanHandshake.cs", "Equals(state.Token.Id, StringComparison.Ordinal)", "the public 4-character label, sent in clear by design"),
            ("Lan/LanSecureSession.cs", "SequenceEqual(AcceptedMessage)", "public constant compared with a decrypted, authenticated message"),
            ("Lan/LanSecureSession.cs", "SequenceEqual(ProofPrefix)", "public constant compared with a decrypted, authenticated message"),
        };
        var root = Path.Combine(RepoRoot(), "src", "Remotely.Core");
        var files = Directory.GetFiles(Path.Combine(root, "Crypto"), "*.cs").Concat(new[] { "Lan/LanToken.cs", "Lan/LanHandshake.cs", "Lan/LanSecureSession.cs", "Lan/FramedStream.cs" }.Select(f => Path.Combine(root, f)));
        var found = new List<(string File, string Line)>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var line in File.ReadAllLines(file))
                if (line.Contains("SequenceEqual(") || line.Contains(".Equals(") || Regex.IsMatch(line, @"\b(Secret|tokenKey|secret|Key|key)\b\s*(==|!=)"))
                    found.Add((relative, line.Trim()));
        }
        foreach (var (file, line) in found)
            Assert.True(allowed.Any(a => a.File == file && line.Contains(a.Fragment)), $"Unreviewed comparison in {file}: {line}");
        foreach (var a in allowed)
            Assert.True(found.Any(f => f.File == a.File && f.Line.Contains(a.Fragment)), $"The reviewed comparison is gone or changed, update the audit: {a.File}: {a.Fragment}");
    }

    [Fact]
    public void Device_ids_are_the_sha256_of_the_public_key_and_short_ids_are_for_people_only()
    {
        using var id = DeviceIdentity.Generate();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(id.PublicKey)), id.DeviceId);
        Assert.Equal(id.DeviceId[..4] + "-" + id.DeviceId[4..8] + "-" + id.DeviceId[8..12], DeviceIds.Short(id.DeviceId));
        Assert.Equal(32, DeviceIds.ToBytes(id.DeviceId).Length);
    }
}
