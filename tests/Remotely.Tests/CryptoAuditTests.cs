using System.Text;
using Remotely.Core.Crypto;

namespace Remotely.Tests;

public class CryptoAuditTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 16, 23, 46, 5, 123, TimeSpan.Zero);

    [Fact]
    public void An_audit_line_has_the_documented_shape()
    {
        var line = new AuditEvent(Time, AuditKind.ConnectionAccepted, "lan", "viewer-pc", "192.168.1.5", "").Format();
        Assert.Equal("2026-10-16T23:46:05.123Z remotely-audit v=1 event=connection.accepted mode=\"lan\" peer=\"viewer-pc\" addr=\"192.168.1.5\" detail=\"\"", line);
    }

    [Fact]
    public void A_name_chosen_by_another_pc_cannot_start_a_new_line_or_add_a_field()
    {
        var hostile = "pc\r\n2026-01-01T00:00:00.000Z remotely-audit v=1 event=consent.granted mode=\"lan\" peer=\"admin\"\u0000\u001b[31m\u2028x\" addr=\"1.1.1.1";
        var line = new AuditEvent(Time, AuditKind.ConnectionAttempt, "lan", hostile, "a\"b\\c", "d").Format();

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.All(line, c => Assert.False(char.IsControl(c) || c is '\u2028' or '\u2029'));
        Assert.True(AuditEvent.TryParse(line, out var parsed));
        Assert.Equal(AuditKind.ConnectionAttempt, parsed!.Kind);   // the planted "consent.granted" stayed inside the peer value
        Assert.Equal(hostile.Length > AuditEvent.MaxValueLength ? hostile[..AuditEvent.MaxValueLength] : hostile, parsed.Peer);
        Assert.Equal("a\"b\\c", parsed.Address);
        Assert.Equal("d", parsed.Detail);
    }

    [Fact]
    public void Every_event_kind_has_its_own_stable_name_and_survives_a_round_trip()
    {
        var names = new HashSet<string>();
        foreach (var kind in Enum.GetValues<AuditKind>())
        {
            var original = new AuditEvent(Time, kind, "relay", "ab12-cd34-ef56", "10.0.0.2", "reason é中");
            Assert.True(names.Add(AuditEvent.Name(kind)), "duplicate name " + AuditEvent.Name(kind));
            Assert.True(AuditEvent.TryParse(original.Format(), out var back));
            Assert.Equal(original, back);
        }
        Assert.Equal(Enum.GetValues<AuditKind>().Length, names.Count);
    }

    [Fact]
    public void Long_values_are_cut_and_random_text_always_round_trips()
    {
        var rng = new Random(2026);
        for (var i = 0; i < Fuzz.Budget; i++)
        {
            var peer = Encoding.UTF8.GetString(Fuzz.Noise(rng, 300));
            var detail = string.Concat(Fuzz.Texts(rng.Next(), 1, "ab \"\\\n\t=", 250));
            var line = new AuditEvent(Time, (AuditKind)rng.Next(Enum.GetValues<AuditKind>().Length), "lan", peer, "", detail).Format();
            Assert.DoesNotContain('\n', line);
            Assert.True(AuditEvent.TryParse(line, out var back), line);
            Assert.Equal(AuditEvent.Name(back!.Kind), line.Split(' ')[3][6..]);
            Assert.True(back.Peer.Length <= AuditEvent.MaxValueLength);
            Assert.Equal(peer.Length > AuditEvent.MaxValueLength ? peer[..AuditEvent.MaxValueLength] : peer, back.Peer);
        }
    }

    [Fact]
    public void Lines_that_are_not_audit_lines_are_rejected_without_an_exception()
    {
        var valid = new AuditEvent(Time, AuditKind.TokenIssued, "lan").Format();
        foreach (var input in Fuzz.Inputs(55, Fuzz.Budget, [Encoding.UTF8.GetBytes(valid)], 120))
        {
            var text = Encoding.UTF8.GetString(input);
            try { AuditEvent.TryParse(text, out _); }
            catch (Exception ex) { Assert.Fail($"TryParse threw {ex.GetType().Name} for: {text}"); }
        }
        Assert.False(AuditEvent.TryParse("", out _));
        Assert.False(AuditEvent.TryParse(valid.Replace("v=1", "v=2"), out _));
        Assert.False(AuditEvent.TryParse(valid.Replace("token.issued", "token.unknown"), out _));
    }

    [Fact]
    public void The_format_has_no_field_that_could_carry_a_secret()
    {
        var fields = typeof(AuditEvent).GetProperties().Where(p => p.DeclaringType == typeof(AuditEvent) && p.Name != "EqualityContract").Select(p => p.Name);
        Assert.Equal(["TimeUtc", "Kind", "Mode", "Peer", "Address", "Detail"], fields);
    }
}
