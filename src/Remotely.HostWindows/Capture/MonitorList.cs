using Remotely.HostCore;

namespace Remotely.Host.Capture;

/// <summary>The displays attached to the current desktop, primary first, then left to right and top to bottom. Index is the position in that order.</summary>
internal static class MonitorList
{
    internal sealed record Entry(MonitorInfo Info, IntPtr Handle, string Device);

    public static IReadOnlyList<Entry> Enumerate()
    {
        var found = new List<(IntPtr Handle, MONITORINFOEXW Info)>();
        CaptureNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr dc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEXW { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>() };
            if (CaptureNative.GetMonitorInfoW(handle, ref info)) found.Add((handle, info));
            return true;
        }, IntPtr.Zero);

        var ordered = found
            .OrderByDescending(m => (m.Info.dwFlags & CaptureNative.MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.Info.rcMonitor.Left).ThenBy(m => m.Info.rcMonitor.Top)
            .ToList();
        var list = new List<Entry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (handle, m) = ordered[i];
            var r = m.rcMonitor;
            list.Add(new Entry(new MonitorInfo(i, m.szDevice, r.Left, r.Top, r.Width, r.Height, (m.dwFlags & CaptureNative.MONITORINFOF_PRIMARY) != 0), handle, m.szDevice));
        }
        return list;
    }

    /// <summary>Bounding box of all displays, which is also the area the old whole-desktop capture and the mouse mapping use.</summary>
    public static (int X, int Y, int Width, int Height) Bounds(IReadOnlyList<Entry> monitors)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in monitors)
        {
            left = Math.Min(left, m.Info.X);
            top = Math.Min(top, m.Info.Y);
            right = Math.Max(right, m.Info.X + m.Info.Width);
            bottom = Math.Max(bottom, m.Info.Y + m.Info.Height);
        }
        return (left, top, right - left, bottom - top);
    }
}
