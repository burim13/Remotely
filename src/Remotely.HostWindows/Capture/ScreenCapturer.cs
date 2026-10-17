using System.Runtime.InteropServices;
using Remotely.Host.Helper;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Capture;

/// <summary>One grab: the picture, the displays, the pointer, and which API produced it.</summary>
internal sealed record Shot(RawImage Image, IReadOnlyList<MonitorInfo> Monitors, string Path);

/// <summary>
/// Chooses between Desktop Duplication and GDI for each grab and logs which one ran. DXGI is used for a single display; the whole virtual
/// desktop of several displays and any DXGI failure use GDI. After a failure DXGI is tried again after a pause, because the usual causes
/// (desktop switch to the lock screen, display mode change) pass. Set the environment variable REMOTELY_CAPTURE=gdi to turn DXGI off.
/// </summary>
internal sealed class ScreenCapturer(Action<string>? log) : IDisposable
{
    private static readonly TimeSpan RetryDxgiAfter = TimeSpan.FromSeconds(15);
    private readonly bool _dxgiAllowed = !string.Equals(Environment.GetEnvironmentVariable("REMOTELY_CAPTURE"), "gdi", StringComparison.OrdinalIgnoreCase);
    private readonly DxgiGrabber _dxgi = new();
    private byte[]? _gdi;
    private long _version;
    private DateTime _dxgiRetryAt = DateTime.MinValue;
    private string? _lastPath;
    private string? _lastDxgiFailure;

    public string? LastPath => _lastPath;

    private long NextVersion() => ++_version;

    /// <summary>The displays of the current input desktop. If Windows lists none, one display with the virtual-screen size is reported so there is always something to capture.</summary>
    public IReadOnlyList<MonitorList.Entry> Monitors()
    {
        var list = MonitorList.Enumerate();
        if (list.Count > 0) return list;
        var info = new MonitorInfo(0, "DISPLAY", Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN), true);
        return [new MonitorList.Entry(info, IntPtr.Zero, "")];
    }

    /// <summary>Must run on the desktop thread. <paramref name="monitor"/> is a display index, or -1 for the whole virtual desktop.</summary>
    public Shot Grab(string desktopName, int monitor, bool allowDxgi)
    {
        var entries = Monitors();
        var infos = entries.Select(e => e.Info).ToList();
        var selected = monitor >= 0 && monitor < entries.Count ? entries[monitor] : null;
        var area = selected is not null
            ? (selected.Info.X, selected.Info.Y, selected.Info.Width, selected.Info.Height)
            : (Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
               Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
        if (area.Item3 <= 0 || area.Item4 <= 0) throw new InvalidOperationException($"Desktop '{desktopName}' reports no screen size.");

        // DXGI duplicates one display. With a single display the whole desktop is that display.
        var dxgiTarget = selected ?? (entries.Count == 1 && entries[0].Device.Length > 0 ? entries[0] : null);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (allowDxgi && _dxgiAllowed && dxgiTarget is not null && DateTime.UtcNow >= _dxgiRetryAt)
        {
            try
            {
                var (image, version) = _dxgi.Grab(desktopName, dxgiTarget, NextVersion);
                Note("DXGI", $"Desktop Duplication on {_dxgi.LastAdapter}");
                _lastDxgiFailure = null;
                return new Shot(new RawImage(area.Item3, area.Item4, desktopName, selected is null ? -1 : selected.Info.Index, image, version, Micros(started)), infos, "DXGI");
            }
            catch (DxgiGrabber.Unavailable ex)
            {
                _dxgiRetryAt = DateTime.UtcNow + RetryDxgiAfter;
                if (ex.Message != _lastDxgiFailure) log?.Invoke($"DXGI capture unavailable, using GDI (retrying in {RetryDxgiAfter.TotalSeconds:0} s): {ex.Message}");
                _lastDxgiFailure = ex.Message;
            }
        }

        var pixels = GdiGrab(area.Item1, area.Item2, area.Item3, area.Item4);
        Note(allowDxgi ? "GDI" : "GDI/full-frame", !allowDxgi ? "GDI BitBlt, full-frame JPEG for viewers without tiled frames" : _dxgiAllowed ? (dxgiTarget is null ? "GDI BitBlt (several displays as one picture)" : "GDI BitBlt (DXGI unavailable)") : "GDI BitBlt (DXGI turned off by REMOTELY_CAPTURE=gdi)");
        return new Shot(new RawImage(area.Item3, area.Item4, desktopName, selected is null ? -1 : selected.Info.Index, pixels, NextVersion(), Micros(started)), infos, "GDI");
    }

    private void Note(string path, string detail)
    {
        if (path == _lastPath) return;
        _lastPath = path;
        log?.Invoke($"Capture path: {path} ({detail}).");
    }

    private static int Micros(long started) => (int)Math.Min(int.MaxValue, System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks / 10);

    private byte[] GdiGrab(int left, int top, int width, int height)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw DesktopThread.Fail("GetDC");
        var memory = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = Native.CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width, biHeight = -height, // negative: top-down rows
                biPlanes = 1, biBitCount = 32,
            };
            dib = Native.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw DesktopThread.Fail("CreateDIBSection");
            previous = Native.SelectObject(memory, dib);
            if (!Native.BitBlt(memory, 0, 0, width, height, screen, left, top, Native.SRCCOPY | Native.CAPTUREBLT)) throw DesktopThread.Fail("BitBlt");
            Native.GdiFlush();
            var size = checked(width * height * 4);
            if (_gdi is null || _gdi.Length != size) _gdi = new byte[size];
            Marshal.Copy(bits, _gdi, 0, size);
            return _gdi;
        }
        finally
        {
            if (previous != IntPtr.Zero) Native.SelectObject(memory, previous);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (memory != IntPtr.Zero) Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose() => _dxgi.Dispose();
}
