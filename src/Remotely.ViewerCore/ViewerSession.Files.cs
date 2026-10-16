using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.HostCore;

namespace Remotely.ViewerCore;

public sealed partial class ViewerSession
{
    // ---- files ----

    /// <summary>Uploads a file into the host's receive folder. Returns the name it was saved under.</summary>
    public async Task<string> UploadFileAsync(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!HasFiles) throw new InvalidOperationException("The host does not accept files.");
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > HostMessages.MaxFileBytes) throw new InvalidOperationException("That file is too large to send.");
        var hash = await SHA256.HashDataAsync(stream, link.Token).ConfigureAwait(false);
        stream.Position = 0;

        var id = Interlocked.Increment(ref _nextId);
        var ack = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acks[id] = ack;
        try
        {
            var name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            var header = new byte[8 + 32 + 2 + name.Length];
            BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)stream.Length);
            hash.CopyTo(header, 8);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(40), (ushort)name.Length);
            name.CopyTo(header, 42);
            await _secure.SendAsync(HostMessages.WithId(HostMessages.FileStart, id, header), link.Token).ConfigureAwait(false);

            var buffer = new byte[HostMessages.FileChunkBytes];
            long sent = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, link.Token).ConfigureAwait(false)) > 0)
            {
                if (ack.Task.IsCompleted) break; // host refused or failed: stop sending
                await _secure.SendAsync(HostMessages.WithId(HostMessages.FileChunk, id, buffer.AsSpan(0, read)), link.Token).ConfigureAwait(false);
                sent += read;
                progress?.Report(stream.Length == 0 ? 1 : (double)sent / stream.Length);
            }
            if (!ack.Task.IsCompleted)
                await _secure.SendAsync(HostMessages.WithId(HostMessages.FileEnd, id, []), link.Token).ConfigureAwait(false);
            return await ack.Task.WaitAsync(TimeSpan.FromSeconds(30), link.Token).ConfigureAwait(false);
        }
        finally
        {
            _acks.TryRemove(id, out _);
        }
    }

    public async Task<ShareEntry[]> ListSharedFilesAsync(CancellationToken ct = default)
    {
        if (!HasFiles) return [];
        var waiter = new TaskCompletionSource<ShareEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listWaiter = waiter;
        await _secure.SendAsync(new[] { HostMessages.ShareList }, ct).ConfigureAwait(false);
        return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a file from the host's shared folder into <see cref="DownloadDirectory"/>. Returns the saved path.</summary>
    public async Task<string> DownloadFileAsync(string name, CancellationToken ct = default)
    {
        if (!HasFiles) throw new InvalidOperationException("The host does not share files.");
        var id = Interlocked.Increment(ref _nextId);
        var download = new Download();
        _downloads[id] = download;
        try
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var payload = new byte[2 + nameBytes.Length];
            BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)nameBytes.Length);
            nameBytes.CopyTo(payload, 2);
            await _secure.SendAsync(HostMessages.WithId(HostMessages.ShareGet, id, payload), ct).ConfigureAwait(false);
            download.RequestedName = name;
            return await download.Done.Task.WaitAsync(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        }
        finally
        {
            _downloads.TryRemove(id, out var d);
            d?.Sink?.Dispose();
        }
    }

    private sealed class Download
    {
        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileSink? Sink { get; set; }
        public string RequestedName { get; set; } = "";
    }
}
