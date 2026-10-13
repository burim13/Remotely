using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

/// <summary>
/// The process the service starts inside the console session. It serves two pipes (see <see cref="HelperRpc"/>): "main" for screen
/// capture and clipboard, "input" for mouse and keyboard. It exits when the main pipe closes.
/// </summary>
internal static class HelperProgram
{
    public static int Run(string pipeName, FileLog log)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2, so sizes are real pixels
        try
        {
            using var main = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            using var input = new NamedPipeClientStream(".", pipeName + "-input", PipeDirection.InOut, PipeOptions.None);
            main.Connect(5000);
            input.Connect(5000);
            using var desktop = new DesktopThread();
            var capturer = new DesktopCapturer(desktop);
            var injector = new InputInjector(desktop);
            var clipboard = new ClipboardAccess();
            log.Write($"Helper started in session {System.Diagnostics.Process.GetCurrentProcess().SessionId}.");

            var inputThread = new Thread(() => Serve(input, request => request.First == HelperRpc.Input
                ? InputAsync(injector, request.Payload) : throw new InvalidOperationException("Unknown input request.")))
            { IsBackground = true, Name = "Remotely input pipe" };
            inputThread.Start();

            Serve(main, request => request.First switch
            {
                HelperRpc.Capture => CaptureAsync(capturer),
                HelperRpc.ClipboardRead => ClipboardReadAsync(clipboard),
                HelperRpc.ClipboardWrite => ClipboardWriteAsync(clipboard, request.Payload),
                _ => throw new InvalidOperationException("Unknown request."),
            });
            log.Write("Service closed the pipe; helper exiting.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            log.Write("Helper failed: " + ex.Message);
            return 1;
        }
    }

    private static void Serve(Stream pipe, Func<(byte First, byte[] Payload), Task<byte[]>> handle)
    {
        try
        {
            while (true)
            {
                var request = HelperRpc.ReadBlocking(pipe);
                byte[] reply;
                byte status;
                try { reply = handle(request).GetAwaiter().GetResult(); status = 0; }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or FormatException)
                {
                    reply = Encoding.UTF8.GetBytes(ex.Message);
                    status = 1;
                }
                pipe.Write(HelperRpc.Frame(status, reply));
                pipe.Flush();
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException) { /* peer closed */ }
    }

    private static async Task<byte[]> CaptureAsync(DesktopCapturer capturer) =>
        FrameCodec.Encode(await capturer.CaptureAsync(CancellationToken.None).ConfigureAwait(false));

    private static async Task<byte[]> InputAsync(InputInjector injector, byte[] payload)
    {
        if (!InputCodec.TryDecode(payload, out var events)) throw new InvalidDataException("Bad input message.");
        await injector.InjectAsync(events, CancellationToken.None).ConfigureAwait(false);
        return [];
    }

    private static async Task<byte[]> ClipboardReadAsync(ClipboardAccess clipboard)
    {
        var (sequence, text) = await clipboard.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var body = text is null ? [] : Encoding.UTF8.GetBytes(text);
        var reply = new byte[9 + body.Length];
        BinaryPrimitives.WriteInt64BigEndian(reply, sequence);
        reply[8] = text is null ? (byte)0 : (byte)1;
        body.CopyTo(reply, 9);
        return reply;
    }

    private static async Task<byte[]> ClipboardWriteAsync(ClipboardAccess clipboard, byte[] payload)
    {
        var sequence = await clipboard.WriteAsync(Encoding.UTF8.GetString(payload), CancellationToken.None).ConfigureAwait(false);
        var reply = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(reply, sequence);
        return reply;
    }
}
