using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

internal static class ClipboardNative
{
    public const uint CF_DIB = 8, CF_HDROP = 15, CF_DIBV5 = 17;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr memory);
}

/// <summary>
/// Clipboard of the interactive window station. The clipboard belongs to the window station, not the desktop, so this works on the lock screen too.
/// Plain text is the original path; <see cref="IRichClipboardSync"/> adds HTML, images (PNG) and file lists.
/// </summary>
internal sealed class ClipboardAccess : IClipboardSync, IRichClipboardSync
{
    private static readonly uint HtmlFormat = ClipboardNative.RegisterClipboardFormatW("HTML Format");
    private static readonly uint PngFormat = ClipboardNative.RegisterClipboardFormatW("PNG");

    public Task<(long Sequence, string? Text)> ReadAsync(CancellationToken ct) => Task.FromResult(ReadNow());

    public Task<long> WriteAsync(string text, CancellationToken ct) => Task.FromResult(WriteNow(text));

    public Task<ClipReadResult> ReadBundleAsync(long knownSequence, ClipLimits limits, CancellationToken ct) =>
        Task.FromResult(ReadBundleNow(knownSequence, limits));

    public Task<long> WriteBundleAsync(ClipBundle bundle, CancellationToken ct) => Task.FromResult(WriteBundleNow(bundle));

    // ---- plain text (original path) ----

    private static (long, string?) ReadNow()
    {
        var sequence = (long)Native.GetClipboardSequenceNumber();
        if (!Open()) return (sequence, null);
        try
        {
            var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return (sequence, null);
            var locked = Native.GlobalLock(handle);
            if (locked == IntPtr.Zero) return (sequence, null);
            try { return (sequence, Marshal.PtrToStringUni(locked)); }
            finally { Native.GlobalUnlock(handle); }
        }
        finally { Native.CloseClipboard(); }
    }

    private static long WriteNow(string text)
    {
        if (!Open()) throw DesktopThread.Fail("OpenClipboard");
        try
        {
            if (!Native.EmptyClipboard()) throw DesktopThread.Fail("EmptyClipboard");
            SetText(text);
        }
        finally { Native.CloseClipboard(); }
        return Native.GetClipboardSequenceNumber();
    }

    private static void SetText(string text)
    {
        var chars = (text + "\0").ToCharArray();
        var memory = Alloc(chars.Length * 2, locked => Marshal.Copy(chars, 0, locked, chars.Length));
        SetData(Native.CF_UNICODETEXT, memory);
    }

    // ---- rich ----

    private sealed record Raw(string? Text, byte[]? Html, byte[]? Png, byte[]? Dib, List<string>? Files);

    private static ClipReadResult ReadBundleNow(long knownSequence, ClipLimits limits)
    {
        var sequence = (long)Native.GetClipboardSequenceNumber();
        if (sequence == knownSequence) return new ClipReadResult(sequence, null);
        if (!Open()) return new ClipReadResult(sequence, null, "The clipboard was busy.");
        Raw raw;
        try
        {
            var pngData = ReadBytesLocked(PngFormat, limits.MaxPngBytes);
            // A bitmap is only read (and converted) when no PNG is offered: asking for a DIB makes Windows synthesise one from other formats.
            var dibData = pngData is null ? ReadBytesLocked(ClipboardNative.CF_DIB, limits.MaxPngBytes * 2L) ?? ReadBytesLocked(ClipboardNative.CF_DIBV5, limits.MaxPngBytes * 2L) : null;
            raw = new Raw(ReadTextLocked(), ReadBytesLocked(HtmlFormat, limits.MaxHtmlBytes * 2L), pngData, dibData, ReadFileListLocked());
        }
        finally { Native.CloseClipboard(); }

        // Everything slow happens after the clipboard is closed again, so other programs are not blocked.
        var items = new List<ClipItem>();
        string? note = null;
        if (raw.Text is { Length: > 0 } text && Encoding.UTF8.GetByteCount(text) <= limits.MaxTextBytes)
            items.Add(new ClipItem(ClipItemKind.Text, Encoding.UTF8.GetBytes(text)));
        if (raw.Html is not null)
        {
            var fragment = Encoding.UTF8.GetBytes(ClipHtml.ExtractFragment(raw.Html));
            if (fragment.Length <= limits.MaxHtmlBytes) items.Add(new ClipItem(ClipItemKind.Html, fragment));
            else note = "The copied formatted text is too large to share.";
        }
        var png = raw.Png ?? (raw.Dib is null ? null : DibToPng(raw.Dib));
        if (png is not null)
        {
            if (png.Length <= limits.MaxPngBytes) items.Add(new ClipItem(ClipItemKind.Png, png));
            else note = "The copied image is too large to share.";
        }
        if (raw.Files is { Count: > 0 } paths)
        {
            var files = ReadFiles(paths, limits, out var skipped);
            if (files is not null) items.Add(new ClipItem(ClipItemKind.Files, ClipFiles.Encode(files)));
            if (skipped is not null) note = skipped;
        }
        return new ClipReadResult(sequence, items.Count == 0 ? null : new ClipBundle(items), note);
    }

    private static List<ClipFile>? ReadFiles(List<string> paths, ClipLimits limits, out string? note)
    {
        note = null;
        var files = new List<ClipFile>();
        long total = 0;
        var folders = 0;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path)) { folders++; continue; }
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                total += info.Length;
                if (files.Count + 1 > limits.MaxFileCount || total > limits.MaxFilesBytes)
                {
                    note = "The copied files are too large to share.";
                    return null;
                }
                files.Add(new ClipFile(info.Name, File.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { note = "Some copied files could not be read."; }
        }
        if (folders > 0) note ??= "Folders are not shared through the clipboard.";
        return files.Count == 0 ? null : files;
    }

    private static string? ReadTextLocked()
    {
        var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return null;
        var locked = Native.GlobalLock(handle);
        if (locked == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(locked); }
        finally { Native.GlobalUnlock(handle); }
    }

    private static byte[]? ReadBytesLocked(uint format, long maxBytes)
    {
        if (format == 0) return null;
        var handle = Native.GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (long)(ulong)ClipboardNative.GlobalSize(handle);
        if (size <= 0 || size > maxBytes) return null;
        var locked = Native.GlobalLock(handle);
        if (locked == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(locked, bytes, 0, (int)size);
            return bytes;
        }
        finally { Native.GlobalUnlock(handle); }
    }

    /// <summary>CF_HDROP: a DROPFILES header (offset of the list, point, non-client flag, wide flag) followed by the paths, each ended by NUL, the list ended by an extra NUL.</summary>
    private static List<string>? ReadFileListLocked()
    {
        var bytes = ReadBytesLocked(ClipboardNative.CF_HDROP, 4 * 1024 * 1024);
        if (bytes is null || bytes.Length < 20) return null;
        var offset = BitConverter.ToInt32(bytes, 0);
        var wide = BitConverter.ToInt32(bytes, 16) != 0;
        if (offset < 20 || offset > bytes.Length) return null;
        var text = (wide ? Encoding.Unicode : Encoding.Default).GetString(bytes, offset, bytes.Length - offset);
        var paths = text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        return paths.Count == 0 ? null : paths;
    }

    private static long WriteBundleNow(ClipBundle bundle)
    {
        // Prepare everything that is slow or can fail before touching the clipboard.
        var html = bundle.Get(ClipItemKind.Html) is { } h ? ClipHtml.BuildCfHtml(Encoding.UTF8.GetString(h.Data)) : null;
        var png = bundle.Get(ClipItemKind.Png)?.Data;
        var dib = png is null ? null : PngToDib(png);
        var drop = bundle.Get(ClipItemKind.Files) is { } f ? BuildDropFiles(f.Data) : null;
        var text = bundle.Text;

        if (!Open()) throw DesktopThread.Fail("OpenClipboard");
        try
        {
            if (!Native.EmptyClipboard()) throw DesktopThread.Fail("EmptyClipboard");
            if (text is not null) SetText(text);
            if (html is not null) SetBytes(HtmlFormat, html);
            if (png is not null) SetBytes(PngFormat, png);
            if (dib is not null) SetBytes(ClipboardNative.CF_DIB, dib);
            if (drop is not null) SetBytes(ClipboardNative.CF_HDROP, drop);
        }
        finally { Native.CloseClipboard(); }
        return Native.GetClipboardSequenceNumber();
    }

    private static byte[] BuildDropFiles(byte[] packed)
    {
        if (!ClipFiles.TryDecode(packed, ClipLimits.Default with { MaxFilesBytes = int.MaxValue }, out var files, out var error))
            throw new InvalidDataException(error ?? "Bad file list.");
        var folder = NewClipboardFolder();
        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.Name);
            File.WriteAllBytes(path, file.Content);
            paths.Add(path);
        }
        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0); // pFiles
        BitConverter.GetBytes(1).CopyTo(data, 16); // fWide
        list.CopyTo(data, 20);
        return data;
    }

    /// <summary>A fresh folder for files received through the clipboard. Older ones (a day and more) are deleted first. A SYSTEM helper uses ProgramData, which the signed-in user can read; its own temp folder they could not.</summary>
    private static string NewClipboardFolder()
    {
        var root = Path.Combine(WindowsIdentity.GetCurrent().IsSystem
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) : Path.GetTempPath(), "Remotely", "Clipboard");
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

    // ---- images ----

    private static byte[]? DibToPng(byte[] dib)
    {
        try
        {
            if (dib.Length < 40) return null;
            var headerSize = BitConverter.ToInt32(dib, 0);
            var bitCount = BitConverter.ToUInt16(dib, 14);
            var compression = BitConverter.ToUInt32(dib, 16);
            var colorsUsed = BitConverter.ToUInt32(dib, 32);
            var palette = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
            var masks = compression == 3 && headerSize == 40 ? 12 : 0; // BI_BITFIELDS stores three masks after a plain header
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(14 + headerSize + masks + (int)palette * 4).CopyTo(bmp, 10);
            dib.CopyTo(bmp, 14);
            using var source = new Bitmap(new MemoryStream(bmp));
            // Drawn onto an opaque bitmap: a screenshot's alpha channel is unused and would otherwise come out transparent.
            using var opaque = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(opaque)) g.DrawImage(source, 0, 0, source.Width, source.Height);
            using var output = new MemoryStream();
            opaque.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException) { return null; }
    }

    /// <summary>A 32-bit bottom-up DIB for programs that do not know the PNG format.</summary>
    private static byte[]? PngToDib(byte[] png)
    {
        try
        {
            using var image = new Bitmap(new MemoryStream(png));
            var width = image.Width;
            var height = image.Height;
            using var argb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(argb)) g.DrawImage(image, 0, 0, width, height);
            var bits = argb.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = width * 4;
                var dib = new byte[40 + stride * height];
                BitConverter.GetBytes(40).CopyTo(dib, 0);
                BitConverter.GetBytes(width).CopyTo(dib, 4);
                BitConverter.GetBytes(height).CopyTo(dib, 8); // positive height: rows are stored bottom to top
                BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
                BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
                BitConverter.GetBytes(stride * height).CopyTo(dib, 20);
                for (var y = 0; y < height; y++)
                    Marshal.Copy(bits.Scan0 + (height - 1 - y) * bits.Stride, dib, 40 + y * stride, stride);
                return dib;
            }
            finally { argb.UnlockBits(bits); }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException) { return null; }
    }

    // ---- global memory helpers ----

    private static void SetBytes(uint format, byte[] data) =>
        SetData(format, Alloc(data.Length, locked => Marshal.Copy(data, 0, locked, data.Length)));

    private static IntPtr Alloc(int bytes, Action<IntPtr> fill)
    {
        var memory = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)(uint)Math.Max(bytes, 1));
        if (memory == IntPtr.Zero) throw DesktopThread.Fail("GlobalAlloc");
        var locked = Native.GlobalLock(memory);
        if (locked == IntPtr.Zero) { Native.GlobalFree(memory); throw DesktopThread.Fail("GlobalLock"); }
        try { fill(locked); }
        finally { Native.GlobalUnlock(memory); }
        return memory;
    }

    private static void SetData(uint format, IntPtr memory)
    {
        if (Native.SetClipboardData(format, memory) != IntPtr.Zero) return; // the system owns the memory now
        var failure = DesktopThread.Fail("SetClipboardData");
        Native.GlobalFree(memory);
        throw failure;
    }

    /// <summary>Another program may hold the clipboard for a moment; retry briefly.</summary>
    private static bool Open()
    {
        for (var i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }
}
