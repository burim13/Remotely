using System.Diagnostics;
using System.Text;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Probe;

/// <summary>
/// "probe bench": measures the stream a host delivers to a viewer and prints one parseable line per number (prefix BENCH).
/// Phases: idle (nothing is changing on the host) and, with --activity on the host's own Windows desktop, a window that moves and changes colour.
/// What it can and cannot tell: frames per second, bytes per frame and the host's reported grab and encode time are exact for this run; a moving window
/// is only produced when the probe runs on the same Windows PC as the host, and was not exercised on CI when this was written.
/// </summary>
internal static class Bench
{
    private sealed class Tally
    {
        public int Frames, Keys, Partials, Tiles;
        public long Bytes, CaptureMicros, EncodeMicros;
        public int MinBytes = int.MaxValue, MaxBytes;
    }

    public static async Task<int> RunAsync(ViewerSession session, string[] args)
    {
        var seconds = TimeSpan.FromSeconds(double.Parse(Option(args, "--seconds") ?? "8", System.Globalization.CultureInfo.InvariantCulture));
        var legacy = args.Contains("--legacy") || !session.SupportsTiledFrames;
        var monitor = int.Parse(Option(args, "--monitor") ?? "-1");
        var fixedQuality = Option(args, "--quality");

        var current = new Tally();
        session.FrameReceived += frame =>
        {
            var t = current;
            lock (t)
            {
                var size = frame.Tiles?.Sum(x => x.Jpeg.Length) ?? frame.Jpeg.Length;
                t.Frames++;
                t.Bytes += size;
                t.MinBytes = Math.Min(t.MinBytes, size);
                t.MaxBytes = Math.Max(t.MaxBytes, size);
                t.CaptureMicros += frame.CaptureMicros;
                t.EncodeMicros += frame.EncodeMicros;
                if (frame.Tiles is null) t.Keys++; else { t.Partials++; t.Tiles += frame.Tiles.Count; }
            }
        };
        session.HostError += e => Console.WriteLine("BENCH host error: " + e);

        if (!legacy)
        {
            if (fixedQuality is not null) await session.SetFixedQualityAsync(int.Parse(fixedQuality));
            await session.EnableTiledFramesAsync();
            if (monitor >= 0) await session.SelectMonitorAsync(monitor);
        }
        Console.WriteLine($"BENCH mode={(legacy ? "full-frame JPEG (protocol 1 path)" : "tiles (protocol 2 path)")} host protocol={session.Hello.Version} monitors={session.Monitors.Count}");

        await Task.Delay(TimeSpan.FromSeconds(1.5)); // let the first full picture and the quality settle; it is not part of the numbers
        var idle = await MeasureAsync(session, current = new Tally(), seconds);
        Print("idle desktop", idle.Tally, idle.Elapsed, session, legacy);

        if (args.Contains("--activity") && OperatingSystem.IsWindows())
        {
            using var workload = StartMovingWindow();
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            var moving = await MeasureAsync(session, current = new Tally(), seconds);
            Print("moving window", moving.Tally, moving.Elapsed, session, legacy);
        }
        else if (args.Contains("--activity")) Console.WriteLine("BENCH moving window: skipped (needs Windows)");
        return 0;
    }

    private static async Task<(Tally Tally, TimeSpan Elapsed)> MeasureAsync(ViewerSession session, Tally tally, TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        await Task.Delay(duration);
        lock (tally) return (tally, clock.Elapsed);
    }

    private static void Print(string phase, Tally t, TimeSpan elapsed, ViewerSession session, bool legacy)
    {
        lock (t)
        {
            var frames = Math.Max(1, t.Frames);
            Console.WriteLine($"BENCH [{phase}] seconds={elapsed.TotalSeconds:0.0} frames={t.Frames} fps={t.Frames / elapsed.TotalSeconds:0.0} " +
                              $"bytes/frame avg={t.Bytes / frames} min={(t.Frames == 0 ? 0 : t.MinBytes)} max={t.MaxBytes} full={t.Keys} partial={t.Partials} tiles/partial={(t.Partials == 0 ? 0 : (double)t.Tiles / t.Partials):0.0}");
            if (legacy) Console.WriteLine($"BENCH [{phase}] host grab/encode time: not reported by protocol 1 hosts");
            else
            {
                var s = session.Stats;
                Console.WriteLine($"BENCH [{phase}] host capture ms avg={t.CaptureMicros / 1000.0 / frames:0.00} encode ms avg={t.EncodeMicros / 1000.0 / frames:0.00} " +
                                  $"round trip ms={s.RoundTripMs:0.0} bandwidth KB/s={s.BandwidthKBytesPerSecond:0} quality={s.Quality}");
            }
        }
    }

    /// <summary>A red/blue window that moves in a circle, redrawn every 16 ms, in a separate PowerShell process. Disposing closes it.</summary>
    private static IDisposable StartMovingWindow()
    {
        const string script = """
            Add-Type -AssemblyName System.Windows.Forms
            $f = New-Object System.Windows.Forms.Form
            $f.Text = 'remotely-bench'; $f.Width = 480; $f.Height = 320; $f.StartPosition = 'Manual'; $f.TopMost = $true; $f.BackColor = 'Red'
            $t = New-Object System.Windows.Forms.Timer; $t.Interval = 16; $script:i = 0
            $t.Add_Tick({ $script:i++; $f.Left = 100 + [int](200 * [math]::Sin($script:i / 20)); $f.Top = 60 + [int](100 * [math]::Cos($script:i / 20)); $f.BackColor = if ($script:i % 2) { 'Red' } else { 'Blue' } })
            $t.Start(); [void]$f.ShowDialog()
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process? process = null;
        try { process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -EncodedCommand {encoded}") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.WriteLine("BENCH could not start the moving window: " + ex.Message); }
        return new Workload(process);
    }

    private sealed class Workload(Process? process) : IDisposable
    {
        public void Dispose()
        {
            try { process?.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process?.Dispose();
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
