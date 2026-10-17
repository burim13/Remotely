using System.Runtime.InteropServices;

namespace Remotely.UITests;

/// <summary>
/// Changes the machine's display state for a test variant: scaling, resolution, high contrast. Every method returns a plain-language result and never throws, because a runner may
/// refuse (no real monitor, no permission); the caller then marks the variant as skipped with that reason instead of pretending it ran.
/// </summary>
internal static class Display
{
    private static readonly int[] ScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public static string Describe()
    {
        var w = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        var h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        var vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        var hc = new Native.HIGHCONTRAST { Size = (uint)Marshal.SizeOf<Native.HIGHCONTRAST>() };
        Native.SystemParametersInfo(0x42, hc.Size, ref hc, 0);
        return $"primary {w}x{h}, virtual {vw}x{vh}, primary monitor scaling {CurrentScalePercent()}%, high contrast {((hc.Flags & 1) != 0 ? "on" : "off")}, OS {Environment.OSVersion.Version}";
    }

    /// <summary>The scaling of the primary display in percent, as Windows reports it for this (per-monitor aware) process; 0 when unknown.</summary>
    public static int CurrentScalePercent()
    {
        var monitor = Native.MonitorFromPoint(0, 0, 1);
        return Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? (int)(dpi * 100 / 96) : 0;
    }

    public static (bool Ok, string Message) SetScale(int percent)
    {
        try
        {
            var target = Array.IndexOf(ScaleSteps, percent);
            if (target < 0) return (false, $"{percent}% is not one of the Windows scaling steps");
            if (Native.GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount) != 0) return (false, "GetDisplayConfigBufferSizes failed");
            var paths = new byte[pathCount * 72];
            var modes = new byte[modeCount * 64];
            var query = Native.QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (query != 0) return (false, $"QueryDisplayConfig returned {query}");
            if (pathCount == 0) return (false, "no active display path");

            // DISPLAYCONFIG_PATH_INFO starts with the source: adapter LUID (low, high) and source id.
            var low = BitConverter.ToUInt32(paths, 0);
            var high = BitConverter.ToInt32(paths, 4);
            var id = BitConverter.ToUInt32(paths, 8);

            var get = new Native.DpiGet { Type = unchecked((uint)-3), Size = (uint)Marshal.SizeOf<Native.DpiGet>(), AdapterLow = low, AdapterHigh = high, Id = id };
            var got = Native.DisplayConfigGetDeviceInfo(ref get);
            if (got != 0) return (false, $"DisplayConfigGetDeviceInfo(get DPI scale) returned {got}");
            var recommended = -get.MinRel;
            var relative = target - recommended;
            if (relative < get.MinRel || relative > get.MaxRel)
                return (false, $"this display allows {ScaleSteps[recommended + get.MinRel]}% to {ScaleSteps[recommended + get.MaxRel]}% (recommended {ScaleSteps[recommended]}%), not {percent}%");
            if (relative == get.CurRel) return (true, $"already at {percent}%");

            var set = new Native.DpiSet { Type = unchecked((uint)-4), Size = (uint)Marshal.SizeOf<Native.DpiSet>(), AdapterLow = low, AdapterHigh = high, Id = id, ScaleRel = relative };
            var done = Native.DisplayConfigSetDeviceInfo(ref set);
            if (done != 0) return (false, $"DisplayConfigSetDeviceInfo(set DPI scale) returned {done}");
            Thread.Sleep(2500);
            var now = CurrentScalePercent();
            return (true, $"set {percent}% (recommended was {ScaleSteps[recommended]}%); the system DPI now reads {now}%");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or ArgumentException or OverflowException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Switches to the largest mode that fits inside the given size (runners often start at 1024x768).</summary>
    public static (bool Ok, string Message) SetLargestResolution(int maxWidth, int maxHeight)
    {
        try
        {
            var mode = new Native.DEVMODE { DeviceName = "", FormName = "", Size = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
            if (!Native.EnumDisplaySettings(null, -1, ref mode)) return (false, "EnumDisplaySettings(current) failed");
            var current = $"{mode.PelsWidth}x{mode.PelsHeight}";
            Native.DEVMODE best = mode;
            var seen = new SortedSet<string>();
            for (var i = 0; ; i++)
            {
                var candidate = new Native.DEVMODE { DeviceName = "", FormName = "", Size = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
                if (!Native.EnumDisplaySettings(null, i, ref candidate)) break;
                if (candidate.BitsPerPel < 32) continue;
                seen.Add($"{candidate.PelsWidth}x{candidate.PelsHeight}");
                if (candidate.PelsWidth <= maxWidth && candidate.PelsHeight <= maxHeight
                    && (long)candidate.PelsWidth * candidate.PelsHeight > (long)best.PelsWidth * best.PelsHeight)
                    best = candidate;
            }
            var modes = "modes offered: " + string.Join(" ", seen.Take(40));
            if (best.PelsWidth == mode.PelsWidth && best.PelsHeight == mode.PelsHeight) return (true, $"kept {current}; {modes}");
            best.Fields = 0x80000 | 0x100000;
            var result = Native.ChangeDisplaySettings(ref best, 0);
            return result == 0 ? (true, $"changed {current} to {best.PelsWidth}x{best.PelsHeight}; {modes}") : (false, $"ChangeDisplaySettings returned {result}; {modes}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static (bool Ok, string Message) SetHighContrast(bool on)
    {
        var scheme = IntPtr.Zero;
        try
        {
            scheme = Marshal.StringToHGlobalUni("High Contrast Black");
            var hc = new Native.HIGHCONTRAST { Size = (uint)Marshal.SizeOf<Native.HIGHCONTRAST>(), Flags = on ? 1u : 0u, DefaultScheme = scheme };
            if (!Native.SystemParametersInfo(0x43, hc.Size, ref hc, 3)) return (false, "SystemParametersInfo(SPI_SETHIGHCONTRAST) failed, Win32 error " + Marshal.GetLastWin32Error());
            Thread.Sleep(2500);
            var read = new Native.HIGHCONTRAST { Size = hc.Size };
            Native.SystemParametersInfo(0x42, hc.Size, ref read, 0);
            var actual = (read.Flags & 1) != 0;
            return actual == on ? (true, "high contrast " + (on ? "on" : "off") + " (read back from Windows)") : (false, "Windows did not keep the high contrast setting (read back: " + (actual ? "on" : "off") + ")");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (scheme != IntPtr.Zero) Marshal.FreeHGlobal(scheme);
        }
    }
}
