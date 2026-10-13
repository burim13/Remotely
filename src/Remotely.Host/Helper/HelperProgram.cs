using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

/// <summary>
/// The process the service starts inside the console session. It answers one request type on the pipe:
/// byte 1 = "send a frame". Reply = status(1: 0 ok, 1 error) length(u32 BE) payload (frame bytes or an error text).
/// </summary>
internal static class HelperProgram
{
    public static int Run(string pipeName, FileLog log)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2, so sizes are real pixels
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(5000);
            using var capturer = new DesktopCapturer();
            log.Write($"Helper started in session {System.Diagnostics.Process.GetCurrentProcess().SessionId}.");

            var request = new byte[1];
            while (pipe.Read(request, 0, 1) == 1)
            {
                if (request[0] != 1) continue;
                try
                {
                    var frame = capturer.CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Reply(pipe, 0, FrameCodec.Encode(frame));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    Reply(pipe, 1, Encoding.UTF8.GetBytes(ex.Message));
                }
            }
            log.Write("Service closed the pipe; helper exiting.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            log.Write("Helper failed: " + ex.Message);
            return 1;
        }
    }

    private static void Reply(Stream pipe, byte status, byte[] payload)
    {
        var message = new byte[5 + payload.Length];
        message[0] = status;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(message, 5);
        pipe.Write(message);
        pipe.Flush();
    }
}
