using Remotely.Core.Protocol;
using Remotely.HostCore;
using Remotely.Server;

namespace Remotely.Tests;

public class InviteTests
{
    private const string Pin = "e3411e89584cc29534e669f1f94b3fd7b40abe129b0df653ab771638bda41aad";

    [Fact]
    public void An_invite_round_trips_the_address_pin_and_code()
    {
        var text = new Invite("wss://192.168.1.50:8443/ws", Pin, "F4WG-4FFJ").Encode();
        Assert.StartsWith("RMT1-", text);
        Assert.True(Invite.TryParse(text, out var back, out var error), error);
        Assert.Equal("wss://192.168.1.50:8443/ws", back!.ServerUri);
        Assert.Equal(Pin, back.PinHex);
        Assert.Equal("F4WG-4FFJ", back.Code);
    }

    [Fact]
    public void A_pasted_invite_may_contain_line_breaks_and_any_letter_case_in_the_prefix()
    {
        var text = new Invite("wss://home.example.com:8443/ws", Pin, "ABCD-2345").Encode();
        var messy = "  rmt1-" + text["RMT1-".Length..][..20] + "\r\n" + text["RMT1-".Length..][20..] + " \n";
        Assert.True(Invite.TryParse(messy, out var back, out var error), error);
        Assert.Equal("ABCD-2345", back!.Code);
    }

    [Fact]
    public void A_mistyped_or_cut_off_invite_is_refused_with_a_plain_message()
    {
        var text = new Invite("wss://home.example.com:8443/ws", Pin, "ABCD-2345").Encode();
        var typo = text[..30] + (text[30] == 'A' ? 'B' : 'A') + text[31..];
        Assert.False(Invite.TryParse(typo, out _, out var typoError));
        Assert.Contains("typing mistake", typoError);
        Assert.False(Invite.TryParse(text[..^6], out _, out _));
        Assert.False(Invite.TryParse("hello", out _, out var notInvite));
        Assert.Contains("RMT1-", notInvite);
        Assert.False(Invite.TryParse("", out _, out _));
    }

    [Fact]
    public void An_invite_for_a_server_without_tls_carries_no_pin()
    {
        var text = new Invite("ws://127.0.0.1:19443/ws", "", "ABCD-2345").Encode();
        Assert.True(Invite.TryParse(text, out var back, out _));
        Assert.Equal("", back!.PinHex);
    }

    // Built independently in Python from the documented layout. The Swift test suite parses this exact string too, so both implementations must agree.
    internal const string KnownInvite = "RMT1-AeNBHolYTMKVNOZp8flLP9e0Cr4Smw32U6t3Fji9pBqtRjRXRzRGRkp3c3M6Ly8xOTIuMTY4LjEuNTA6ODQ0My93c56b7g";

    [Fact]
    public void A_known_invite_made_outside_this_code_decodes_and_encodes_identically()
    {
        Assert.True(Invite.TryParse(KnownInvite, out var invite, out var error), error);
        Assert.Equal("wss://192.168.1.50:8443/ws", invite!.ServerUri);
        Assert.Equal(Pin, invite.PinHex);
        Assert.Equal("F4WG-4FFJ", invite.Code);
        Assert.Equal(KnownInvite, invite.Encode());
    }

    [Fact]
    public void An_invite_code_works_the_stated_number_of_times_then_never_again()
    {
        var codes = new PairingCodes(TimeProvider.System);
        var (code, _) = codes.Create(uses: 3, lifetime: TimeSpan.FromHours(1));
        for (var i = 0; i < 3; i++) Assert.True(codes.TryConsume("10.0.0." + i, code));
        Assert.False(codes.TryConsume("10.0.0.9", code));
    }

    [Fact]
    public void A_plain_pairing_code_still_works_once()
    {
        var codes = new PairingCodes(TimeProvider.System);
        var (code, _) = codes.Create();
        Assert.True(codes.TryConsume("a", code));
        Assert.False(codes.TryConsume("b", code));
    }

    [Fact]
    public void Family_mode_trusts_any_paired_device_but_still_honours_view_only()
    {
        var strict = new HostConfig { TrustedViewers = ["aaaa"], ViewOnlyViewers = ["bbbb"] };
        Assert.True(strict.CanControl("aaaa"));
        Assert.False(strict.IsTrusted("cccc"));
        Assert.True(strict.IsTrusted("bbbb")); Assert.False(strict.CanControl("bbbb"));

        var family = new HostConfig { TrustAllPaired = true, ViewOnlyViewers = ["bbbb"] };
        Assert.True(family.CanControl("cccc"));
        Assert.True(family.IsTrusted("cccc"));
        Assert.True(family.IsTrusted("bbbb")); Assert.False(family.CanControl("bbbb"));
    }
}
