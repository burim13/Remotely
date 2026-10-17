using System.IO.Pipes;
using Remotely.Unattended;

namespace Remotely.Host.Unattended;

/// <summary>Who the pipe is open for right now: the user signed in at the console and the console's session. Both null when nobody is signed in.</summary>
public sealed record PeerContext(string? UserSid, uint? Session);

/// <summary>The check on a connected client. The Windows one reads the client's user and session; tests use a fake.</summary>
public interface IPeerCheck
{
    PeerVerdict Check(NamedPipeServerStream pipe, PeerContext expected);
}

/// <summary>
/// The service's end of the pipe. It keeps one listening instance whose access list names the signed-in user (made by <c>createPipe</c>, which on Windows applies
/// <see cref="PipeAccessPolicy.Sddl"/>), replaces it when the signed-in user changes, and for every client that connects: checks who it is (<see cref="IPeerCheck"/>) before reading a
/// single byte, then answers one request at a time until the client leaves, goes quiet, or the signed-in user changes. A refused client gets no reply.
/// </summary>
/// <param name="createPipe">Creates a listening instance for the given user SID (null: nobody signed in). The second argument is true only for the very first instance this process creates.</param>
public sealed class UnattendedServer(IUnattendedBackend backend, Func<PeerContext> context, Func<string?, bool, NamedPipeServerStream> createPipe, IPeerCheck check, Action<string> log)
{
    private static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private readonly object _tasksGate = new();
    private readonly List<Task> _served = [];

    /// <summary>A connection that sends nothing for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>How often the signed-in user is looked up while waiting for a client.</summary>
    public TimeSpan ContextPoll { get; init; } = TimeSpan.FromSeconds(2);

    public async Task RunAsync(CancellationToken ct)
    {
        var first = true;
        string? lastProblem = null;
        PeerContext? lastLogged = null;
        (NamedPipeServerStream Pipe, PeerContext Expected)? accepted = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var expected = context();
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = createPipe(expected.UserSid, first);
                    first = false;
                    lastProblem = null;
                    if (expected != lastLogged)
                    {
                        lastLogged = expected;
                        log(expected.UserSid is null ? "The unattended-access pipe is ready; nobody is signed in at the console, so nobody may use it."
                            : $"The unattended-access pipe is ready for {expected.UserSid} (session {expected.Session}).");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Most likely another program already owns the pipe name, or all instances are in use. Say so once per kind of problem, then keep trying.
                    if (lastProblem != ex.Message) log("Could not open the unattended-access pipe: " + ex.Message);
                    lastProblem = ex.Message;
                }
                // The next listening instance exists before the connection that was just accepted is served, so the name never disappears between two clients.
                if (accepted is { } done) { Track(ServeAsync(done.Pipe, done.Expected, ct)); accepted = null; }
                if (pipe is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    continue;
                }

                using var userChanged = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watcher = WatchAsync(expected, userChanged);
                try
                {
                    await pipe.WaitForConnectionAsync(userChanged.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe.Dispose();
                    await watcher.ConfigureAwait(false);
                    continue;   // the loop ends if the service is stopping, otherwise makes a new instance for the new user
                }
                catch (IOException)
                {
                    pipe.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }
                userChanged.Cancel();
                await watcher.ConfigureAwait(false);
                accepted = (pipe, expected);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (accepted is { } leftover) leftover.Pipe.Dispose();
            Task[] running;
            lock (_tasksGate) running = [.. _served];
            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
    }

    private void Track(Task task)
    {
        lock (_tasksGate)
        {
            _served.RemoveAll(t => t.IsCompleted);
            _served.Add(task);
        }
    }

    private async Task WatchAsync(PeerContext expected, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(ContextPoll, cts.Token).ConfigureAwait(false);
                if (context() != expected) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, PeerContext expected, CancellationToken ct)
    {
        using (pipe)
        {
            PeerVerdict verdict;
            try { verdict = check.Check(pipe, expected); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                log("Could not identify a program that connected to the unattended pipe: " + ex.Message);
                return;
            }
            if (verdict != PeerVerdict.Allowed)
            {
                log($"Refused a program that connected to the unattended pipe ({verdict}).");
                return;
            }

            var timeout = FirstRequestTimeout;
            while (!ct.IsCancellationRequested)
            {
                byte[]? bytes;
                try
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(timeout);
                    bytes = await PipeFraming.ReadAsync(pipe, idle.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException) { return; }
                if (bytes is null || context() != expected) return;   // client left, or somebody else is signed in now
                timeout = IdleTimeout;

                PipeReply reply;
                try
                {
                    var request = PipeJson.Decode<PipeRequest>(bytes);
                    reply = await backend.HandleAsync(request, ct).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    reply = new PipeReply { Ok = false, Error = PipeErrors.BadRequest };
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log("Unattended pipe request failed: " + ex.Message);
                    reply = new PipeReply { Ok = false, Error = PipeErrors.Internal };
                }

                try
                {
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    send.CancelAfter(ReplyTimeout);
                    await PipeFraming.WriteAsync(pipe, PipeJson.Encode(reply), send.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException) { return; }
            }
        }
    }
}
