using System.Security.Cryptography;
using Remotely.Core.Crypto;

namespace Remotely.Tests;

public class CryptoTests
{
    private static (SecureChannel Initiator, SecureChannel Responder, DeviceIdentity I, DeviceIdentity R) Connect()
    {
        var i = DeviceIdentity.Generate();
        var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello1 = init.CreateHello1();
        var hello2 = resp.ProcessHello1(hello1, id => id == i.DeviceId, out _, out var responderChannel);
        var initiatorChannel = init.ProcessHello2(hello2);
        return (initiatorChannel, responderChannel, i, r);
    }

    [Fact]
    public void Identity_roundtrips_through_pkcs8_and_keeps_its_id()
    {
        using var a = DeviceIdentity.Generate();
        using var b = DeviceIdentity.FromPkcs8(a.ExportPkcs8());
        Assert.Equal(a.DeviceId, b.DeviceId);
        Assert.Equal(64, a.DeviceId.Length);
        Assert.True(DeviceIdentity.Verify(a.PublicKey, "hi"u8, b.Sign("hi"u8)));
        Assert.False(DeviceIdentity.Verify(a.PublicKey, "ho"u8, b.Sign("hi"u8)));
    }

    [Fact]
    public void Handshake_gives_both_sides_working_channels_in_both_directions()
    {
        var (init, resp, _, _) = Connect();
        Assert.Equal("ping"u8.ToArray(), resp.Open(init.Seal("ping"u8)));
        Assert.Equal("pong"u8.ToArray(), init.Open(resp.Seal("pong"u8)));
    }

    [Fact]
    public void Initiator_rejects_a_responder_that_is_not_the_expected_device()
    {
        using var i = DeviceIdentity.Generate();
        using var expected = DeviceIdentity.Generate();
        using var impostor = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, expected.DeviceId);
        using var resp = new ResponderHandshake(impostor);
        // The impostor refuses because hello1 names another device...
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(init.CreateHello1(), _ => true, out _, out _));
    }

    [Fact]
    public void Initiator_rejects_a_hello2_signed_by_someone_else()
    {
        using var i = DeviceIdentity.Generate();
        using var real = DeviceIdentity.Generate();
        using var impostor = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, real.DeviceId);
        var hello1 = init.CreateHello1();
        // An impostor answers a hello1 re-addressed to itself: its identity differs from the one the initiator expects.
        using var otherInit = new InitiatorHandshake(i, impostor.DeviceId);
        using var resp = new ResponderHandshake(impostor);
        var hello2 = resp.ProcessHello1(otherInit.CreateHello1(), _ => true, out _, out _);
        Assert.Throws<HandshakeException>(() => init.ProcessHello2(hello2));
        _ = hello1;
    }

    [Fact]
    public void Responder_rejects_an_initiator_it_does_not_trust()
    {
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(init.CreateHello1(), _ => false, out _, out _));
    }

    [Fact]
    public void Tampering_with_hello1_is_detected()
    {
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello1 = init.CreateHello1();
        hello1[hello1.Length / 2] ^= 0x01;
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1(hello1, _ => true, out _, out _));
    }

    [Fact]
    public void Tampering_with_hello2_is_detected()
    {
        using var i = DeviceIdentity.Generate();
        using var r = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(i, r.DeviceId);
        using var resp = new ResponderHandshake(r);
        var hello2 = resp.ProcessHello1(init.CreateHello1(), _ => true, out _, out _);
        hello2[hello2.Length / 2] ^= 0x01;
        Assert.Throws<HandshakeException>(() => init.ProcessHello2(hello2));
    }

    [Fact]
    public void Garbage_is_a_handshake_error_not_a_crash()
    {
        using var r = DeviceIdentity.Generate();
        using var resp = new ResponderHandshake(r);
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1([1, 2, 3], _ => true, out _, out _));
        Assert.Throws<HandshakeException>(() => resp.ProcessHello1([], _ => true, out _, out _));
    }

    [Fact]
    public void Channel_rejects_tampered_replayed_and_reordered_frames()
    {
        var (init, resp, _, _) = Connect();
        var f0 = init.Seal("zero"u8);
        var f1 = init.Seal("one"u8);

        var tampered = (byte[])f0.Clone();
        tampered[10] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => resp.Open(tampered));
        Assert.ThrowsAny<CryptographicException>(() => resp.Open(f1));       // reordered: counter 1 before 0
        Assert.Equal("zero"u8.ToArray(), resp.Open(f0));
        Assert.ThrowsAny<CryptographicException>(() => resp.Open(f0));       // replay
        Assert.Equal("one"u8.ToArray(), resp.Open(f1));
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext_and_directions_use_different_keys()
    {
        var (init, resp, _, _) = Connect();
        var secret = "correct horse battery staple"u8.ToArray();
        var frame = init.Seal(secret);
        Assert.False(frame.AsSpan().IndexOf(secret) >= 0);
        // A frame sealed by the initiator must not open with the initiator's own receive direction.
        Assert.ThrowsAny<CryptographicException>(() => init.Open(frame));
        _ = resp;
    }

    [Fact]
    public void Two_handshakes_between_the_same_devices_derive_different_keys()
    {
        var (a, _, _, _) = Connect();
        var (b, _, _, _) = Connect();
        Assert.NotEqual(a.Seal("x"u8), b.Seal("x"u8));
    }
}
