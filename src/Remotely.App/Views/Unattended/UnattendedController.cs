using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Remotely.Core.Lan.Consent;
using Remotely.Unattended;

namespace Remotely.App.Views.Unattended;

/// <summary>
/// The app's side of unattended access, with no screen of its own: looks at the service every second while a page needs it (or while the service is listening for a token, so a question
/// from the service is still answered when the page is closed), keeps the newest <see cref="View"/>, and sends the few commands the signed-in user may give. It never trusts the pipe
/// before checking that the program on the other end is the installed Remotely service (same process id as the service manager reports).
/// </summary>
internal sealed class UnattendedController
{
    public static UnattendedController Instance { get; } = new();

    private readonly object _gate = new();
    private int _retained;
    private bool _looping;
    private uint _servicePid;
    private UnattendedPipeClient? _client;
    private string? _askedQuestion;

    /// <summary>Self-tests only: answers the service's question without a window (returns a <see cref="PipeDecisions"/> value).</summary>
    public Func<ConsentQuestion, Task<string>>? QuestionOverride { get; set; }

    public UnattendedView View { get; private set; } = new(ServiceState.Unknown, null, null, null);

    /// <summary>Raised on a background thread whenever <see cref="View"/> was refreshed.</summary>
    public event Action? Changed;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    /// <summary>A page that shows the state calls this when it appears and <see cref="Release"/> when it goes away.</summary>
    public void Retain(DispatcherQueue ui)
    {
        App.Share.AttachUi(ui);   // the question window the service's questions are shown in
        lock (_gate) _retained++;
        StartLoop();
    }

    public void Release()
    {
        lock (_gate) _retained = Math.Max(0, _retained - 1);
    }

    /// <summary>For the app's start-up: looks once, and keeps looking only while the service is listening for a token.</summary>
    public void EnsureBackground(DispatcherQueue ui)
    {
        App.Share.AttachUi(ui);
        StartLoop();
    }

    private void StartLoop()
    {
        lock (_gate)
        {
            if (_looping) return;
            _looping = true;
        }
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            await RefreshAsync().ConfigureAwait(false);
            lock (_gate)
            {
                var keep = _retained > 0 || (View.Service == ServiceState.Running && View.Status?.Listening == true);
                if (!keep) { _looping = false; return; }
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync()
    {
        UnattendedView view;
        try
        {
            var service = ServiceControl.Query();
            var sas = ReadSas();
            if (service.State != ServiceState.Running)
            {
                DropClient();
                view = new(service.State, null, null, sas);
            }
            else
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var reply = await ClientFor(service.ProcessId).CallAsync(new PipeRequest { Cmd = PipeCommands.Status }, cts.Token).ConfigureAwait(false);
                    view = new(ServiceState.Running, null, reply.Status, sas);
                    if (reply.Status?.Question is { } question) AnswerOnce(question);
                }
                catch (UnattendedPipeException ex)
                {
                    view = new(ServiceState.Running, ex.Problem, null, sas);
                }
                catch (OperationCanceledException)
                {
                    view = new(ServiceState.Running, PipeProblem.Unreachable, null, sas);   // no answer within 8 seconds
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.LogCrash(ex);
            view = new(ServiceState.Unknown, null, null, null);
        }
        lock (_gate) View = view;
        Changed?.Invoke();
    }

    private static bool? ReadSas()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SasPolicy.KeyPath);
            return SasPolicy.ServicesMaySend(key?.GetValue(SasPolicy.ValueName) as int?);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private UnattendedPipeClient ClientFor(uint servicePid)
    {
        lock (_gate)
        {
            if (_client is null || _servicePid != servicePid)
            {
                _client?.Dispose();
                _servicePid = servicePid;
                _client = new UnattendedPipeClient(CreateClient, VerifyServer);
            }
            return _client;
        }
    }

    private void DropClient()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
            _servicePid = 0;
        }
    }

    private static NamedPipeClientStream CreateClient() => new(".", UnattendedPipe.Name, PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.Asynchronous,
        TokenImpersonationLevel.Identification, HandleInheritability.None);

    /// <summary>Anything can create a pipe with this name before the service does; only the program the service manager says is the service is believed.</summary>
    private string? VerifyServer(NamedPipeClientStream pipe)
    {
        uint expected;
        lock (_gate) expected = _servicePid;
        if (expected == 0 || !GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var actual)) return "Cannot tell who owns the pipe.";
        return actual == expected ? null : $"The pipe belongs to process {actual}, not to the Remotely service (process {expected}).";
    }

    // ---- commands the signed-in user may give ----

    /// <returns>null when it worked, otherwise a <see cref="PipeErrors"/> code, or "unreachable" when the service could not be reached at all.</returns>
    public Task<string?> StartListeningAsync() => CommandAsync(new PipeRequest { Cmd = PipeCommands.Start });
    public Task<string?> StopListeningAsync() => CommandAsync(new PipeRequest { Cmd = PipeCommands.Stop });
    public Task<string?> NewTokenAsync() => CommandAsync(new PipeRequest { Cmd = PipeCommands.NewToken });
    public Task<string?> EndSessionAsync() => CommandAsync(new PipeRequest { Cmd = PipeCommands.EndSession });

    private async Task<string?> CommandAsync(PipeRequest request)
    {
        try
        {
            uint pid;
            lock (_gate) pid = _servicePid;
            if (pid == 0) { await RefreshAsync().ConfigureAwait(false); lock (_gate) pid = _servicePid; }
            if (pid == 0) return "unreachable";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await ClientFor(pid).CallAsync(request, cts.Token).ConfigureAwait(false);
            if (reply.Status is { } status)
            {
                lock (_gate) View = View with { Status = status, Link = null };
                Changed?.Invoke();
            }
            return reply.Ok ? null : reply.Error ?? PipeErrors.Internal;
        }
        catch (UnattendedPipeException) { return "unreachable"; }
    }

    /// <summary>The service has no screen, so its question "Allow this PC?" is shown here, once, and the answer goes back. Silence or any error counts as no.</summary>
    private void AnswerOnce(ConsentQuestion question)
    {
        lock (_gate)
        {
            if (_askedQuestion == question.Id) return;
            _askedQuestion = question.Id;
        }
        _ = Task.Run(async () =>
        {
            var decision = PipeDecisions.Deny;
            try
            {
                if (QuestionOverride is { } over) decision = await over(question).ConfigureAwait(false);
                else if (App.Share.Ui is { } ui)
                {
                    var left = question.ExpiresUtc - DateTimeOffset.UtcNow;
                    using var cts = new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.FromSeconds(1));
                    var answer = await ui.AskAsync(new ConsentRequest(question.ControllerName, question.Address, null, false, DateTimeOffset.UtcNow), cts.Token).ConfigureAwait(false);
                    if (answer is ConsentDecision.AllowOnce or ConsentDecision.AlwaysAllow) decision = PipeDecisions.AllowOnce;   // "Always" is not offered here (no device key), so it is only ever "once"
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                App.LogCrash(ex);
            }
            await CommandAsync(new PipeRequest { Cmd = PipeCommands.Answer, QuestionId = question.Id, Decision = decision }).ConfigureAwait(false);
        });
    }
}
