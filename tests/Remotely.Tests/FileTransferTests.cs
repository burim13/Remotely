using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

/// <summary>Helpers shared by the file transfer tests: a viewer-side client wired straight to a host-side endpoint, and temporary folders.</summary>
internal static class TransferRig
{
    internal sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "xfer-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Root);
        public string Dir(string name) { var p = Path.Combine(Root, name); Directory.CreateDirectory(p); return p; }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    /// <summary>
    /// A viewer client connected to a host endpoint through plain method calls. Each message is copied, as the encrypted channel would deliver it.
    /// <see cref="CutViewerToHostAfter"/> / <see cref="CutHostToViewerAfter"/> drop the link after that many data bytes went through.
    /// </summary>
    internal sealed class Loop : IDisposable
    {
        public HostTransferEndpoint Host = null!;
        public TransferClient Client = null!;
        public long DataToHost, DataToViewer, DataToHostAtMark;
        public long CutViewerToHostAfter = -1, CutHostToViewerAfter = -1;
        public readonly List<string> Log = [];
        public Action<long>? OnDataToHost, OnDataToViewer;
        private bool _cut;

        public Loop(string? receive, string? share, string downloads, FileTransferOptions? hostOptions = null, FileTransferOptions? viewerOptions = null, string label = "viewer-1")
        {
            Host = new HostTransferEndpoint(label, receive, share, hostOptions ?? new FileTransferOptions(), (m, ct) =>
            {
                if (_cut) throw new IOException("link cut");
                var span = m.Span;
                if (span[0] == TransferMessages.DataToViewer)
                {
                    DataToViewer += span.Length - TransferMessages.HeaderBytes;
                    if (CutHostToViewerAfter >= 0 && DataToViewer > CutHostToViewerAfter) { Cut(); throw new IOException("link cut"); }
                    OnDataToViewer?.Invoke(DataToViewer);
                }
                Client.Handle(m.ToArray());
                return Task.CompletedTask;
            }, s => { lock (Log) Log.Add(s); });
            Client = new TransferClient((m, ct) =>
            {
                if (_cut) throw new IOException("link cut");
                var span = m.Span;
                if (span[0] == TransferMessages.Data)
                {
                    if (CutViewerToHostAfter >= 0 && DataToHost >= CutViewerToHostAfter) { Cut(); throw new IOException("link cut"); }
                    DataToHost += span.Length - TransferMessages.HeaderBytes;
                    OnDataToHost?.Invoke(DataToHost);
                }
                return Host.HandleAsync(m.ToArray(), ct);
            }, label, viewerOptions ?? new FileTransferOptions(), () => downloads);
        }

        public void Cut()
        {
            _cut = true;
            Client.Fail("link cut");
            Host.Dispose();
        }

        public void Dispose() => Host.Dispose();
    }

    public static async Task<T> Eventually<T>(Func<T?> probe, string what, int seconds = 15) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(20);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    public static string RandomFile(string path, long size, int seed = 7)
    {
        var rng = new Random(seed);
        var buffer = new byte[64 * 1024];
        using var fs = File.Create(path);
        for (long left = size; left > 0;)
        {
            var n = (int)Math.Min(buffer.Length, left);
            rng.NextBytes(buffer.AsSpan(0, n));
            fs.Write(buffer, 0, n);
            left -= n;
        }
        return path;
    }

    public static byte[] Sha(string path) { using var fs = File.OpenRead(path); return SHA256.HashData(fs); }

    public static TransferItem UploadItem(string path) =>
        new(TransferDirection.Upload, Path.GetFileName(path), path, Directory.Exists(path), -1);

    public static TransferItem DownloadItem(string name, bool folder = false) => new(TransferDirection.Download, name, null, folder, -1);

    /// <summary>All files and folders under a directory as relative paths with '/', for comparing trees.</summary>
    public static string[] Tree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/') + (Directory.Exists(p) ? "/" : "")).OrderBy(x => x, StringComparer.Ordinal).ToArray();
}

/// <summary>A real server, host agent (with file options) and viewer, for tests that go through <see cref="HostAgent"/> itself.</summary>
internal sealed class FileRig : IAsyncDisposable
{
    private TestServer _server = null!;
    private Remotely.Client.CoordinatorClient _host = null!, _viewer = null!;
    private Remotely.Core.Crypto.DeviceIdentity _hostId = null!, _viewerId = null!;
    private Task _hosting = null!;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));

    public static async Task<FileRig> StartAsync(string receive, FileTransferOptions options, string? share = null)
    {
        var rig = new FileRig { _server = await TestServer.StartAsync() };
        (rig._host, rig._hostId) = await rig._server.PairedDeviceAsync("Host", canHost: true);
        (rig._viewer, rig._viewerId) = await rig._server.PairedDeviceAsync("Viewer", canHost: false);
        var services = new HostServices(new HostCoreTests.FakeScreen(() => new FrameInfo(8, 8, "Default", [1])))
        {
            IsTrusted = _ => true, CanControl = _ => true, ReceiveDirectory = receive, ShareDirectory = share,
        };
        var agent = new HostAgent(rig._hostId, services, _ => { }) { FileOptions = options };
        rig._hosting = agent.RunAsync(rig._host, rig._cts.Token);
        return rig;
    }

    public Task<ViewerSession> OpenAsync() => ViewerSession.OpenAsync(_viewer, _viewerId, _hostId.DeviceId, _cts.Token);

    public async ValueTask DisposeAsync()
    {
        await _viewer.DisposeAsync();
        await _host.DisposeAsync();
        try { await _hosting.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        await _server.DisposeAsync();
        _cts.Dispose();
    }
}

public class FileTransferTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_file_is_uploaded_with_its_checksum_and_never_overwrites_an_existing_name()
    {
        using var t = new TransferRig.Temp();
        var source = TransferRig.RandomFile(Path.Combine(t.Root, "report.bin"), 1_300_000);
        using var loop = new TransferRig.Loop(t.Dir("in"), null, t.Dir("dl"));

        var first = TransferRig.UploadItem(source);
        await loop.Client.UploadAsync(first, CancellationToken.None).WaitAsync(Limit);
        var second = TransferRig.UploadItem(source);
        await loop.Client.UploadAsync(second, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal("report.bin", first.SavedName);
        Assert.Equal("report (1).bin", second.SavedName);
        Assert.Equal(TransferRig.Sha(source), TransferRig.Sha(Path.Combine(t.Root, "in", "report.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Root, "in"), "*.part"));
        Assert.Equal(1_300_000, first.BytesDone);
        Assert.Equal(TransferState.Completed, first.State);
    }

    [Fact]
    public async Task An_upload_resumes_after_the_link_dropped_and_only_sends_what_is_missing()
    {
        using var t = new TransferRig.Temp();
        const long size = 5_000_000;
        var source = TransferRig.RandomFile(Path.Combine(t.Root, "big.bin"), size);
        var receive = t.Dir("in");

        long held;
        using (var first = new TransferRig.Loop(receive, null, t.Dir("dl")) { CutViewerToHostAfter = 4L * TransferMessages.ChunkBytes })
        {
            var attempt = TransferRig.UploadItem(source);
            var ex = await Assert.ThrowsAsync<TransferException>(() => first.Client.UploadAsync(attempt, CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.LinkLost, ex.Code);
            var part = Assert.Single(Directory.GetFiles(receive, "*.part"));   // the interrupted transfer left its partial file for the next attempt
            held = new FileInfo(part).Length;
            Assert.Equal(4L * TransferMessages.ChunkBytes, held);
            Assert.DoesNotContain(Directory.GetFiles(receive), f => !f.EndsWith(".part"));
        }

        using var second = new TransferRig.Loop(receive, null, t.Dir("dl"));
        var retry = TransferRig.UploadItem(source);
        await second.Client.UploadAsync(retry, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(held, retry.ResumedFrom);                                        // the receiver said where it stopped
        Assert.Equal(size - held, second.DataToHost);                                 // and only the rest was sent
        Assert.Equal(TransferRig.Sha(source), TransferRig.Sha(Path.Combine(receive, "big.bin")));
        Assert.Empty(Directory.GetFiles(receive, "*.part"));
    }

    [Fact]
    public async Task A_download_resumes_after_the_link_dropped()
    {
        using var t = new TransferRig.Temp();
        const long size = 4_200_000;
        var share = t.Dir("share");
        var source = TransferRig.RandomFile(Path.Combine(share, "movie.bin"), size, seed: 11);
        var downloads = t.Dir("dl");

        using (var first = new TransferRig.Loop(null, share, downloads) { CutHostToViewerAfter = 3L * TransferMessages.ChunkBytes })
        {
            var item = TransferRig.DownloadItem("movie.bin");
            var ex = await Assert.ThrowsAsync<TransferException>(() => first.Client.DownloadAsync(item, CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.LinkLost, ex.Code);
            Assert.Single(Directory.GetFiles(downloads, "*.part"));
        }

        using var second = new TransferRig.Loop(null, share, downloads);
        var retry = TransferRig.DownloadItem("movie.bin");
        await second.Client.DownloadAsync(retry, CancellationToken.None).WaitAsync(Limit);

        Assert.True(retry.ResumedFrom > 0);
        Assert.Equal(size - retry.ResumedFrom, second.DataToViewer);
        Assert.Equal(TransferRig.Sha(source), TransferRig.Sha(Path.Combine(downloads, "movie.bin")));
        Assert.Empty(Directory.GetFiles(downloads, "*.part"));
    }

    [Fact]
    public async Task A_nested_folder_with_unicode_names_arrives_intact_and_can_also_be_kept_as_a_zip()
    {
        using var t = new TransferRig.Temp();
        var folder = Path.Combine(t.Root, "Fotografi të verës");
        Directory.CreateDirectory(Path.Combine(folder, "日本語", "Ünïcödé 🎉"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        TransferRig.RandomFile(Path.Combine(folder, "përshëndetje.txt"), 1000, 1);
        TransferRig.RandomFile(Path.Combine(folder, "日本語", "файл.bin"), 700_000, 2);
        TransferRig.RandomFile(Path.Combine(folder, "日本語", "Ünïcödé 🎉", "deep file (1).dat"), 70_000, 3);
        File.WriteAllBytes(Path.Combine(folder, "zero.bin"), []);

        var receive = t.Dir("in");
        using (var loop = new TransferRig.Loop(receive, null, t.Dir("dl")))
        {
            var item = TransferRig.UploadItem(folder);
            Assert.True(item.IsFolder);
            await loop.Client.UploadAsync(item, CancellationToken.None).WaitAsync(Limit);
            Assert.Equal("Fotografi të verës", item.SavedName);
        }
        var landed = Path.Combine(receive, "Fotografi të verës");
        Assert.Equal(TransferRig.Tree(folder), TransferRig.Tree(landed));
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            Assert.Equal(TransferRig.Sha(file), TransferRig.Sha(Path.Combine(landed, Path.GetRelativePath(folder, file))));
        Assert.DoesNotContain(Directory.GetFiles(receive, "*", SearchOption.AllDirectories), f => f.EndsWith(".part") || f.EndsWith(".zip"));

        // Same folder again with unpacking switched off: the zip is kept and its entry names are real UTF-8.
        var keep = t.Dir("zips");
        using (var loop = new TransferRig.Loop(keep, null, t.Dir("dl2"), new FileTransferOptions { ExtractFolders = false }))
            await loop.Client.UploadAsync(TransferRig.UploadItem(folder), CancellationToken.None).WaitAsync(Limit);
        using var zip = ZipFile.OpenRead(Path.Combine(keep, "Fotografi të verës.zip"));
        Assert.Contains("Fotografi të verës/日本語/файл.bin", zip.Entries.Select(e => e.FullName));
        Assert.Contains("Fotografi të verës/日本語/Ünïcödé 🎉/deep file (1).dat", zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task A_shared_folder_can_be_downloaded_and_a_name_with_path_parts_is_refused()
    {
        using var t = new TransferRig.Temp();
        var share = t.Dir("share");
        var folder = Path.Combine(share, "Projekt");
        Directory.CreateDirectory(Path.Combine(folder, "ndërfaqe"));
        TransferRig.RandomFile(Path.Combine(folder, "ndërfaqe", "a.bin"), 90_000, 5);
        TransferRig.RandomFile(Path.Combine(share, "single.bin"), 10_000, 6);
        File.WriteAllText(Path.Combine(t.Root, "secret.txt"), "no");
        var downloads = t.Dir("dl");
        using var loop = new TransferRig.Loop(null, share, downloads);

        var list = await loop.Client.ListAsync(CancellationToken.None).WaitAsync(Limit);
        Assert.Equal([("Projekt", true), ("single.bin", false)], list.Select(e => (e.Name, e.IsFolder)).ToArray());

        var item = TransferRig.DownloadItem("Projekt", folder: true);
        await loop.Client.DownloadAsync(item, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(TransferRig.Tree(folder), TransferRig.Tree(Path.Combine(downloads, "Projekt")));

        foreach (var bad in new[] { "../secret.txt", @"..\secret.txt", Path.Combine(t.Root, "secret.txt"), "Projekt/ndërfaqe", "missing" })
        {
            var ex = await Assert.ThrowsAsync<TransferException>(() => loop.Client.DownloadAsync(TransferRig.DownloadItem(bad), CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.NotFound, ex.Code);
        }
        Assert.False(File.Exists(Path.Combine(downloads, "secret.txt")));
    }

    [Fact]
    public async Task A_corrupted_transfer_is_detected_and_leaves_nothing_behind()
    {
        using var t = new TransferRig.Temp();
        var receive = t.Dir("in");
        var sent = new List<byte[]>();
        using var host = new HostTransferEndpoint("v", receive, null, new FileTransferOptions(), (m, _) => { lock (sent) sent.Add(m.ToArray()); return Task.CompletedTask; }, _ => { });

        var content = "hello world"u8.ToArray();
        var announce = new TransferWire.Announce(false, 0, RandomNumberGenerator.GetBytes(16), content.Length, "bad.txt");
        await host.HandleAsync(TransferWire.EncodeAnnounce(TransferMessages.Offer, 9, announce), CancellationToken.None);
        await TransferRig.Eventually(() => { lock (sent) return sent.FirstOrDefault(m => m[0] == TransferMessages.Accept); }, "accept");

        var data = new byte[8 + content.Length];
        content.CopyTo(data, 8);
        await host.HandleAsync(HostMessages.WithId(TransferMessages.Data, 9, data), CancellationToken.None);
        await host.HandleAsync(TransferWire.EncodeEnd(TransferMessages.End, 9, content.Length, SHA256.HashData("something else"u8)), CancellationToken.None);

        var result = await TransferRig.Eventually(() => { lock (sent) return sent.FirstOrDefault(m => m[0] == TransferMessages.Result); }, "result");
        Assert.True(TransferWire.TryReadResult(result.AsSpan(5), out var code, out _));
        Assert.Equal(TransferError.HashMismatch, code);
        Assert.Empty(Directory.GetFileSystemEntries(receive));   // no final file, no .part: the result was sent after the cleanup
    }

    [Fact]
    public async Task Cancelling_removes_the_partial_file_on_the_receiver()
    {
        using var t = new TransferRig.Temp();
        var source = TransferRig.RandomFile(Path.Combine(t.Root, "long.bin"), 6_000_000);
        var receive = t.Dir("in");
        using var cts = new CancellationTokenSource();
        using var loop = new TransferRig.Loop(receive, null, t.Dir("dl"));
        var item = TransferRig.UploadItem(source);
        loop.OnDataToHost = sent => { if (sent >= 2_000_000) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.Client.UploadAsync(item, cts.Token).WaitAsync(Limit));
        await TransferRig.Eventually(() => Directory.GetFileSystemEntries(receive).Length == 0 ? receive : null, "partial file removed");
        Assert.Empty(Directory.GetFileSystemEntries(receive));
    }

    [Fact]
    public async Task Cancelling_a_download_removes_its_partial_file_and_stops_the_host()
    {
        using var t = new TransferRig.Temp();
        var share = t.Dir("share");
        TransferRig.RandomFile(Path.Combine(share, "long.bin"), 6_000_000);
        var downloads = t.Dir("dl");
        using var cts = new CancellationTokenSource();
        using var loop = new TransferRig.Loop(null, share, downloads);
        var item = TransferRig.DownloadItem("long.bin");
        loop.OnDataToViewer = sent => { if (sent >= 1_500_000) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.Client.DownloadAsync(item, cts.Token).WaitAsync(Limit));
        Assert.Empty(Directory.GetFileSystemEntries(downloads));
    }

    [Fact]
    public async Task Size_and_quota_limits_and_free_space_are_enforced_with_clear_codes()
    {
        using var t = new TransferRig.Temp();
        var source = TransferRig.RandomFile(Path.Combine(t.Root, "one.bin"), 2_000_000);
        var receive = t.Dir("in");

        using (var loop = new TransferRig.Loop(receive, null, t.Dir("dl"), new FileTransferOptions { MaxFileBytes = 1_000_000 }))
        {
            var ex = await Assert.ThrowsAsync<TransferException>(() => loop.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.TooLarge, ex.Code);
        }
        using (var loop = new TransferRig.Loop(receive, null, t.Dir("dl"), new FileTransferOptions { ReceiveQuotaBytes = 3_000_000 }))
        {
            await loop.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit);                    // 2 MB of 3 MB
            var ex = await Assert.ThrowsAsync<TransferException>(() => loop.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.QuotaExceeded, ex.Code);                                                                          // would be 4 MB
        }
        using (var loop = new TransferRig.Loop(receive, null, t.Dir("dl"), new FileTransferOptions { MinFreeDiskBytes = long.MaxValue / 4 }))
        {
            var ex = await Assert.ThrowsAsync<TransferException>(() => loop.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.DiskFull, ex.Code);
        }
        Assert.Empty(Directory.GetFiles(receive, "*.part"));
        Assert.Single(Directory.GetFiles(receive));

        // A folder's size is only known after zipping: the running total stops it.
        var folder = Path.Combine(t.Root, "bulk");
        Directory.CreateDirectory(folder);
        TransferRig.RandomFile(Path.Combine(folder, "a.bin"), 3_000_000);
        using (var loop = new TransferRig.Loop(t.Dir("in2"), null, t.Dir("dl"), new FileTransferOptions { MaxFolderBytes = 1_000_000 }))
        {
            var ex = await Assert.ThrowsAsync<TransferException>(() => loop.Client.UploadAsync(TransferRig.UploadItem(folder), CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.TooLarge, ex.Code);
        }
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(t.Root, "in2")));
    }

    [Fact]
    public void Failures_of_the_file_system_are_translated_into_clear_codes()
    {
        Assert.Equal(TransferError.AccessDenied, TransferException.From(new UnauthorizedAccessException()).Code);
        Assert.Equal(TransferError.NotFound, TransferException.From(new FileNotFoundException()).Code);
        Assert.Equal(TransferError.NotFound, TransferException.From(new DirectoryNotFoundException()).Code);
        var full = new IOException("disk", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);
        Assert.Equal(TransferError.DiskFull, TransferException.From(full).Code);
        Assert.Contains("free space", TransferException.From(full).Message);
        Assert.Equal(TransferError.Other, TransferException.From(new IOException("x", 5)).Code);
    }

    [Fact]
    public async Task The_owner_is_asked_before_new_files_but_not_when_an_interrupted_one_resumes()
    {
        using var t = new TransferRig.Temp();
        var source = TransferRig.RandomFile(Path.Combine(t.Root, "ask.bin"), 3_000_000);
        var receive = t.Dir("in");
        var asked = new List<IncomingFileRequest>();
        var answer = false;
        var options = new FileTransferOptions { ConfirmIncoming = (r, _) => { lock (asked) asked.Add(r); return Task.FromResult(answer); } };

        using (var loop = new TransferRig.Loop(receive, null, t.Dir("dl"), options))
        {
            var ex = await Assert.ThrowsAsync<TransferException>(() => loop.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit));
            Assert.Equal(TransferError.Declined, ex.Code);
        }
        Assert.Empty(Directory.GetFileSystemEntries(receive));
        Assert.Equal("ask.bin", asked[0].Name);
        Assert.Equal(3_000_000, asked[0].Size);

        answer = true;
        using (var loop = new TransferRig.Loop(receive, null, t.Dir("dl"), options) { CutViewerToHostAfter = 2L * TransferMessages.ChunkBytes })
            await Assert.ThrowsAsync<TransferException>(() => loop.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit));
        Assert.Equal(2, asked.Count);

        using (var loop = new TransferRig.Loop(receive, null, t.Dir("dl"), options))
            await loop.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(2, asked.Count);   // the resumed attempt did not ask again
        Assert.Equal(TransferRig.Sha(source), TransferRig.Sha(Path.Combine(receive, "ask.bin")));

        // A hook that never answers means "no" after the timeout; the viewer is not left hanging.
        var silent = new FileTransferOptions { ConfirmTimeout = TimeSpan.FromMilliseconds(200), ConfirmIncoming = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; } };
        using var quiet = new TransferRig.Loop(t.Dir("in3"), null, t.Dir("dl"), silent);
        var timedOut = await Assert.ThrowsAsync<TransferException>(() => quiet.Client.UploadAsync(TransferRig.UploadItem(source), CancellationToken.None).WaitAsync(Limit));
        Assert.Equal(TransferError.Declined, timedOut.Code);
    }

    [Fact]
    public void An_unpacked_folder_cannot_write_outside_its_folder_and_links_and_bombs_are_refused()
    {
        using var t = new TransferRig.Temp();
        var out1 = t.Dir("out");
        var options = new FileTransferOptions();

        foreach (var evil in new[] { "../evil.txt", "a/../../evil.txt", "/abs/evil.txt", @"C:\evil.txt", @"..\evil.txt" })
        {
            var zip = Path.Combine(t.Root, "evil.zip");
            File.Delete(zip);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry(evil).Open())) w.Write("x");
            var ex = Assert.Throws<TransferException>(() => SafeZip.ExtractToUniqueFolder(zip, out1, "evil", options));
            Assert.Equal(TransferError.UnsafeArchive, ex.Code);
        }
        Assert.False(File.Exists(Path.Combine(t.Root, "evil.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(out1));   // not even a half-made folder

        // Too many entries, and a declared size over the limit.
        var many = Path.Combine(t.Root, "many.zip");
        using (var z = ZipFile.Open(many, ZipArchiveMode.Create)) for (var i = 0; i < 20; i++) z.CreateEntry($"d/{i}.txt");
        Assert.Equal(TransferError.UnsafeArchive, Assert.Throws<TransferException>(() => SafeZip.ExtractToUniqueFolder(many, out1, "many", new FileTransferOptions { MaxZipEntries = 5 })).Code);
        var big = Path.Combine(t.Root, "big.zip");
        using (var z = ZipFile.Open(big, ZipArchiveMode.Create))
        using (var s = z.CreateEntry("d/zeros.bin", CompressionLevel.Optimal).Open()) s.Write(new byte[2_000_000]);
        Assert.Equal(TransferError.TooLarge, Assert.Throws<TransferException>(() => SafeZip.ExtractToUniqueFolder(big, out1, "big", new FileTransferOptions { MaxFolderBytes = 1_000_000 })).Code);

        // A symlink entry is skipped, and names invalid on Windows are made valid.
        var odd = Path.Combine(t.Root, "odd.zip");
        using (var z = ZipFile.Open(odd, ZipArchiveMode.Create))
        {
            var link = z.CreateEntry("pack/link");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using (var w = new StreamWriter(link.Open())) w.Write("/etc/passwd");
            using (var w = new StreamWriter(z.CreateEntry("pack/what?.txt").Open())) w.Write("ok");
            using (var w = new StreamWriter(z.CreateEntry("pack/CON.txt").Open())) w.Write("ok");
        }
        var name = SafeZip.ExtractToUniqueFolder(odd, out1, "odd", options);
        Assert.Equal("pack", name);
        Assert.Equal(new[] { "_CON.txt", "what.txt" }, Directory.GetFiles(Path.Combine(out1, "pack")).Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("pack (1)", SafeZip.ExtractToUniqueFolder(odd, out1, "odd", options));   // a taken folder name is never reused
    }

    [Fact]
    public void Wire_messages_round_trip_and_hostile_input_is_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var a = new TransferWire.Announce(true, 3, key, 1234, "Fotografi të verës.zip");
        var message = TransferWire.EncodeAnnounce(TransferMessages.Offer, 77, a);
        Assert.True(HostMessages.TryReadId(message, out var id, out var body));
        Assert.Equal(77u, id);
        Assert.True(TransferWire.TryReadAnnounce(body, out var back));
        Assert.Equal(a.Name, back.Name);
        Assert.Equal(key, back.Key);
        Assert.False(back.Resumable);
        Assert.False(back.SizeKnown);
        Assert.False(TransferWire.TryReadAnnounce(body[..^1], out _));                                   // truncated
        var lying = body.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(lying.AsSpan(26), 9999);       // name length lies
        Assert.False(TransferWire.TryReadAnnounce(lying, out _));
        var badUtf8 = TransferWire.EncodeAnnounce(TransferMessages.Offer, 1, new TransferWire.Announce(false, 0, key, 1, "ab"));
        badUtf8[^1] = 0xFF;
        Assert.False(TransferWire.TryReadAnnounce(badUtf8.AsSpan(5), out _));
        var negative = body.ToArray(); BinaryPrimitives.WriteInt64BigEndian(negative.AsSpan(18), -5);
        Assert.False(TransferWire.TryReadAnnounce(negative, out _));

        Assert.True(TransferWire.TryReadResult(TransferWire.EncodeResult(5, TransferError.QuotaExceeded, "limit").AsSpan(5), out var code, out var text));
        Assert.Equal((TransferError.QuotaExceeded, "limit"), (code, text));
        Assert.False(TransferWire.TryReadResult([1, 0], out _, out _));
        Assert.False(TransferWire.TryReadOffset([1, 2, 3], out _));
        Assert.False(TransferWire.TryReadEnd(new byte[39], out _, out _));
        Assert.False(TransferWire.TryReadPull([0, 5, 1], out _));
        Assert.True(TransferWire.TryReadPull(TransferWire.EncodePull(1, "naïve.txt").AsSpan(5), out var pulled));
        Assert.Equal("naïve.txt", pulled);

        // New ids stay inside their reserved ranges and do not collide with the original ones.
        byte[] viewerToHost = [TransferMessages.Offer, TransferMessages.Data, TransferMessages.End, TransferMessages.Cancel, TransferMessages.Pull, TransferMessages.ListEntries, TransferMessages.Start];
        byte[] hostToViewer = [TransferMessages.Accept, TransferMessages.DataToViewer, TransferMessages.EndToViewer, TransferMessages.Result, TransferMessages.Begin, TransferMessages.EntriesReply];
        Assert.All(viewerToHost, b => Assert.InRange(b, TransferMessages.FirstViewerToHost, TransferMessages.LastViewerToHost));
        Assert.All(hostToViewer, b => Assert.InRange(b, TransferMessages.FirstHostToViewer, TransferMessages.LastHostToViewer));
        Assert.Equal(viewerToHost.Length, viewerToHost.Distinct().Count());
        Assert.Equal(hostToViewer.Length, hostToViewer.Distinct().Count());
        Assert.Equal(128, (int)HostCapabilities.ResumableFiles);
    }

    [Fact]
    public async Task A_host_endpoint_survives_garbage_and_wrong_order_messages()
    {
        using var t = new TransferRig.Temp();
        var sent = new List<byte[]>();
        using var host = new HostTransferEndpoint("v", t.Dir("in"), t.Dir("share"), new FileTransferOptions(), (m, _) => { lock (sent) sent.Add(m.ToArray()); return Task.CompletedTask; }, _ => { });
        foreach (var message in new[]
        {
            new byte[] { TransferMessages.Offer }, new byte[] { TransferMessages.Offer, 0, 0, 0, 1 }, new byte[] { TransferMessages.Offer, 0, 0, 0, 1, 1, 2, 3 },
            HostMessages.WithId(TransferMessages.Data, 99, [1, 2, 3]), HostMessages.WithId(TransferMessages.End, 99, []), HostMessages.WithId(TransferMessages.Start, 99, []),
            HostMessages.WithId(TransferMessages.Pull, 5, [255, 255]), HostMessages.WithId(0x4F, 5, []), HostMessages.WithId(TransferMessages.Cancel, 12345, []),
        })
            await host.HandleAsync(message, CancellationToken.None);   // none of these may throw
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(t.Root, "in")));
    }

    [Fact]
    public async Task The_queue_resumes_interrupted_items_when_a_new_link_is_attached_and_can_cancel_and_retry()
    {
        using var t = new TransferRig.Temp();
        var source = TransferRig.RandomFile(Path.Combine(t.Root, "queued.bin"), 5_000_000);
        var receive = t.Dir("in");
        using var queue = new TransferQueue();

        var first = new TransferRig.Loop(receive, null, t.Dir("dl")) { CutViewerToHostAfter = 3L * TransferMessages.ChunkBytes };
        queue.Attach(first.Client);
        var item = queue.EnqueueUpload(source);
        await TransferRig.Eventually(() => item.State == TransferState.Interrupted ? item : null, "interrupted");
        Assert.Equal(TransferError.LinkLost, item.Error);
        first.Dispose();

        queue.Attach(null);
        Assert.Equal(TransferState.Interrupted, item.State);   // nothing runs without a link
        using var second = new TransferRig.Loop(receive, null, t.Dir("dl"));
        queue.Attach(second.Client);
        await TransferRig.Eventually(() => item.State == TransferState.Completed ? item : null, "completed after reconnect");
        Assert.Equal(3L * TransferMessages.ChunkBytes, item.ResumedFrom);
        Assert.Equal(2, item.Attempts);
        Assert.Equal(TransferRig.Sha(source), TransferRig.Sha(Path.Combine(receive, "queued.bin")));

        // A rejected item is Failed (not Interrupted) and can be retried once the cause is gone.
        var missing = queue.EnqueueUpload(Path.Combine(t.Root, "nope.bin"));
        await TransferRig.Eventually(() => missing.State == TransferState.Failed ? missing : null, "failed");
        Assert.Equal(TransferError.NotFound, missing.Error);
        TransferRig.RandomFile(Path.Combine(t.Root, "nope.bin"), 1000);
        queue.Retry(missing);
        await TransferRig.Eventually(() => missing.State == TransferState.Completed ? missing : null, "completed on retry");

        // Cancelling a running item removes the partial file.
        var slow = TransferRig.RandomFile(Path.Combine(t.Root, "slow.bin"), 8_000_000, 3);
        second.OnDataToHost = sent => { if (sent > second.DataToHostAtMark + 1_000_000) queue.Cancel(queue.Items.Last()); };
        second.DataToHostAtMark = second.DataToHost;
        var running = queue.EnqueueUpload(slow);
        await TransferRig.Eventually(() => running.State == TransferState.Cancelled ? running : null, "cancelled");
        await TransferRig.Eventually(() => Directory.GetFiles(receive, "*.part").Length == 0 ? receive : null, "partial removed");
        queue.ClearFinished();
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task Existing_clients_keep_working_the_old_upload_flow_is_subject_to_the_new_limits_and_consent()
    {
        using var t = new TransferRig.Temp();
        var receive = t.Dir("in");
        var data = RandomNumberGenerator.GetBytes(300_000);
        var file = Path.Combine(t.Root, "legacy.bin");
        await File.WriteAllBytesAsync(file, data);
        var asked = 0;
        var answer = true;
        var options = new FileTransferOptions { ReceiveQuotaBytes = 500_000, ConfirmIncoming = (r, _) => { asked++; return Task.FromResult(answer); } };

        await using var rig = await FileRig.StartAsync(receive, options);
        await using var session = await rig.OpenAsync();
        Assert.True(session.HasFiles);

        Assert.Equal("legacy.bin", await session.UploadFileAsync(file));
        Assert.Equal(1, asked);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(receive, "legacy.bin")));

        var over = await Assert.ThrowsAsync<IOException>(() => session.UploadFileAsync(file));   // 300 KB + 300 KB > the 500 KB quota
        Assert.Contains("limited", over.Message);
        answer = false;
        File.Delete(Path.Combine(receive, "legacy.bin"));
        var declined = await Assert.ThrowsAsync<IOException>(() => session.UploadFileAsync(file));
        Assert.Contains("declined", declined.Message);
        Assert.Empty(Directory.GetFileSystemEntries(receive));
    }
}

/// <summary>Heavy tests: they run alone, because they measure the memory of the whole process.</summary>
[CollectionDefinition("Serial transfer tests", DisableParallelization = true)]
public sealed class SerialTransferCollection;

[Collection("Serial transfer tests")]
public class LargeFileTransferTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const long TwoGiB = 2L << 30;

    private static string SparseFile(string path, long size)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(size);   // sparse on ext4/NTFS-with-flag: no disk is used until the receiver writes it
        return path;
    }

    [Fact]
    public async Task Sending_a_two_gigabyte_file_allocates_almost_nothing_per_chunk()
    {
        using var t = new TransferRig.Temp();
        var source = TransferSource.ForFile(SparseFile(Path.Combine(t.Root, "sparse.bin"), TwoGiB));
        long sent = 0;
        // warm up the paths first so one-time costs are not counted
        await TransferPump.RunAsync(TransferSource.ForFile(SparseFile(Path.Combine(t.Root, "warm.bin"), 8_000_000)), TransferMessages.Data, 1, 0, (m, _) => Task.CompletedTask, null, CancellationToken.None);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var (total, hash) = await TransferPump.RunAsync(source, TransferMessages.Data, 1, 0, (m, _) => { sent += m.Length - TransferMessages.HeaderBytes; return Task.CompletedTask; }, null, CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        output.WriteLine($"2 GiB sent in {TwoGiB / TransferMessages.ChunkBytes} chunks; allocated {allocated / 1024.0:N0} KiB in total");
        Assert.Equal(TwoGiB, total);
        Assert.Equal(TwoGiB, sent);
        Assert.Equal(32, hash.Length);
        Assert.True(allocated < 16L << 20, $"Sending 2 GiB allocated {allocated:N0} bytes: the chunk buffer must be reused, not allocated per chunk.");
    }

    [Fact]
    public async Task A_two_gigabyte_file_transfers_end_to_end_with_flat_memory()
    {
        using var t = new TransferRig.Temp();
        var source = SparseFile(Path.Combine(t.Root, "sparse.bin"), TwoGiB);
        var receive = t.Dir("in");
        using var loop = new TransferRig.Loop(receive, null, t.Dir("dl"));

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(10);
            }
        });

        var item = TransferRig.UploadItem(source);
        await loop.Client.UploadAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
        stop.Cancel();
        await sampler;

        var growth = peak - baseline;
        output.WriteLine($"2 GiB transferred; peak managed heap grew by {growth / (1024.0 * 1024):N1} MiB over a {baseline / (1024.0 * 1024):N1} MiB baseline");
        var stored = new FileInfo(Path.Combine(receive, "sparse.bin"));
        Assert.Equal(TwoGiB, stored.Length);
        Assert.Equal(TwoGiB, item.BytesDone);
        Assert.Empty(Directory.GetFiles(receive, "*.part"));
        Assert.True(growth < 128L << 20, $"Peak managed memory grew by {growth / (1024 * 1024)} MiB while moving 2 GiB: the transfer is buffering instead of streaming.");
    }
}
