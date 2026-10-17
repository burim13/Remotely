using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Protocol;
using Remotely.HostCore;

namespace Remotely.Tests;

/// <summary>
/// Hostile-input tests for every parser that reads bytes from another party. Each target gets a fixed-seed input stream (see <see cref="Fuzz"/>) and must
/// either accept the input or fail with its documented exception type; anything else (another exception type, a hang, a crash) fails the test and
/// prints the seed, the iteration and the input so it can be replayed. Property checks are added where a parser accepts input.
/// </summary>
public class CryptoFuzzTests
{
    private static readonly int N = Fuzz.Budget;

    // ---- text formats typed or pasted by a person ----

    private static string ReSignedInvite(Random rng, string invite)
    {
        // Mutates the inside of a valid invite and recomputes the checksum, so the parser's later checks are reached too.
        var b64 = invite[Invite.Prefix.Length..].Replace('-', '+').Replace('_', '/');
        var all = Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
        var body = all[..^3];
        for (var n = rng.Next(0, 4); n > 0; n--) body[rng.Next(body.Length)] = (byte)rng.Next(256);
        if (rng.Next(4) == 0) body = body[..rng.Next(1, body.Length + 1)];
        if (rng.Next(6) == 0) body = [.. body, .. Fuzz.Noise(rng, 20)];
        var signed = new byte[body.Length + 3];
        body.CopyTo(signed, 0);
        SHA256.HashData(body).AsSpan(0, 3).CopyTo(signed.AsSpan(body.Length));
        return Invite.Prefix + Convert.ToBase64String(signed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public void Invite_parser_never_throws_and_only_accepts_a_usable_server_address()
    {
        const int seed = 101;
        var valid = new Invite("wss://example.test:47800/ws", new string('a', 64), "K7P4-4NX2").Encode();
        var validNoTls = new Invite("ws://192.168.1.2:47800/ws", "", "ABCD-EFGH").Encode();
        var rng = new Random(seed);
        var accepted = 0;
        for (var i = 0; i < N; i++)
        {
            var text = rng.Next(3) switch
            {
                0 => ReSignedInvite(rng, rng.Next(2) == 0 ? valid : validNoTls),
                1 => Invite.Prefix + string.Concat(Fuzz.Texts(rng.Next(), 1, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_", 120)),
                _ => Encoding.UTF8.GetString(Fuzz.Noise(rng, 100)),
            };
            try
            {
                if (Invite.TryParse(text, out var invite, out var error))
                {
                    accepted++;
                    Assert.Contains(new Uri(invite!.ServerUri).Scheme, new[] { "ws", "wss" });
                    Assert.Equal(9, invite.Code.Length);
                    Assert.True(invite.PinHex.Length is 0 or 64);
                }
                else Assert.False(string.IsNullOrEmpty(error));
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"Invite.TryParse: seed={seed} iteration={i} input={text} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        Assert.True(accepted > 0, "the generator should reach the accepting path at least once, or the property above checked nothing");
    }

    [Fact]
    public void Token_parser_never_throws_and_accepts_only_twelve_alphabet_characters()
    {
        var accepted = 0;
        foreach (var text in Fuzz.Texts(102, N, LanToken.Alphabet + "abcdefghjkmnpqrstuvwxyz", 20))
        {
            if (LanToken.TryParse(text, out var token, out var error))
            {
                accepted++;
                Assert.Equal(4, token!.Id.Length);
                Assert.Equal(8, token.Secret.Length);
                Assert.All(token.Id + token.Secret, c => Assert.Contains(c, LanToken.Alphabet));
                Assert.True(LanToken.TryParse(token.Display, out var again, out _));
                Assert.Equal(token, again);
            }
            else Assert.False(string.IsNullOrEmpty(error));
        }
        Assert.True(accepted > 0);
        Assert.False(LanToken.TryParse(null, out _, out _));
    }

    // ---- framing and small binary formats ----

    [Fact]
    public async Task LAN_framing_survives_hostile_streams_and_roundtrips_valid_ones()
    {
        var rng = new Random(103);
        var valid = new List<byte[]>();
        foreach (var length in new[] { 0, 1, 17, 300 })
        {
            var frame = new byte[4 + length];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)length);
            rng.NextBytes(frame.AsSpan(4));
            valid.Add(frame);
        }
        valid.Add([.. valid[1], .. valid[2], .. valid[3]]);

        Fuzz.Run("FramedStream.ReceiveAsync", 103, Fuzz.Inputs(103, N, valid, 80), input =>
        {
            using var wire = new FramedStream(new MemoryStream(input));
            for (var guard = 0; guard < 50; guard++)
            {
                byte[]? message;
                try { message = wire.ReceiveAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return; }
                if (message is null) return;
                Assert.True(message.Length <= FramedStream.MaxFrame);
            }
        });

        // Property: whatever is sent is received unchanged, in order.
        var pipe = new MemoryStream();
        var sender = new FramedStream(pipe);
        var sent = Enumerable.Range(0, 200).Select(_ => Fuzz.Noise(rng, 5000)).ToList();
        foreach (var message in sent) await sender.SendAsync(message, CancellationToken.None);
        pipe.Position = 0;
        var receiver = new FramedStream(pipe);
        foreach (var message in sent) Assert.Equal(message, await receiver.ReceiveAsync(CancellationToken.None));
        Assert.Null(await receiver.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LAN_framing_refuses_an_oversized_length_before_allocating_or_reading_it()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)FramedStream.MaxFrame + 1);
        using var wire = new FramedStream(new MemoryStream(header));
        Assert.Throws<InvalidDataException>(() => wire.ReceiveAsync(CancellationToken.None).GetAwaiter().GetResult());
        BinaryPrimitives.WriteUInt32BigEndian(header, uint.MaxValue);
        using var wire2 = new FramedStream(new MemoryStream(header));
        Assert.Throws<InvalidDataException>(() => wire2.ReceiveAsync(CancellationToken.None).GetAwaiter().GetResult());
        using var wire3 = new FramedStream(new MemoryStream());
        await Assert.ThrowsAsync<InvalidDataException>(() => wire3.SendAsync(new byte[FramedStream.MaxFrame + 1], CancellationToken.None));
    }

    [Fact]
    public void Input_codec_never_throws_and_what_it_accepts_encodes_back_to_the_same_events()
    {
        var valid = new List<byte[]>
        {
            InputCodec.Encode([InputEvent.Move(1, 2)]),
            InputCodec.Encode([InputEvent.Down(MouseButton.Right, 5, 6), InputEvent.Up(MouseButton.X2, 7, 8), InputEvent.Scroll(-120, 1, 2), InputEvent.Key(true, 0x41, 0x1E, false)]),
            InputCodec.Encode([new InputEvent(InputKind.ReleaseAll)]),
            InputCodec.Encode(Enumerable.Repeat(InputEvent.Move(9, 9), InputCodec.MaxEventsPerMessage).ToList()),
        };
        var accepted = 0;
        Fuzz.Run("InputCodec.TryDecode", 104, Fuzz.Inputs(104, N, valid, 100), input =>
        {
            if (!InputCodec.TryDecode(input, out var events)) return;
            accepted++;
            Assert.InRange(events.Count, 0, InputCodec.MaxEventsPerMessage);
            Assert.True(InputCodec.TryDecode(InputCodec.Encode(events), out var again));
            Assert.Equal(events, again);
        });
        Assert.True(accepted > 0);
    }

    [Fact]
    public void Frame_hello_and_id_parsers_never_throw_the_wrong_exception()
    {
        var jpeg = new byte[40];
        new Random(5).NextBytes(jpeg);
        var valid = new List<byte[]>
        {
            FrameCodec.Encode(new FrameInfo(800, 600, "Default", jpeg)),
            FrameCodec.Encode(new FrameInfo(1, 1, "", [])),
            FrameCodec.Encode(new FrameInfo(32768, 32768, "Winlogon", jpeg)),
        };
        Fuzz.Run("FrameCodec.Decode", 105, Fuzz.Inputs(105, N, valid, 100), input =>
        {
            try
            {
                var frame = FrameCodec.Decode(input);
                Assert.InRange(frame.Width, 1, 32768);
                Assert.InRange(frame.Height, 1, 32768);
                Assert.True(frame.Jpeg.Length <= input.Length);
            }
            catch (FormatException) { }
        });

        var hello = new HelloInfo(HostMessages.ProtocolVersion, HostCapabilities.Control | HostCapabilities.Files).Encode();
        Fuzz.Run("HelloInfo.Decode and HostMessages.TryReadId", 106, Fuzz.Inputs(106, N, [hello, HostMessages.WithId(HostMessages.FileEnd, 7, [])], 20), input =>
        {
            var parsed = HelloInfo.Decode(input);
            if (parsed is not null) Assert.Equal(HostMessages.Hello, input[0]);
            if (HostMessages.TryReadId(input, out _, out var rest)) Assert.Equal(input.Length - 5, rest.Length);
        });
    }

    [Fact]
    public void Control_messages_and_relay_frames_never_throw_the_wrong_exception()
    {
        var valid = new List<byte[]>
        {
            ControlCodec.Serialize(new PairRequest("K7P4-4NX2", "My PC", true)),
            ControlCodec.Serialize(new ServerHello(new byte[32], 1)),
            ControlCodec.Serialize(new SessionOpened(Guid.NewGuid(), new string('a', 64), true)),
            ControlCodec.Serialize(new DeviceList([new DeviceInfo(new string('b', 64), "x", true, false)])),
            ControlCodec.Serialize(new NotPaired()),
        };
        Fuzz.Run("ControlCodec.Deserialize", 107, Fuzz.Inputs(107, N, valid, 120), input =>
        {
            try { ControlCodec.Deserialize(input); }
            catch (FormatException) { }
        });
        Fuzz.Run("RelayFrame.TryParse", 108, Fuzz.Inputs(108, N, [RelayFrame.Build(Guid.NewGuid(), [1, 2, 3])], 40), input =>
        {
            if (RelayFrame.TryParse(input, out _)) Assert.True(input.Length >= RelayFrame.HeaderLength);
        });
    }

    [Fact]
    public void Field_reader_only_ever_fails_with_a_format_error()
    {
        var sample = new FieldWriter().Raw("RMT1"u8).Byte(1).Field(new byte[91]).Field(new byte[32]).Field([]).ToArray();
        Fuzz.Run("FieldReader", 109, Fuzz.Inputs(109, N, [sample], 100), input =>
        {
            var rng = new Random(input.Length * 31 + (input.Length > 0 ? input[0] : 0));
            var r = new FieldReader(input);
            try
            {
                for (var step = 0; step < 12 && !r.AtEnd; step++)
                {
                    switch (rng.Next(3))
                    {
                        case 0: r.Byte(); break;
                        case 1: r.Raw(rng.Next(-2, 20)); break;
                        default: r.Field(rng.Next(0, 5000)); break;
                    }
                }
            }
            catch (FormatException) { }
        });
    }

    // ---- handshakes ----

    [Fact]
    public void Device_handshake_messages_are_either_accepted_or_a_handshake_error()
    {
        using var initiator = DeviceIdentity.Generate();
        using var responder = DeviceIdentity.Generate();
        using var init = new InitiatorHandshake(initiator, responder.DeviceId);
        using var resp = new ResponderHandshake(responder);
        var hello1 = init.CreateHello1();
        var hello2 = resp.ProcessHello1(hello1, _ => true, out _, out var ch);
        ch.Dispose();

        var trustCalls = 0;
        Fuzz.Run("ResponderHandshake.ProcessHello1", 110, Fuzz.Inputs(110, N, [hello1], 300), input =>
        {
            try
            {
                resp.ProcessHello1(input, _ => { trustCalls++; return true; }, out _, out var channel);
                channel.Dispose();
                Assert.Equal(hello1, input);   // the only input that may be accepted is a signed message that was not changed
            }
            catch (HandshakeException) { }
        });
        Fuzz.Run("InitiatorHandshake.ProcessHello2", 111, Fuzz.Inputs(111, N, [hello2], 400), input =>
        {
            try
            {
                init.ProcessHello2(input).Dispose();
                Assert.Equal(hello2, input);
            }
            catch (HandshakeException) { }
        });
        Assert.True(trustCalls > 0);
    }

    [Fact]
    public void Validly_signed_hellos_with_hostile_contents_are_still_only_handshake_errors()
    {
        // The signature is valid (made with the attacker's own key), but the key, nonce and length fields are garbage.
        using var attacker = DeviceIdentity.Generate();
        using var victim = DeviceIdentity.Generate();
        using var resp = new ResponderHandshake(victim);
        using var init = new InitiatorHandshake(attacker, victim.DeviceId);
        var expected = DeviceIds.ToBytes(victim.DeviceId);
        var rng = new Random(112);
        var accepted = 0;
        for (var i = 0; i < Math.Min(N, 1500); i++)
        {
            var ephemeral = Fuzz.Noise(rng, 120);
            var nonce = rng.Next(3) == 0 ? new byte[HandshakeCommon.NonceLength] : Fuzz.Noise(rng, 64);
            var signature = attacker.Sign(HandshakeCommon.SignatureInput("RMT1-I", ephemeral, nonce, expected));
            var hello1 = new FieldWriter().Raw(HandshakeCommon.Magic).Byte(HandshakeCommon.TypeHello1)
                .Field(attacker.PublicKey).Field(ephemeral).Field(nonce).Field(expected).Field(signature).ToArray();
            try { resp.ProcessHello1(hello1, _ => true, out _, out var channel); channel.Dispose(); accepted++; }
            catch (HandshakeException) { }
        }
        Assert.Equal(0, accepted);   // a random blob is never a valid P-256 public key; if it were, 2^-128 odds

        // Same for a Hello2 that the expected responder "signed" over garbage.
        init.CreateHello1();
        for (var i = 0; i < Math.Min(N, 1500); i++)
        {
            var ephemeral = Fuzz.Noise(rng, 120);
            var nonceR = Fuzz.Noise(rng, 64);
            var body = new FieldWriter().Raw(HandshakeCommon.Magic).Byte(HandshakeCommon.TypeHello2).Field(victim.PublicKey).Field(ephemeral).Field(nonceR).ToArray();
            var signature = victim.Sign(HandshakeCommon.SignatureInput("RMT1-R", ephemeral, nonceR, new byte[32]));
            var hello2 = body.Concat(new FieldWriter().Field(signature).ToArray()).ToArray();
            Assert.Throws<HandshakeException>(() => init.ProcessHello2(hello2));
        }
    }

    [Fact]
    public void Token_handshake_messages_are_either_accepted_or_a_handshake_error()
    {
        var token = LanToken.Create();
        var state = new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(10));
        var resp = new LanResponderHandshake(state);
        using var init = new LanInitiatorHandshake(token);
        var hello1 = init.CreateHello1();
        var hello2 = resp.ProcessHello1(hello1, out var ch);
        ch.Dispose();

        Fuzz.Run("LanResponderHandshake.ProcessHello1", 113, Fuzz.Inputs(113, N, [hello1], 300), input =>
        {
            try { resp.ProcessHello1(input, out var channel); channel.Dispose(); }
            catch (HandshakeException) { }
        });

        // Each initiator attempt that gets past the shape checks costs one key stretch (about 0.1 s), so this target has a smaller budget.
        var stretched = 0;
        Fuzz.Run("LanInitiatorHandshake.ProcessHello2", 114, Fuzz.Inputs(114, Math.Min(N, 400), [hello2], 300), input =>
        {
            stretched++;
            try { init.ProcessHello2(input).Dispose(); }
            catch (HandshakeException) { }
        });
        Assert.True(stretched > 0);

        // Hello1 with hostile field contents (no signature exists in this protocol, so every shape-valid garbage reaches the key agreement).
        var rng = new Random(115);
        for (var i = 0; i < Math.Min(N, 1500); i++)
        {
            var hello = new FieldWriter().Raw(LanHandshakeCommon.Magic).Byte(LanHandshakeCommon.TypeHello1)
                .Field(Fuzz.Noise(rng, 120)).Field(rng.Next(2) == 0 ? new byte[32] : Fuzz.Noise(rng, 64)).Field(Encoding.ASCII.GetBytes(token.Id)).ToArray();
            Assert.Throws<HandshakeException>(() => resp.ProcessHello1(hello, out _));
        }
    }

    [Fact]
    public void Encrypted_frames_that_are_changed_in_any_way_are_always_refused()
    {
        var rng = new Random(116);
        for (var i = 0; i < N; i++)
        {
            var a = RandomNumberGenerator.GetBytes(32);
            var b = RandomNumberGenerator.GetBytes(32);
            using var sender = new SecureChannel(a, b);
            using var receiver = new SecureChannel(b, a);
            var frame = sender.Seal(Fuzz.Noise(rng, 200));
            var changed = Fuzz.Inputs(rng.Next(), 1, [frame], 64).Single();
            if (changed.AsSpan().SequenceEqual(frame)) continue;
            Assert.ThrowsAny<CryptographicException>(() => receiver.Open(changed));
            Assert.NotNull(receiver.Open(frame));   // a refused frame does not damage the channel
        }
    }

    [Fact]
    public void Public_key_and_identity_parsers_fail_with_the_documented_exception_only()
    {
        using var id = DeviceIdentity.Generate();
        var pkcs8 = id.ExportPkcs8();
        Fuzz.Run("DeviceIdentity.Verify", 117, Fuzz.Inputs(117, N, [id.PublicKey], 200), input =>
        {
            Assert.False(DeviceIdentity.Verify(input, "data"u8, id.Sign("other"u8)));
            Assert.False(DeviceIdentity.Verify(id.PublicKey, "data"u8, input));
        });
        Fuzz.Run("DeviceIdentity.FromPkcs8", 118, Fuzz.Inputs(118, Math.Min(N, 2000), [pkcs8], 200), input =>
        {
            try { DeviceIdentity.FromPkcs8(input).Dispose(); }
            catch (CryptographicException) { }
        });
        Fuzz.Run("DeviceIds.ToBytes", 119, Fuzz.Inputs(119, N, [Encoding.ASCII.GetBytes(id.DeviceId)], 90), input =>
        {
            try { Assert.Equal(32, DeviceIds.ToBytes(Encoding.Latin1.GetString(input)).Length); }
            catch (FormatException) { }
        });
    }

    // ---- file messages and names ----

    [Fact]
    public void File_names_always_come_out_safe()
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var rng = new Random(120);
        var parts = new[] { "..", ".", "/", "\\", "C:", "CON", "nul.txt", "a", "b c", " ", "...", "‮", "\0", "\n", "é", new string('x', 300), ".hidden", "name.", "x.exe", "COM1" };
        for (var i = 0; i < N; i++)
        {
            var name = string.Concat(Enumerable.Range(0, rng.Next(1, 8)).Select(_ => parts[rng.Next(parts.Length)] + (rng.Next(3) == 0 ? "/" : "")));
            var safe = FileNames.Sanitize(name);
            Assert.NotEmpty(safe);
            Assert.True(safe.Length <= 140, safe);
            Assert.Equal(safe, Path.GetFileName(safe));
            Assert.DoesNotContain(safe, new[] { ".", ".." });
            Assert.All(safe, c => Assert.DoesNotContain(c, invalid));
            Assert.All(safe, c => Assert.False(char.IsControl(c)));
            Assert.False(safe.EndsWith('.') || safe.EndsWith(' '));
        }
    }

    [Fact]
    public async Task Hostile_file_and_clipboard_messages_never_write_outside_the_receive_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "remotely-fuzz-" + Guid.NewGuid().ToString("N"));
        var receive = Path.Combine(root, "receive");
        var share = Path.Combine(root, "share");
        Directory.CreateDirectory(receive);
        Directory.CreateDirectory(share);
        File.WriteAllText(Path.Combine(share, "ok.txt"), "shared");
        File.WriteAllText(Path.Combine(root, "secret.txt"), "not shared");
        try
        {
            static byte[] Header(long size, byte[] hash, string name)
            {
                var n = Encoding.UTF8.GetBytes(name);
                var h = new byte[8 + 32 + 2 + n.Length];
                BinaryPrimitives.WriteUInt64BigEndian(h, (ulong)size);
                hash.CopyTo(h, 8);
                BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(40), (ushort)n.Length);
                n.CopyTo(h, 42);
                return h;
            }
            static byte[] Get(string name)
            {
                var n = Encoding.UTF8.GetBytes(name);
                var p = new byte[2 + n.Length];
                BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)n.Length);
                n.CopyTo(p, 2);
                return p;
            }
            var content = "hello"u8.ToArray();
            var valid = new List<byte[]>
            {
                HostMessages.WithId(HostMessages.FileStart, 1, Header(5, SHA256.HashData(content), "a.txt")),
                HostMessages.WithId(HostMessages.FileStart, 2, Header(5, SHA256.HashData(content), "../../escape.txt")),
                HostMessages.WithId(HostMessages.FileStart, 3, Header(5, SHA256.HashData(content), "..\\..\\escape2.txt")),
                HostMessages.WithId(HostMessages.FileStart, 4, Header(long.MaxValue, new byte[32], "big")),
                HostMessages.WithId(HostMessages.FileChunk, 1, content),
                HostMessages.WithId(HostMessages.FileEnd, 1, []),
                HostMessages.WithId(HostMessages.ShareGet, 5, Get("ok.txt")),
                HostMessages.WithId(HostMessages.ShareGet, 6, Get("../secret.txt")),
                HostMessages.WithId(HostMessages.ShareGet, 7, Get("..\\secret.txt")),
                new[] { HostMessages.ShareList },
                HostMessages.Prefixed(HostMessages.Clipboard, "text"u8),
                new[] { HostMessages.FrameRequest },
                new[] { HostMessages.SecureAttention },
                InputCodec.Encode([InputEvent.Move(1, 1)]),
            };
            var sessions = Math.Max(10, N / 40);
            var all = Fuzz.Inputs(121, sessions * 40, valid, 120).ToList();
            var rng = new Random(121);
            for (var s = 0; s < sessions; s++)
            {
                var messages = all.Skip(s * 40).Take(40).ToList();
                if (rng.Next(2) == 0) messages.InsertRange(0, valid.Take(6));   // sometimes start from a well-formed upload so the chunk and end paths are reached
                var services = new HostServices(new HostCoreTests.FakeScreen(() => new FrameInfo(10, 10, "Default", [1])))
                {
                    IsTrusted = _ => true,
                    CanControl = _ => true,
                    Input = new HostCoreTests.RecordingInput(),
                    Clipboard = new HostCoreTests.FakeClipboard(),
                    ReceiveDirectory = receive,
                    ShareDirectory = share,
                };
                var agent = new HostAgent(DeviceIdentity.Generate(), services, _ => { });
                var channel = new ScriptedChannel(messages);
                await agent.ServeChannelAsync(channel, "fuzz", control: true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            }

            var written = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).ToList();
            Assert.All(written, f => Assert.True(
                f is "secret.txt" || f == Path.Combine("share", "ok.txt") || f.StartsWith("receive" + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "unexpected file outside the receive folder: " + f));
            Assert.Empty(Directory.GetDirectories(receive));
            Assert.Empty(Directory.GetFiles(receive, "*.part"));
            Assert.Equal("not shared", File.ReadAllText(Path.Combine(root, "secret.txt")));
            Assert.Equal("shared", File.ReadAllText(Path.Combine(share, "ok.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private sealed class ScriptedChannel(IEnumerable<byte[]> incoming) : IMessageChannel
    {
        private readonly Queue<byte[]> _queue = new(incoming);
        public Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> ReceiveAsync(CancellationToken ct = default) => Task.FromResult(_queue.TryDequeue(out var m) ? m : null);
        public Task CloseAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    // ---- discovery packets ----

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static byte[] Probe(string id) => [.. "RMTL"u8.ToArray(), 0x10, .. Encoding.ASCII.GetBytes(id)];

    private static byte[] Reply(string id, int port, string name) =>
        [.. "RMTL"u8.ToArray(), 0x11, .. Encoding.ASCII.GetBytes(id), (byte)(port >> 8), (byte)port, .. Encoding.UTF8.GetBytes(name)];

    [Fact]
    public async Task The_discovery_responder_survives_hostile_packets_and_answers_only_a_well_formed_probe_for_its_label()
    {
        const string label = "K7P4";
        var port = FreeUdpPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var udp = LanDiscovery.OpenResponder(port);
        var serving = Task.Run(() => LanDiscovery.ServeAsync(udp, () => label, () => 4242, "fuzz-host", allowLoopback: true, cts.Token));

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, port);
        var valid = Probe(label);
        var inputs = Fuzz.Inputs(122, Math.Min(N, 2000), [valid, Probe("ZZZZ"), Reply(label, 4242, "x")], 60).ToList();
        var exactProbes = 0;
        var replies = new List<byte[]>();
        using var reader = new CancellationTokenSource();
        var collecting = Task.Run(async () =>
        {
            try { while (true) replies.Add((await client.ReceiveAsync(reader.Token)).Buffer); }
            catch (OperationCanceledException) { }
        });
        foreach (var input in inputs)
        {
            if (input.AsSpan().SequenceEqual(valid)) exactProbes++;
            await client.SendAsync(input, target);
        }
        await Task.Delay(500);
        Assert.False(serving.IsFaulted, "the responder crashed on a hostile packet");

        var before = replies.Count;
        for (var attempt = 0; attempt < 20 && replies.Count == before; attempt++)
        {
            await client.SendAsync(valid, target);
            await Task.Delay(150);
        }
        Assert.True(replies.Count > before, "the responder no longer answers a valid probe after the hostile packets");
        await reader.CancelAsync();
        await collecting;
        cts.Cancel();

        Assert.All(replies, r => Assert.True(r.AsSpan().StartsWith(Reply(label, 4242, "")[..9])));   // magic, reply type and label: nothing else is ever sent
        Assert.True(replies.Count <= exactProbes + 21, $"answered {replies.Count} packets but only {exactProbes + 21} were valid probes");
    }

    [Fact]
    public async Task The_discovery_finder_ignores_hostile_replies_and_still_accepts_a_good_one()
    {
        const string label = "K7P4";
        var rng = new Random(123);
        var hostile = Fuzz.Inputs(123, 400, [Reply(label, 4242, "pc"), Reply(label, 0, "zero"), Reply("ZZZZ", 4242, "other"), Reply(label, 65535, new string('n', 300))], 60).ToList();
        for (var round = 0; round < 3; round++)
        {
            var port = FreeUdpPort();
            using var fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var good = round == 2;
            var answering = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var probe = await fake.ReceiveAsync(stop.Token);
                        foreach (var packet in hostile.OrderBy(_ => rng.Next()).Take(100)) await fake.SendAsync(packet, probe.RemoteEndPoint);
                        if (good) await fake.SendAsync(Reply(label, 5151, "real"), probe.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
            var found = await LanDiscovery.FindAsync(label, TimeSpan.FromMilliseconds(1500), CancellationToken.None, [new IPEndPoint(IPAddress.Loopback, port)], port);
            await stop.CancelAsync();
            await answering;
            if (found is not null)
            {
                Assert.InRange(found.Endpoint.Port, 1, 65535);   // a reply that names port 0 is never returned
                Assert.True(found.HostName.Length <= 300);
            }
            if (good) Assert.NotNull(found);
        }
    }
}
