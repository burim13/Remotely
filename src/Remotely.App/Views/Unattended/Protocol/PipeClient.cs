using System.IO.Pipes;

namespace Remotely.Unattended;

public enum PipeProblem
{
    /// <summary>Nothing answers on the pipe (the service is not running, or has not created it yet).</summary>
    Unreachable,
    /// <summary>Windows refused the connection: this process is not the signed-in user the service listens for.</summary>
    AccessDenied,
    /// <summary>Something answers, but it is not the Remotely service (see <see cref="UnattendedPipeClient"/>'s <c>verify</c> parameter).</summary>
    UntrustedServer,
    /// <summary>The other end sent something that is not a valid reply.</summary>
    BadReply,
}

public sealed class UnattendedPipeException(PipeProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public PipeProblem Problem { get; } = problem;
}

/// <summary>
/// The app's end of the pipe: one connection, kept open between calls, one request at a time. It sends nothing before <paramref name="verify"/> has accepted the
/// server, so a program that created the same pipe name first receives no question and sends back nothing the app acts on.
/// </summary>
/// <param name="create">Makes an unconnected client (the Windows-only constructor lives with the caller).</param>
/// <param name="verify">Returns a problem sentence when the connected server is not the Remotely service, null when it is. Null means no extra check (tests).</param>
public sealed class UnattendedPipeClient(Func<NamedPipeClientStream> create, Func<NamedPipeClientStream, string?>? verify = null) : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public async Task<PipeReply> CallAsync(PipeRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var fresh = _pipe is null;
                try
                {
                    var pipe = _pipe ??= await ConnectAsync(ct).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ReplyTimeout);
                    await PipeFraming.WriteAsync(pipe, PipeJson.Encode(request), timeout.Token).ConfigureAwait(false);
                    var bytes = await PipeFraming.ReadAsync(pipe, timeout.Token).ConfigureAwait(false) ?? throw new IOException("The service closed the pipe.");
                    return PipeJson.Decode<PipeReply>(bytes);
                }
                catch (InvalidDataException ex)
                {
                    Drop();
                    throw new UnattendedPipeException(PipeProblem.BadReply, ex.Message, ex);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    Drop();
                    // An idle connection can have been closed by the service (it drops idle ones, and ones from a user who has signed out): try once more on a new one.
                    if (fresh || attempt > 0) throw new UnattendedPipeException(PipeProblem.Unreachable, ex.Message, ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = create();
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            pipe.Dispose();
            throw new UnattendedPipeException(PipeProblem.Unreachable, "The Remotely service does not answer.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            pipe.Dispose();
            throw new UnattendedPipeException(PipeProblem.AccessDenied, "Windows refused the connection to the Remotely service.", ex);
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            pipe.Dispose();
            throw new UnattendedPipeException(PipeProblem.Unreachable, ex.Message, ex);
        }
        if (verify?.Invoke(pipe) is { } problem)
        {
            pipe.Dispose();
            throw new UnattendedPipeException(PipeProblem.UntrustedServer, problem);
        }
        return pipe;
    }

    private void Drop()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        Drop();
        _gate.Dispose();
    }
}
