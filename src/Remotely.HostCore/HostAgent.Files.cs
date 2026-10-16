using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;

namespace Remotely.HostCore;

public sealed partial class HostAgent
{
    private async Task HandleUploadAsync(Session s, byte[] message, CancellationToken ct)
    {
        if (!HostMessages.TryReadId(message, out var id, out var rest)) { await ErrorAsync(s, "Bad file message.", ct).ConfigureAwait(false); return; }
        switch (message[0])
        {
            case HostMessages.FileStart:
                s.Upload?.Dispose();
                s.Upload = null;
                try
                {
                    if (rest.Length < 8 + 32 + 2) throw new InvalidDataException("Bad file header.");
                    var size = (long)BinaryPrimitives.ReadUInt64BigEndian(rest);
                    var hash = rest.Slice(8, 32).ToArray();
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(rest[40..]);
                    if (rest.Length != 42 + nameLength) throw new InvalidDataException("Bad file header.");
                    s.Upload = FileSink.Create(services.ReceiveDirectory!, Encoding.UTF8.GetString(rest.Slice(42, nameLength)), size, hash);
                    s.UploadId = id;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    await AckAsync(s, id, false, ex.Message, ct).ConfigureAwait(false);
                }
                break;
            case HostMessages.FileChunk when s.Upload is not null && s.UploadId == id:
                try { s.Upload.Write(rest); }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    s.Upload.Dispose(); s.Upload = null;
                    await AckAsync(s, id, false, ex.Message, ct).ConfigureAwait(false);
                }
                break;
            case HostMessages.FileEnd when s.Upload is not null && s.UploadId == id:
                string? stored = null, failure = null;
                try { stored = Path.GetFileName(s.Upload.Complete()); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { failure = ex.Message; }
                // Release (and, on failure, delete) the partial file BEFORE telling the viewer, so "rejected" always means "nothing left behind".
                s.Upload.Dispose(); s.Upload = null;
                if (failure is null)
                {
                    log($"Received file '{stored}' from {DeviceIds.Short(s.ViewerId)}.");
                    await AckAsync(s, id, true, stored!, ct).ConfigureAwait(false);
                }
                else await AckAsync(s, id, false, failure, ct).ConfigureAwait(false);
                break;
        }
    }

    private void StartDownload(Session s, byte[] message, CancellationToken ct)
    {
        if (s.Download is { IsCompleted: false }) { _ = ErrorAsync(s, "A download is already running.", ct); return; }
        s.Download = Task.Run(async () =>
        {
            uint id = 0;
            try
            {
                if (!HostMessages.TryReadId(message, out id, out var rest) || rest.Length < 2) throw new InvalidDataException("Bad request.");
                var nameLength = BinaryPrimitives.ReadUInt16BigEndian(rest);
                if (rest.Length != 2 + nameLength) throw new InvalidDataException("Bad request.");
                var file = ShareDirectory.Resolve(services.ShareDirectory!, Encoding.UTF8.GetString(rest.Slice(2, nameLength)))
                           ?? throw new FileNotFoundException("That file is not shared.");
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = await SHA256.HashDataAsync(stream, s.Cancel.Token).ConfigureAwait(false);
                stream.Position = 0;
                var header = new byte[8 + 32];
                BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)stream.Length);
                hash.CopyTo(header, 8);
                await s.Secure.SendAsync(HostMessages.WithId(HostMessages.ShareFileStart, id, header), s.Cancel.Token).ConfigureAwait(false);
                var buffer = new byte[HostMessages.FileChunkBytes];
                int read;
                while ((read = await stream.ReadAsync(buffer, s.Cancel.Token).ConfigureAwait(false)) > 0)
                    await s.Secure.SendAsync(HostMessages.WithId(HostMessages.ShareFileChunk, id, buffer.AsSpan(0, read)), s.Cancel.Token).ConfigureAwait(false);
                await s.Secure.SendAsync(HostMessages.WithId(HostMessages.ShareFileEnd, id, []), s.Cancel.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                await AckAsync(s, id, false, ex.Message, s.Cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }
}
