using System.Runtime.InteropServices;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

/// <summary>Plain-text clipboard of the interactive window station. The clipboard belongs to the window station, not the desktop, so this works on the lock screen too.</summary>
internal sealed class ClipboardAccess : IClipboardSync
{
    public Task<(long Sequence, string? Text)> ReadAsync(CancellationToken ct) => Task.FromResult(ReadNow());

    public Task<long> WriteAsync(string text, CancellationToken ct) => Task.FromResult(WriteNow(text));

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
        var memory = IntPtr.Zero;
        try
        {
            if (!Native.EmptyClipboard()) throw DesktopThread.Fail("EmptyClipboard");
            var bytes = (text.Length + 1) * 2;
            memory = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes);
            if (memory == IntPtr.Zero) throw DesktopThread.Fail("GlobalAlloc");
            var locked = Native.GlobalLock(memory);
            if (locked == IntPtr.Zero) throw DesktopThread.Fail("GlobalLock");
            try
            {
                var chars = (text + "\0").ToCharArray();
                Marshal.Copy(chars, 0, locked, chars.Length);
            }
            finally { Native.GlobalUnlock(memory); }
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, memory) == IntPtr.Zero) throw DesktopThread.Fail("SetClipboardData");
            memory = IntPtr.Zero; // the system owns it now
        }
        finally
        {
            if (memory != IntPtr.Zero) Native.GlobalFree(memory);
            Native.CloseClipboard();
        }
        return Native.GetClipboardSequenceNumber();
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
