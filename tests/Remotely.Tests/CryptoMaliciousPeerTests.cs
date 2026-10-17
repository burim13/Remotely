using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Remotely.Core.Crypto;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

/// <summary>What a viewer does when the host it is connected to is hostile. Each test plays the host by hand over an in-memory channel.</summary>
public class CryptoMaliciousPeerTests
{
    private sealed class Pipe(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer) : IMessageChannel
    {
        public Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => writer.WriteAsync(plaintext.ToArray(), ct).AsTask();

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
        {
            try { return await reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }

        public Task CloseAsync() { writer.TryComplete(); return Task.CompletedTask; }
        public void Dispose() => writer.TryComplete();
    }

    private static (Pipe ViewerSide, Pipe HostSide) NewPipe()
    {
        var toHost = Channel.CreateUnbounded<byte[]>();
        var toViewer = Channel.CreateUnbounded<byte[]>();
        return (new Pipe(toViewer.Reader, toHost.Writer), new Pipe(toHost.Reader, toViewer.Writer));
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ViewerSession Session;
        public required Pipe Host;
        public required string Directory;
        public required Task HostLoop;
        public required CancellationTokenSource Cts;
        public readonly List<string> Errors = [];

        public static async Task<Rig> StartAsync(Func<byte, uint, byte[], Pipe, Task> onMessage)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (viewerSide, hostSide) = NewPipe();
            var opening = ViewerSession.OpenAsync(viewerSide, "evil-host", cts.Token);
            await hostSide.SendAsync(new HelloInfo(HostMessages.ProtocolVersion, HostCapabilities.Control | HostCapabilities.Files | HostCapabilities.Clipboard).Encode());
            var session = await opening;
            var dir = Path.Combine(Path.GetTempPath(), "remotely-evil-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            session.DownloadDirectory = dir;
            var rig = new Rig { Session = session, Host = hostSide, Directory = dir, Cts = cts, HostLoop = Task.CompletedTask };
            session.HostError += e => { lock (rig.Errors) rig.Errors.Add(e); };
            rig.HostLoop = Task.Run(async () =>
            {
                try
                {
                    while (await hostSide.ReceiveAsync(cts.Token) is { } message)
                    {
                        if (message.Length == 0) continue;
                        if (message[0] == HostMessages.FrameRequest) { await hostSide.SendAsync(new[] { HostMessages.Unchanged }); continue; }
                        HostMessages.TryReadId(message, out var id, out _);
                        await onMessage(message[0], id, message, hostSide);
                    }
                }
                catch (OperationCanceledException) { }
            });
            return rig;
        }

        public string[] Files() => System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories);

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Cts.CancelAsync();
            try { await HostLoop; } catch (OperationCanceledException) { }
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
            Cts.Dispose();
        }
    }

    private static byte[] StartMessage(uint id, long size, byte[] hash)
    {
        var header = new byte[8 + 32];
        BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)size);
        hash.CopyTo(header, 8);
        return HostMessages.WithId(HostMessages.ShareFileStart, id, header);
    }

    private static Func<byte, uint, byte[], Pipe, Task> ShareGetHandler(Func<uint, Pipe, Task> reply) =>
        (kind, id, _, host) => kind == HostMessages.ShareGet ? reply(id, host) : Task.CompletedTask;

    [Fact]
    public async Task A_host_that_announces_an_absurd_file_size_is_refused_and_nothing_is_written()
    {
        await using var rig = await Rig.StartAsync(ShareGetHandler((id, host) => host.SendAsync(StartMessage(id, 1L << 40, new byte[32]))));
        var ex = await Assert.ThrowsAsync<IOException>(() => rig.Session.DownloadFileAsync("any.txt", rig.Cts.Token));
        Assert.Contains("not allowed", ex.Message);
        Assert.Empty(rig.Files());
    }

    [Fact]
    public async Task A_host_that_sends_more_than_it_announced_is_cut_off_and_leaves_no_file()
    {
        var hash = SHA256.HashData("hello"u8);
        await using var rig = await Rig.StartAsync(ShareGetHandler(async (id, host) =>
        {
            await host.SendAsync(StartMessage(id, 5, hash));
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileChunk, id, new byte[10]));
        }));
        var ex = await Assert.ThrowsAsync<IOException>(() => rig.Session.DownloadFileAsync("any.txt", rig.Cts.Token));
        Assert.Contains("More data", ex.Message);
        Assert.Empty(rig.Files());
    }

    [Fact]
    public async Task A_host_that_sends_the_wrong_content_is_caught_by_the_checksum_and_leaves_no_file()
    {
        var hash = SHA256.HashData("hello"u8);
        await using var rig = await Rig.StartAsync(ShareGetHandler(async (id, host) =>
        {
            await host.SendAsync(StartMessage(id, 5, hash));
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileChunk, id, "hellX"u8));
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileEnd, id, []));
        }));
        var ex = await Assert.ThrowsAsync<IOException>(() => rig.Session.DownloadFileAsync("any.txt", rig.Cts.Token));
        Assert.Contains("Checksum", ex.Message);
        Assert.Empty(rig.Files());
    }

    [Fact]
    public async Task A_name_with_path_parts_from_a_host_listing_still_lands_inside_the_download_folder()
    {
        var hash = SHA256.HashData("hello"u8);
        await using var rig = await Rig.StartAsync(ShareGetHandler(async (id, host) =>
        {
            await host.SendAsync(StartMessage(id, 5, hash));
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileChunk, id, "hello"u8));
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileEnd, id, []));
        }));
        var saved = await rig.Session.DownloadFileAsync("..\\..\\Startup\\evil.bat", rig.Cts.Token);
        Assert.Equal(rig.Directory, Path.GetDirectoryName(saved));
        Assert.Equal("evil.bat", Path.GetFileName(saved));
        Assert.Equal("hello", await File.ReadAllTextAsync(saved));
        Assert.Single(rig.Files());
    }

    [Fact]
    public async Task Garbage_frames_lists_acks_and_unknown_ids_from_a_host_do_not_end_the_session()
    {
        var hash = SHA256.HashData("ok"u8);
        await using var rig = await Rig.StartAsync(async (kind, id, _, host) =>
        {
            if (kind == HostMessages.ShareList) await host.SendAsync(HostMessages.Prefixed(HostMessages.ShareListReply, "{ not json ["u8));
            if (kind != HostMessages.ShareGet) return;
            await host.SendAsync(HostMessages.Prefixed(HostMessages.FrameReply, new byte[] { 1, 2, 3 }));                      // not a frame
            await host.SendAsync(HostMessages.Prefixed(HostMessages.FrameReply, FrameCodec.Encode(new FrameInfo(1, 1, "x", [])).Select(b => (byte)(b ^ 0xFF)).ToArray()));
            await host.SendAsync(HostMessages.WithId(HostMessages.FileAck, 999, [1, 0xFF, 0xFF]));                             // ack for nothing, lying length
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileChunk, 4242, new byte[100]));                       // chunk for nothing
            await host.SendAsync(new byte[] { 0x7E, 1, 2, 3 });                                                                // unknown kind
            await host.SendAsync(Array.Empty<byte>());                                                                                       // empty message
            await host.SendAsync(StartMessage(id, 2, hash));
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileChunk, id, "ok"u8));
            await host.SendAsync(HostMessages.WithId(HostMessages.ShareFileEnd, id, []));
        });
        rig.Session.FrameReceived += _ => { };   // frames are only decoded when someone listens
        Assert.Empty(await rig.Session.ListSharedFilesAsync(rig.Cts.Token));
        var saved = await rig.Session.DownloadFileAsync("fine.txt", rig.Cts.Token);   // the session still works after all of that
        Assert.Equal("ok", await File.ReadAllTextAsync(saved));
        Assert.True(rig.Errors.Any(e => e.StartsWith("Bad frame")), string.Join(" / ", rig.Errors));
    }

    [Fact]
    public async Task A_host_with_a_different_protocol_version_or_no_hello_is_refused_at_the_start()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (viewerA, hostA) = NewPipe();
        var opening = ViewerSession.OpenAsync(viewerA, "old", cts.Token);
        await hostA.SendAsync(new HelloInfo(HostMessages.ProtocolVersion + 1, HostCapabilities.None).Encode());
        await Assert.ThrowsAsync<HandshakeException>(() => opening);

        var (viewerB, hostB) = NewPipe();
        var opening2 = ViewerSession.OpenAsync(viewerB, "silent", cts.Token);
        await hostB.SendAsync(new byte[] { HostMessages.FrameReply, 1, 2, 3 });
        await Assert.ThrowsAsync<HandshakeException>(() => opening2);
    }
}
