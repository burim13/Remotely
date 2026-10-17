using System.Runtime.InteropServices;
using System.Text;
using Remotely.HostCore;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Remotely.App.Session.Input;

/// <summary>
/// Moves the local WinUI clipboard to and from a <see cref="ClipBundle"/> (text, HTML, PNG image, files). Every method must run on the UI thread, as the
/// clipboard API requires.
/// <para>Loop prevention: after <see cref="ApplyAsync"/> the sequence number of the clipboard is remembered; <see cref="IsOwnChange"/> tells the
/// <c>Clipboard.ContentChanged</c> handler that the change was our own write. This works even though an image read back is re-encoded and so is not
/// byte-identical to what was received (the session's content fingerprint guard cannot see that case).</para>
/// </summary>
public static class ClipboardBridge
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    private static long _ownSequence = -1;

    /// <summary>True when the clipboard has not changed since this program last wrote it.</summary>
    public static bool IsOwnChange() => GetClipboardSequenceNumber() == Interlocked.Read(ref _ownSequence);

    /// <summary>Reads what the local clipboard holds. Null when there is nothing we carry, or it breaks a size limit (the person is not told; nothing is sent).</summary>
    public static async Task<ClipBundle?> ReadAsync(ClipLimits limits)
    {
        var view = Clipboard.GetContent();
        var items = new List<ClipItem>();
        if (view.Contains(StandardDataFormats.Text) && await view.GetTextAsync() is { Length: > 0 } text && Encoding.UTF8.GetByteCount(text) <= limits.MaxTextBytes)
            items.Add(new ClipItem(ClipItemKind.Text, Encoding.UTF8.GetBytes(text)));
        if (view.Contains(StandardDataFormats.Html))
        {
            var fragment = Encoding.UTF8.GetBytes(HtmlFormatHelper.GetStaticFragment(await view.GetHtmlFormatAsync()));
            if (fragment.Length <= limits.MaxHtmlBytes) items.Add(new ClipItem(ClipItemKind.Html, fragment));
        }
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            var png = await ToPngAsync(stream);
            if (png.Length <= limits.MaxPngBytes) items.Add(new ClipItem(ClipItemKind.Png, png));
        }
        if (view.Contains(StandardDataFormats.StorageItems) && ReadFiles(await view.GetStorageItemsAsync(), limits) is { } files)
            items.Add(new ClipItem(ClipItemKind.Files, ClipFiles.Encode(files)));
        return items.Count == 0 ? null : new ClipBundle(items);
    }

    private static List<ClipFile>? ReadFiles(IReadOnlyList<IStorageItem> storage, ClipLimits limits)
    {
        var files = new List<ClipFile>();
        long total = 0;
        foreach (var item in storage)
        {
            if (!item.IsOfType(StorageItemTypes.File) || string.IsNullOrEmpty(item.Path)) continue; // folders are not shared
            var info = new FileInfo(item.Path);
            total += info.Length;
            if (files.Count + 1 > limits.MaxFileCount || total > limits.MaxFilesBytes) return null;
            try { files.Add(new ClipFile(info.Name, File.ReadAllBytes(item.Path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files.Count == 0 ? null : files;
    }

    /// <summary>Puts a bundle received from the other PC on the local clipboard.</summary>
    public static async Task ApplyAsync(ClipBundle bundle)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (bundle.Text is { } text) package.SetText(text);
        if (bundle.Get(ClipItemKind.Html) is { } html) package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(Encoding.UTF8.GetString(html.Data)));
        if (bundle.Get(ClipItemKind.Png) is { } png)
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png.Data);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        }
        if (bundle.Get(ClipItemKind.Files) is { } packed && ClipFiles.TryDecode(packed.Data, ClipLimits.Default with { MaxFilesBytes = int.MaxValue }, out var files, out _))
        {
            var folder = NewFolder();
            var storage = new List<IStorageItem>();
            foreach (var file in files)
            {
                var path = Path.Combine(folder, file.Name);
                await File.WriteAllBytesAsync(path, file.Content);
                storage.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            package.SetStorageItems(storage);
        }
        Clipboard.SetContent(package);
        Interlocked.Exchange(ref _ownSequence, GetClipboardSequenceNumber());
    }

    private static async Task<byte[]> ToPngAsync(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>A fresh folder in the user's temp for files received through the clipboard; folders older than a day are removed first.</summary>
    private static string NewFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remotely", "Clipboard");
        Directory.CreateDirectory(root);
        foreach (var old in Directory.GetDirectories(root))
        {
            try { if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(old, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
