using System.Security.Cryptography;
using System.Text;
using Remotely.HostCore;
using Remotely.ViewerCore;
using static Remotely.Tests.HostCoreTests;

namespace Remotely.Tests;

public class ControlTests
{
    private static FrameInfo Frame() => new(640, 480, "Default", [1, 2, 3]);

    private static async Task<T> Eventually<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(25);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    private static HostServices Full(DeviceIdentityHolder _, RecordingInput input, FakeClipboard clip, string? receive = null, string? share = null) =>
        new(new FakeScreen(Frame))
        {
            IsTrusted = _ => true,
            CanControl = _ => true,
            Input = input,
            SecureAttention = input,
            Clipboard = clip,
            ReceiveDirectory = receive,
            ShareDirectory = share,
        };

    private sealed class DeviceIdentityHolder;

    [Fact]
    public void Input_events_roundtrip_and_hostile_messages_are_rejected()
    {
        var events = new List<InputEvent>
        {
            InputEvent.Move(100, 65535), InputEvent.Down(MouseButton.Right, 5, 6), InputEvent.Up(MouseButton.X2, 7, 8),
            InputEvent.Scroll(-120, 1, 2), InputEvent.Scroll(120, 3, 4, horizontal: true), InputEvent.Key(true, 0x41, 0x1E, false),
            InputEvent.Key(false, 0x25, 0x4B, true), new(InputKind.ReleaseAll),
        };
        Assert.True(InputCodec.TryDecode(InputCodec.Encode(events), out var back));
        Assert.Equal(events, back);
        Assert.Equal(-120, back[3].WheelDelta);

        var good = InputCodec.Encode(events);
        Assert.False(InputCodec.TryDecode(good[..^1], out _));                                   // truncated
        Assert.False(InputCodec.TryDecode([HostMessages.Input, 0xFF, 0xFF], out _));            // count lies
        var badKind = (byte[])good.Clone(); badKind[3] = 99;
        Assert.False(InputCodec.TryDecode(badKind, out _));
        var badButton = InputCodec.Encode([InputEvent.Down(MouseButton.Left, 0, 0) with { Arg1 = 9 }]);
        Assert.False(InputCodec.TryDecode(badButton, out _));
        Assert.Throws<ArgumentException>(() => InputCodec.Encode(Enumerable.Repeat(InputEvent.Move(0, 0), 257).ToList()));
    }

    [Fact]
    public async Task A_controlling_viewer_moves_the_mouse_types_and_sends_ctrl_alt_del()
    {
        var input = new RecordingInput();
        await using var rig = await Rig.StartAsync(v => Full(null!, input, new FakeClipboard()));
        await using var session = await rig.OpenAsync();
        Assert.True(session.CanControl && session.CanSendSecureAttention && session.HasClipboard);

        session.SendInput(InputEvent.Move(10, 10));
        session.SendInput(InputEvent.Down(MouseButton.Left, 10, 10));
        session.SendInput(InputEvent.Up(MouseButton.Left, 10, 10));
        session.SendInput(InputEvent.Key(true, 0x41, 0x1E, false));
        session.SendInput(InputEvent.Key(false, 0x41, 0x1E, false));
        await session.SendSecureAttentionAsync();

        var got = await Eventually(() => input.Snapshot() is { Length: >= 5 } s ? s : null, "input events");
        Assert.Equal([InputKind.MouseMove, InputKind.MouseDown, InputKind.MouseUp, InputKind.KeyDown, InputKind.KeyUp], got.Select(e => e.Kind).ToArray());   // order preserved
        await Eventually(() => input.Sas == 1 ? input : null, "secure attention");
    }

    [Fact]
    public async Task Mouse_moves_are_coalesced_so_a_flood_does_not_reach_the_host()
    {
        var input = new RecordingInput();
        await using var rig = await Rig.StartAsync(v => Full(null!, input, new FakeClipboard()));
        await using var session = await rig.OpenAsync();
        for (ushort i = 0; i < 2000; i++) session.SendInput(InputEvent.Move(i, i));
        session.SendInput(InputEvent.Down(MouseButton.Left, 1999, 1999));
        var all = await Eventually(() => input.Snapshot() is { Length: > 0 } s && s[^1].Kind == InputKind.MouseDown ? s : null, "final click");
        Assert.True(all.Length < 500, $"expected coalescing, got {all.Length} events");
        Assert.Equal(1999, all.Last(e => e.Kind == InputKind.MouseMove).X);      // the latest position always wins
    }

    [Fact]
    public async Task A_view_only_viewer_cannot_inject_input_or_touch_clipboard_or_files()
    {
        var input = new RecordingInput();
        var clip = new FakeClipboard();
        var receive = Path.Combine(Path.GetTempPath(), "rcv-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var rig = await Rig.StartAsync(v => new HostServices(new FakeScreen(Frame))
            {
                IsTrusted = _ => true, CanControl = _ => false, Input = input, SecureAttention = input, Clipboard = clip, ReceiveDirectory = receive,
            });
            await using var session = await rig.OpenAsync();
            Assert.False(session.CanControl);
            Assert.False(session.HasFiles);
            Assert.False(session.HasClipboard);

            // Bypass the client-side checks by speaking the wire protocol directly: the host must still refuse.
            var errors = new List<string>();
            session.HostError += e => { lock (errors) errors.Add(e); };
            var secureField = typeof(ViewerSession).GetField("_secure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var secure = (Remotely.Client.SecureSession)secureField.GetValue(session)!;
            await secure.SendAsync(InputCodec.Encode([InputEvent.Key(true, 0x41, 0x1E, false)]));
            await secure.SendAsync(HostMessages.Prefixed(HostMessages.Clipboard, "pwned"u8));
            await secure.SendAsync(new[] { HostMessages.SecureAttention });
            await Eventually(() => { lock (errors) return errors.Count >= 3 ? errors : null; }, "three refusals");
            Assert.Empty(input.Snapshot());
            Assert.Equal(0, input.Sas);
            Assert.Equal("initial", clip.Text);
        }
        finally { if (Directory.Exists(receive)) Directory.Delete(receive, true); }
    }

    [Fact]
    public async Task Clipboard_syncs_both_ways_without_echo()
    {
        var clip = new FakeClipboard();
        await using var rig = await Rig.StartAsync(v => Full(null!, new RecordingInput(), clip));
        await using var session = await rig.OpenAsync();
        var received = new List<string>();
        session.ClipboardReceived += t => { lock (received) received.Add(t); };

        // the host's current text arrives once
        await Eventually(() => { lock (received) return received.Count > 0 ? received : null; }, "initial clipboard");
        // viewer -> host
        await session.SendClipboardAsync("from viewer");
        await Eventually(() => clip.Text == "from viewer" ? clip : null, "host clipboard write");
        // host -> viewer
        clip.LocalCopy("from host");
        await Eventually(() => { lock (received) return received.Contains("from host") ? received : null; }, "host clipboard push");
        await Task.Delay(300);
        lock (received) Assert.Equal(["initial", "from host"], received);       // "from viewer" was never echoed back
    }

    [Fact]
    public async Task Files_upload_with_checksum_and_unsafe_names_are_neutralised()
    {
        var receive = Path.Combine(Path.GetTempPath(), "rcv-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        try
        {
            await using var rig = await Rig.StartAsync(v => Full(null!, new RecordingInput(), new FakeClipboard(), receive));
            await using var session = await rig.OpenAsync();

            var data = RandomNumberGenerator.GetBytes(1_300_000);                       // three chunks
            var file = Path.Combine(source, "report.bin");
            await File.WriteAllBytesAsync(file, data);
            var saved = await session.UploadFileAsync(file);
            Assert.Equal("report.bin", saved);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(receive, "report.bin")));

            var second = await session.UploadFileAsync(file);                          // never overwrites
            Assert.Equal("report (1).bin", second);
            Assert.Equal(2, Directory.GetFiles(receive).Length);
            Assert.Empty(Directory.GetFiles(receive, "*.part"));

            var empty = Path.Combine(source, "empty.txt");
            await File.WriteAllBytesAsync(empty, []);
            Assert.Equal("empty.txt", await session.UploadFileAsync(empty));
        }
        finally { foreach (var d in new[] { receive, source }) if (Directory.Exists(d)) Directory.Delete(d, true); }
    }

    [Fact]
    public void File_names_cannot_escape_the_folder_or_hit_device_names()
    {
        Assert.Equal("passwd", FileNames.Sanitize("../../etc/passwd"));
        Assert.Equal("evil.exe", FileNames.Sanitize(@"..\..\Windows\evil.exe"));
        Assert.Equal("_CON.txt", FileNames.Sanitize("CON.txt"));
        Assert.Equal("file", FileNames.Sanitize("..."));
        Assert.Equal("file", FileNames.Sanitize(""));
        Assert.Equal("ab.txt", FileNames.Sanitize("a\0b<>:|?.txt"));
        Assert.True(FileNames.Sanitize(new string('x', 500) + ".txt").Length <= 120);

        var dir = Path.Combine(Path.GetTempPath(), "shr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ok.txt"), "x");
            Assert.NotNull(ShareDirectory.Resolve(dir, "ok.txt"));
            Assert.NotNull(ShareDirectory.Resolve(dir, "OK.TXT"));
            Assert.Null(ShareDirectory.Resolve(dir, "../ok.txt"));
            Assert.Null(ShareDirectory.Resolve(dir, @"..\ok.txt"));
            Assert.Null(ShareDirectory.Resolve(dir, Path.Combine(dir, "ok.txt")));
            Assert.Null(ShareDirectory.Resolve(dir, "missing.txt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_corrupted_upload_is_rejected_and_leaves_nothing_behind()
    {
        var receive = Path.Combine(Path.GetTempPath(), "rcv-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var rig = await Rig.StartAsync(v => Full(null!, new RecordingInput(), new FakeClipboard(), receive));
            await using var session = await rig.OpenAsync();
            var secure = (Remotely.Client.SecureSession)typeof(ViewerSession).GetField("_secure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;

            var content = "hello"u8.ToArray();
            var name = "bad.txt"u8.ToArray();
            var header = new byte[42 + name.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)content.Length);
            SHA256.HashData("something else"u8).CopyTo(header, 8);                       // wrong checksum
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(40), (ushort)name.Length);
            name.CopyTo(header, 42);
            var acks = new TaskCompletionSource<string>();
            var errorsField = typeof(ViewerSession).GetField("_acks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ((System.Collections.Concurrent.ConcurrentDictionary<uint, TaskCompletionSource<string>>)errorsField.GetValue(session)!)[77] = acks;

            await secure.SendAsync(HostMessages.WithId(HostMessages.FileStart, 77, header));
            await secure.SendAsync(HostMessages.WithId(HostMessages.FileChunk, 77, content));
            await secure.SendAsync(HostMessages.WithId(HostMessages.FileEnd, 77, []));
            var ex = await Assert.ThrowsAsync<IOException>(() => acks.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("Checksum", ex.Message);
            Assert.Empty(Directory.GetFiles(receive));
        }
        finally { if (Directory.Exists(receive)) Directory.Delete(receive, true); }
    }

    [Fact]
    public async Task Shared_files_can_be_listed_and_downloaded_but_nothing_else_on_the_host()
    {
        var share = Path.Combine(Path.GetTempPath(), "shr-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var downloads = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(share);
        try
        {
            var data = RandomNumberGenerator.GetBytes(1_100_000);
            await File.WriteAllBytesAsync(Path.Combine(share, "tax-2025.pdf"), data);
            await File.WriteAllTextAsync(outside, "secret");
            await using var rig = await Rig.StartAsync(v => Full(null!, new RecordingInput(), new FakeClipboard(), share: share));
            await using var session = await rig.OpenAsync();
            session.DownloadDirectory = downloads;

            var list = await session.ListSharedFilesAsync();
            var entry = Assert.Single(list);
            Assert.Equal(("tax-2025.pdf", data.Length), (entry.Name, (int)entry.Size));

            var path = await session.DownloadFileAsync("tax-2025.pdf");
            Assert.Equal(data, await File.ReadAllBytesAsync(path));

            await Assert.ThrowsAsync<IOException>(() => session.DownloadFileAsync(outside));                                 // absolute path
            await Assert.ThrowsAsync<IOException>(() => session.DownloadFileAsync("../" + Path.GetFileName(outside)));       // traversal
            await Assert.ThrowsAsync<IOException>(() => session.DownloadFileAsync("nope.txt"));
        }
        finally { foreach (var d in new[] { share, downloads }) if (Directory.Exists(d)) Directory.Delete(d, true); File.Delete(outside); }
    }

    [Fact]
    public async Task Concurrent_senders_on_one_session_never_break_the_message_counter()
    {
        // Frames, input, file chunks and clipboard all share one encrypted channel. Parallel sends must stay in counter order.
        var input = new RecordingInput();
        await using var rig = await Rig.StartAsync(v => Full(null!, input, new FakeClipboard()));
        await using var session = await rig.OpenAsync();
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            session.SendInput(InputEvent.Key(true, (ushort)(i + 1), 0, false));
            await session.SendClipboardAsync("clip " + i);
        })).ToArray();
        await Task.WhenAll(tasks);
        var keys = await Eventually(() => input.Snapshot().Count(e => e.Kind == InputKind.KeyDown) == 200 ? input.Snapshot() : null, "all 200 keys");
        Assert.Equal(200, keys.Count(e => e.Kind == InputKind.KeyDown));
    }

    [Fact]
    public async Task Closing_the_host_connection_ends_the_viewer_session_with_an_event()
    {
        await using var rig = await Rig.StartAsync(v => Full(null!, new RecordingInput(), new FakeClipboard()));
        await using var session = await rig.OpenAsync();
        var closed = new TaskCompletionSource<string?>();
        session.Closed += r => closed.TrySetResult(r);
        await rig.HostClient.DisposeAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
