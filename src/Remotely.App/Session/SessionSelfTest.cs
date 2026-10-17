using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.HostCore;
using Remotely.ViewerCore;
using Remotely.ViewerCore.Mapping;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Remotely.App;

/// <summary>
/// <c>Remotely.exe --selftest-session result.txt</c>: opens a real <see cref="SessionWindow"/> against a host in this process (a fake screen that
/// returns a generated JPEG, a recording input sink, the real LAN host and host agent over loopback) and checks what a person would rely on:
/// a frame reached the screen, a pointer move reached the host (through the same path a mouse move takes, after the window's own mapping), the
/// transfers panel is in the window and laid out, and closing the window ends the session. Writes PASS or FAIL plus details to the file.
/// What it does not do: send real mouse events through WinUI's input system, or look at pixels on screen.
/// </summary>
internal static class SessionSelfTest
{
    private sealed class FakeScreen(byte[] jpeg, int width, int height) : IFrameSource
    {
        public Task<FrameInfo> CaptureAsync(CancellationToken ct) => Task.FromResult(new FrameInfo(width, height, "Default", jpeg));
    }

    private sealed class RecordingInput : IInputSink
    {
        private readonly List<InputEvent> _events = [];
        public Task InjectAsync(IReadOnlyList<InputEvent> events, CancellationToken ct)
        {
            lock (_events) _events.AddRange(events);
            return Task.CompletedTask;
        }
        public InputEvent[] Snapshot() { lock (_events) return [.. _events]; }
    }

    public static async Task<int> RunAsync(string resultPath)
    {
        var lines = new List<string>();
        void Log(string s) { lock (lines) lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {s}"); }
        void Require(bool ok, string what)
        {
            Log((ok ? "ok: " : "FAILED: ") + what);
            if (!ok) throw new InvalidOperationException(what);
        }
        var work = Path.Combine(Path.GetTempPath(), "remotely-selftest-session-" + Environment.ProcessId);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Directory.CreateDirectory(Path.Combine(work, "share"));
            Directory.CreateDirectory(Path.Combine(work, "received"));

            const int width = 320, height = 200;
            var input = new RecordingInput();
            var services = new HostServices(new FakeScreen(await MakeJpegAsync(width, height), width, height))
            {
                Input = input,
                IsTrusted = _ => true,
                CanControl = _ => true,
                ReceiveDirectory = Path.Combine(work, "received"),
                ShareDirectory = Path.Combine(work, "share"),
            };
            var agent = new HostAgent(DeviceIdentity.Generate(), services, Log);
            int discoveryPort;
            using (var probe = new UdpClient(0)) discoveryPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            await using var host = new LanHost(
                new LanHostOptions { TcpPort = 0, DiscoveryPort = discoveryPort, AllowLoopback = true, HostName = "selftest-host" },
                (channel, name, ct) => agent.ServeChannelAsync(channel, name, control: true, ct), Log);
            host.Start();
            await Task.Delay(100, cts.Token);

            var found = await LanConnector.ConnectAsync(host.CurrentToken.Display, "selftest-viewer", "127.0.0.1:" + host.TcpPort, cts.Token);
            var session = await ViewerSession.OpenAsync(found.Channel, "selftest-host", cts.Token);
            var sessionEnded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Closed += reason => sessionEnded.TrySetResult(reason);
            Log("Session open, can control: " + session.CanControl);

            var window = new SessionWindow(session, "selftest-host");
            var windowClosed = new TaskCompletionSource();
            window.Closed += (_, _) => windowClosed.TrySetResult();
            window.Activate();

            await WaitAsync(() => window.FramesShown > 0, TimeSpan.FromSeconds(30), cts.Token);
            Require(window.FramesShown > 0, $"a frame was painted ({window.FramesShown} so far)");
            Require(window.RemoteView.FrameWidth == width && window.RemoteView.FrameHeight == height, $"the picture is {window.RemoteView.FrameWidth}x{window.RemoteView.FrameHeight}");

            await WaitAsync(() => window.RemoteView.ActualWidth > 0 && window.RemoteView.ActualHeight > 0, TimeSpan.FromSeconds(10), cts.Token);
            Require(window.RemoteView.CanControl, "the window controls the other PC");
            window.RemoteView.MovePointer(new PointD(window.RemoteView.ActualWidth / 2, window.RemoteView.ActualHeight / 2));
            await WaitAsync(() => input.Snapshot().Any(e => e.Kind == InputKind.MouseMove), TimeSpan.FromSeconds(10), cts.Token);
            var move = input.Snapshot().FirstOrDefault(e => e.Kind == InputKind.MouseMove);
            Require(move.Kind == InputKind.MouseMove, "a pointer move reached the host");
            Require(Math.Abs(move.X - 32768) < 400 && Math.Abs(move.Y - 32768) < 400, $"the view centre arrived as the desktop centre (x={move.X}, y={move.Y})");

            window.SetTransfersVisible(true);
            await WaitAsync(() => window.TransfersView.ActualWidth > 0 && window.TransfersView.ActualHeight > 0, TimeSpan.FromSeconds(10), cts.Token);
            Require(window.TransfersVisible && window.TransfersView.ActualWidth > 0, $"the transfers panel is in the window ({window.TransfersView.ActualWidth:0}x{window.TransfersView.ActualHeight:0})");

            var stats = session.ReceivedStats.Snapshot();
            Require(stats.TotalFrames > 0 && stats.TotalBytes > 0, $"stats counted {stats.TotalFrames} frame(s), {stats.TotalBytes} bytes");

            window.Close();
            await windowClosed.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            Log("window closed");
            await sessionEnded.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            Require(sessionEnded.Task.IsCompletedSuccessfully, "closing the window ended the session");

            File.WriteAllLines(resultPath, ["PASS", .. Snapshot(lines)]);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            File.WriteAllLines(resultPath, ["FAIL", .. Snapshot(lines)]);
            return 1;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string[] Snapshot(List<string> lines)
    {
        lock (lines) return [.. lines];
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50, ct);
    }

    /// <summary>A real JPEG (a colour gradient), made with the same imaging API the window decodes with.</summary>
    private static async Task<byte[]> MakeJpegAsync(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 255 / width);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = 128;
                pixels[i + 3] = 255;
            }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
