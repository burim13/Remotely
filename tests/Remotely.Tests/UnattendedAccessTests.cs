using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Remotely.Host.Unattended;
using Remotely.Unattended;

namespace Remotely.Tests;

/// <summary>
/// Who may talk to the host service over the pipe (agent A13). What is tested here is the logic: the access list as a security descriptor (parsed back with the framework's own
/// SDDL reader, not by string matching), the second check after a client connected, and the server loop around them. That Windows enforces the access list when the pipe is
/// opened, and that a second local user is refused, can only be shown on Windows: see docs/unattended.md.
/// </summary>
public class UnattendedAccessTests
{
    private const string User = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string OtherUser = "S-1-5-21-1004336348-1177238915-682003330-1002";

    /// <summary>One entry of the access list as written in the descriptor text: (type;flags;rights;;;sid).</summary>
    private sealed record Ace(string Type, string Flags, int Rights, string Sid);

    /// <summary>
    /// A deliberately small reader for the shape this policy produces ("D:" + optional flags + a run of parenthesised entries). The framework's own parser (RawSecurityDescriptor) is
    /// Windows-only, so it cannot run in the Linux test job; <see cref="Windows_accepts_the_descriptor_text"/> uses it where it exists.
    /// </summary>
    private static (string Flags, List<Ace> Aces) Read(string? sid)
    {
        var text = PipeAccessPolicy.Sddl(sid);
        Assert.StartsWith("D:", text);
        var firstParen = text.IndexOf('(');
        var flags = text[2..firstParen];
        var aces = new List<Ace>();
        foreach (var part in text[firstParen..].Split(')', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith("(", part);
            var fields = part[1..].Split(';');
            Assert.Equal(6, fields.Length);
            Assert.Equal("", fields[3]);   // no object GUIDs
            Assert.Equal("", fields[4]);
            Assert.StartsWith("0x", fields[2]);   // always numbers, never generic-rights letters
            var rights = Convert.ToInt32(fields[2][2..], 16);
            aces.Add(new Ace(fields[0], fields[1], rights, fields[5]));
        }
        return (flags, aces);
    }

    [Fact]
    public void The_access_list_names_the_service_and_the_signed_in_user_and_nobody_else()
    {
        var (_, aces) = Read(User);
        Assert.Equal(3, aces.Count);
        Assert.Equal(new Ace("D", "", PipeAccessPolicy.AllAccess, "NU"), aces[0]);                   // network logons are refused first
        Assert.Equal(new Ace("A", "", PipeAccessPolicy.AllAccess, "SY"), aces[1]);                   // SYSTEM, which is what the service runs as
        Assert.Equal(new Ace("A", "", PipeAccessPolicy.UserRights, User), aces[2]);  // the one user
    }

    [Fact]
    public void Nobody_else_is_named_not_Everyone_not_Users_not_Authenticated_Users_not_Administrators()
    {
        var allowed = Read(User).Aces.Where(a => a.Type == "A").Select(a => a.Sid).ToList();
        Assert.Equal(["SY", User], allowed);
        foreach (var broad in new[] { "WD", "BU", "AU", "BA", "AN", "IU", "S-1-1-0", "S-1-5-11", "S-1-5-32-544", "S-1-5-32-545", OtherUser })
            Assert.DoesNotContain(broad, allowed);
    }

    [Fact]
    public void The_list_does_not_inherit_anything_and_the_user_cannot_create_another_instance_of_the_pipe()
    {
        var (flags, aces) = Read(User);
        Assert.Equal("P", flags);   // protected: nothing is inherited
        var rights = aces[2].Rights;
        Assert.Equal(0, rights & (int)PipeAccessRights.CreateNewInstance);
        Assert.Equal(0, rights & (int)(PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership | PipeAccessRights.Delete | PipeAccessRights.AccessSystemSecurity));
        // what a client needs to talk over the pipe
        Assert.Equal((int)(PipeAccessRights.ReadData | PipeAccessRights.WriteData), rights & (int)(PipeAccessRights.ReadData | PipeAccessRights.WriteData));
    }

    [Fact]
    public void With_nobody_signed_in_only_the_service_remains()
    {
        var (_, aces) = Read(null);
        Assert.Equal([new Ace("D", "", PipeAccessPolicy.AllAccess, "NU"), new Ace("A", "", PipeAccessPolicy.AllAccess, "SY")], aces);
    }

    [Fact]
    public void Windows_accepts_the_descriptor_text()
    {
        if (!OperatingSystem.IsWindows()) return;   // RawSecurityDescriptor exists only there; this test therefore does not run in the Linux CI job
        var descriptor = new RawSecurityDescriptor(PipeAccessPolicy.Sddl(User));
        Assert.Equal(3, descriptor.DiscretionaryAcl!.Count);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Equal(new SecurityIdentifier(User), ((CommonAce)descriptor.DiscretionaryAcl[2]).SecurityIdentifier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5-21-1)(A;;GA;;;WD")]
    [InlineData("WD")]
    [InlineData("S-1-5-21-1 ")]
    [InlineData("S-1-")]
    [InlineData("s-1-5-21-1-2")]
    [InlineData("S-1-5-21-1-2)(A;;GA;;;WD)")]
    public void Text_that_is_not_a_SID_cannot_become_part_of_the_access_list(string text)
    {
        Assert.False(PipeAccessPolicy.IsSid(text));
        Assert.Throws<ArgumentException>(() => PipeAccessPolicy.Sddl(text));
    }

    [Theory]
    [InlineData(User)]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-12-1-1234-5678-9012-3456")]
    public void Real_SIDs_are_accepted(string sid)
    {
        Assert.True(PipeAccessPolicy.IsSid(sid));
        Assert.Equal(3, Read(sid).Aces.Count);
    }

    [Fact]
    public void The_second_check_lets_in_only_the_signed_in_user_in_the_console_session_on_this_PC()
    {
        PeerVerdict Decide(PeerInfo peer, string? sid = User, uint? session = 1) => PipeAccessPolicy.Decide(peer, sid, session);

        Assert.Equal(PeerVerdict.Allowed, Decide(new(User, 1, false)));
        Assert.Equal(PeerVerdict.Allowed, Decide(new(User.ToLowerInvariant().Replace("s-", "S-"), 1, false)));   // SID text is not case sensitive
        Assert.Equal(PeerVerdict.WrongUser, Decide(new(OtherUser, 1, false)));
        Assert.Equal(PeerVerdict.WrongSession, Decide(new(User, 2, false)));          // the same user signed in elsewhere (for example remote desktop)
        Assert.Equal(PeerVerdict.WrongSession, Decide(new(User, null, false)));
        Assert.Equal(PeerVerdict.Remote, Decide(new(User, 1, true)));                 // over the network, even with the right name
        Assert.Equal(PeerVerdict.Unidentified, Decide(new(null, 1, false)));
        Assert.Equal(PeerVerdict.NoUser, Decide(new(User, 1, false), sid: null));     // nobody is signed in: nobody is let in
        Assert.Equal(PeerVerdict.Allowed, Decide(new(User, 99, false), session: null)); // session not known: the user check alone decides
    }

    [Fact]
    public void Another_user_is_never_let_in_whatever_else_is_true_of_them()
    {
        foreach (var session in new uint?[] { null, 0, 1, 2 })
        foreach (var remote in new[] { false, true })
        foreach (var expectedSession in new uint?[] { null, 1 })
            Assert.NotEqual(PeerVerdict.Allowed, PipeAccessPolicy.Decide(new PeerInfo(OtherUser, session, remote), User, expectedSession));
    }

    // ---- the server loop, on a real pipe, with a fake "who is this" check ----

    private sealed class FakeBackend : IUnattendedBackend
    {
        public int Calls;
        public Task<PipeReply> HandleAsync(PipeRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (request.Cmd == "boom") throw new InvalidOperationException("boom");
            return Task.FromResult(new PipeReply { Ok = true, Error = "got " + request.Cmd });
        }
    }

    private sealed class FakeCheck(Func<PeerContext, PeerVerdict> verdict) : IPeerCheck
    {
        public int Checks;
        public PeerVerdict Check(NamedPipeServerStream pipe, PeerContext expected)
        {
            Interlocked.Increment(ref Checks);
            return verdict(expected);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly string Name = "rmt-unattended-srv-" + Guid.NewGuid().ToString("N");
        public readonly FakeBackend Backend = new();
        public readonly List<string> Log = [];
        public readonly List<(string? Sid, bool First)> Created = [];
        public PeerContext Context = new(User, 1);
        public readonly CancellationTokenSource Cts = new(TimeSpan.FromSeconds(30));
        public readonly FakeCheck Check;
        private readonly Task _run;

        public Rig(Func<PeerContext, PeerVerdict>? verdict = null, TimeSpan? idle = null)
        {
            Check = new FakeCheck(verdict ?? (expected => expected.UserSid is null ? PeerVerdict.NoUser : PeerVerdict.Allowed));   // as the real policy: nobody signed in, nobody let in
            var server = new UnattendedServer(Backend, () => Context, (sid, first) =>
            {
                lock (Created) Created.Add((sid, first));
                return new NamedPipeServerStream(Name, PipeDirection.InOut, UnattendedPipe.MaxServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }, Check, line => { lock (Log) Log.Add(line); })
            { ContextPoll = TimeSpan.FromMilliseconds(50), IdleTimeout = idle ?? TimeSpan.FromSeconds(30) };
            _run = Task.Run(() => server.RunAsync(Cts.Token));
        }

        public NamedPipeClientStream NewClient() => new(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        public UnattendedPipeClient NewTalker() => new(NewClient);
        public string[] LogLines() { lock (Log) return [.. Log]; }
        public (string? Sid, bool First)[] CreatedNow() { lock (Created) return [.. Created]; }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Cts.Dispose();
        }
    }

    [Fact]
    public async Task An_allowed_client_gets_its_answers_and_the_first_pipe_is_marked_first()
    {
        await using var rig = new Rig();
        using var talker = rig.NewTalker();
        var reply = await talker.CallAsync(new PipeRequest { Cmd = "status" }, rig.Cts.Token);
        Assert.Equal("got status", reply.Error);
        Assert.Equal("got start", (await talker.CallAsync(new PipeRequest { Cmd = "start" }, rig.Cts.Token)).Error);
        var created = rig.CreatedNow();
        Assert.True(created[0].First);
        Assert.All(created.Skip(1), c => Assert.False(c.First));
        Assert.All(created, c => Assert.Equal(User, c.Sid));
    }

    [Fact]
    public async Task A_refused_client_gets_no_answer_and_the_service_never_acts_on_anything_it_sent()
    {
        await using var rig = new Rig(_ => PeerVerdict.WrongUser);
        using var talker = rig.NewTalker();
        var ex = await Assert.ThrowsAsync<UnattendedPipeException>(() => talker.CallAsync(new PipeRequest { Cmd = "start" }, rig.Cts.Token));
        Assert.Equal(PipeProblem.Unreachable, ex.Problem);
        Assert.Equal(0, rig.Backend.Calls);
        Assert.Contains(rig.LogLines(), l => l.Contains("WrongUser"));
    }

    [Fact]
    public async Task A_check_that_fails_to_identify_the_client_counts_as_a_refusal()
    {
        await using var rig = new Rig(_ => throw new InvalidOperationException("cannot read the client"));
        using var talker = rig.NewTalker();
        await Assert.ThrowsAsync<UnattendedPipeException>(() => talker.CallAsync(new PipeRequest { Cmd = "start" }, rig.Cts.Token));
        Assert.Equal(0, rig.Backend.Calls);
    }

    [Fact]
    public async Task Garbage_oversize_and_half_messages_are_dropped_without_reaching_the_service_and_the_next_client_is_served()
    {
        await using var rig = new Rig();
        // oversize announcement
        await using (var client = rig.NewClient())
        {
            await client.ConnectAsync(5000, rig.Cts.Token);
            await client.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, rig.Cts.Token);
            await client.FlushAsync(rig.Cts.Token);
            Assert.Equal(0, await client.ReadAsync(new byte[8], rig.Cts.Token));   // closed, nothing sent back
        }
        // valid framing, nonsense inside: answered with bad-request, the connection stays usable
        using (var talker = rig.NewTalker())
        {
            await using var raw = rig.NewClient();
            await raw.ConnectAsync(5000, rig.Cts.Token);
            await PipeFraming.WriteAsync(raw, "not json at all"u8.ToArray(), rig.Cts.Token);
            var bad = PipeJson.Decode<PipeReply>((await PipeFraming.ReadAsync(raw, rig.Cts.Token))!);
            Assert.False(bad.Ok);
            Assert.Equal(PipeErrors.BadRequest, bad.Error);
            Assert.Equal("got status", (await talker.CallAsync(new PipeRequest { Cmd = "status" }, rig.Cts.Token)).Error);
        }
        // half a message, then gone
        await using (var half = rig.NewClient())
        {
            await half.ConnectAsync(5000, rig.Cts.Token);
            await half.WriteAsync(new byte[] { 0, 0, 0, 20, 1, 2, 3 }, rig.Cts.Token);
            await half.FlushAsync(rig.Cts.Token);
        }
        using var after = rig.NewTalker();
        Assert.Equal("got status", (await after.CallAsync(new PipeRequest { Cmd = "status" }, rig.Cts.Token)).Error);
        Assert.Equal(2, rig.Backend.Calls);
    }

    [Fact]
    public async Task A_failure_inside_the_service_is_an_internal_error_reply_not_a_dead_pipe()
    {
        await using var rig = new Rig();
        using var talker = rig.NewTalker();
        var reply = await talker.CallAsync(new PipeRequest { Cmd = "boom" }, rig.Cts.Token);
        Assert.False(reply.Ok);
        Assert.Equal(PipeErrors.Internal, reply.Error);
        Assert.Equal("got status", (await talker.CallAsync(new PipeRequest { Cmd = "status" }, rig.Cts.Token)).Error);
    }

    [Fact]
    public async Task When_a_different_user_signs_in_a_new_pipe_is_made_for_them_and_the_old_user_is_no_longer_served()
    {
        // the client in this test is always "User": the check is the real policy, fed with that identity
        await using var rig = new Rig(expected => PipeAccessPolicy.Decide(new PeerInfo(User, 1, false), expected.UserSid, expected.Session));
        using var oldUser = rig.NewTalker();
        Assert.Equal("got status", (await oldUser.CallAsync(new PipeRequest { Cmd = "status" }, rig.Cts.Token)).Error);

        rig.Context = new PeerContext(OtherUser, 2);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!rig.CreatedNow().Any(c => c.Sid == OtherUser) && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.Contains(rig.CreatedNow(), c => c.Sid == OtherUser);
        Assert.All(rig.CreatedNow().Where(c => c.Sid == OtherUser), c => Assert.False(c.First));

        var calls = rig.Backend.Calls;
        await Assert.ThrowsAsync<UnattendedPipeException>(() => oldUser.CallAsync(new PipeRequest { Cmd = "start" }, rig.Cts.Token));
        Assert.Equal(calls, rig.Backend.Calls);
        Assert.Contains(rig.LogLines(), l => l.Contains("WrongUser"));
    }

    [Fact]
    public async Task With_nobody_signed_in_the_pipe_is_made_for_nobody_and_no_one_is_served()
    {
        await using var rig = new Rig();
        rig.Context = new PeerContext(null, null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!rig.CreatedNow().Any(c => c.Sid is null) && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.Contains(rig.CreatedNow(), c => c.Sid is null);
        using var talker = rig.NewTalker();
        await Assert.ThrowsAsync<UnattendedPipeException>(() => talker.CallAsync(new PipeRequest { Cmd = "status" }, rig.Cts.Token));
        Assert.Equal(0, rig.Backend.Calls);
    }

    [Fact]
    public async Task A_client_that_connects_and_says_nothing_is_dropped_and_does_not_block_the_next_one()
    {
        await using var rig = new Rig(idle: TimeSpan.FromMilliseconds(300));
        await using var silent = rig.NewClient();
        await silent.ConnectAsync(5000, rig.Cts.Token);
        using var talker = rig.NewTalker();
        Assert.Equal("got status", (await talker.CallAsync(new PipeRequest { Cmd = "status" }, rig.Cts.Token)).Error);
    }
}
