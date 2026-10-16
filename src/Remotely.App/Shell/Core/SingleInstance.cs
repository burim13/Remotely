using System.IO.Pipes;
using System.Text.Json;

namespace Remotely.App.Shell;

/// <summary>
/// One running copy per signed-in session. The first launch owns a named mutex and listens on a named pipe (current user only); a later launch sends
/// its arguments over the pipe and exits. Handshake, so the new process can let the old one take the foreground: client sends [length][JSON array of
/// strings], the primary answers its process id, the client acknowledges with one byte, and only then does the primary run its handler.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;
    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    private SingleInstance(Mutex? mutex, string pipeName, bool isPrimary)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    /// <summary>Claims the name. <paramref name="name"/> should include the user and session so two signed-in users do not meet.</summary>
    public static SingleInstance Acquire(string name)
    {
        var mutex = new Mutex(true, "Local\\" + name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex, name, true);
        mutex.Dispose();
        return new SingleInstance(null, name, false);
    }

    /// <summary>Primary only: runs <paramref name="onArguments"/> for every later launch (on a thread-pool thread).</summary>
    public void Listen(Action<string[]> onArguments)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the first instance listens.");
        _listener = Task.Run(() => ListenLoopAsync(onArguments, _stop.Token));
    }

    private async Task ListenLoopAsync(Action<string[]> onArguments, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, timeout.Token);
                var length = BitConverter.ToInt32(header);
                if (length is <= 0 or > MaxMessageBytes) continue;
                var body = new byte[length];
                await pipe.ReadExactlyAsync(body, timeout.Token);
                var args = JsonSerializer.Deserialize<string[]>(body) ?? [];
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), timeout.Token);
                var ack = new byte[1];
                await pipe.ReadExactlyAsync(ack, timeout.Token);
                onArguments(args);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
            {
                // a bad or slow client must not stop the listener
            }
        }
    }

    /// <summary>
    /// Secondary: hands the arguments to the running instance. <paramref name="primaryKnown"/> receives its process id before the instance acts
    /// (use it for AllowSetForegroundWindow). Returns false when nobody answered within <paramref name="timeout"/>; retries while the first instance is still starting.
    /// </summary>
    public async Task<bool> ForwardAsync(string[] args, TimeSpan timeout, Action<int>? primaryKnown = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(args);
        if (body.Length > MaxMessageBytes) return false;
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(500, cts.Token);
                await pipe.WriteAsync(BitConverter.GetBytes(body.Length), cts.Token);
                await pipe.WriteAsync(body, cts.Token);
                var pid = new byte[4];
                await pipe.ReadExactlyAsync(pid, cts.Token);
                primaryKnown?.Invoke(BitConverter.ToInt32(pid));
                await pipe.WriteAsync(new byte[] { 1 }, cts.Token);
                await pipe.FlushAsync(cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                try { await Task.Delay(200, cts.Token); }
                catch (OperationCanceledException) { /* the overall timeout ends the loop */ }
            }
            catch (OperationCanceledException) { /* the overall timeout ends the loop */ }
        }
        return false;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { /* the listener ended with an exception we already swallow */ }
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* released from another thread: the process end releases it */ }
            _mutex.Dispose();
        }
        _stop.Dispose();
    }
}
