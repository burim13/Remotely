using Remotely.Core.Lan.Consent;
using Remotely.Unattended;

namespace Remotely.Host.Unattended;

/// <summary>
/// The service has no screen, so the question "Allow this PC?" is handed to the app of the signed-in user, which shows its question window and sends the answer back over the pipe.
/// The app is considered to be there only while it has asked for the status in the last few seconds; with nobody looking the question is refused at once instead of
/// waiting for a timeout nobody will answer. Only one question is open at a time, a second one is refused, and an answer must carry the id of the open question.
/// </summary>
public sealed class ConsentRelay(Func<DateTimeOffset>? clock = null, TimeSpan? promptTimeout = null)
{
    private static readonly TimeSpan AttendedWindow = TimeSpan.FromSeconds(6);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _timeout = promptTimeout ?? new ConsentOptions().PromptTimeout;
    private readonly object _gate = new();
    private Pending? _pending;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

    private sealed record Pending(ConsentQuestion Question, TaskCompletionSource<ConsentDecision> Answer);

    /// <summary>Call whenever the app asks for the status.</summary>
    public void NotePoll()
    {
        lock (_gate) _lastPoll = _clock();
    }

    public bool AppIsWatching
    {
        get { lock (_gate) return _clock() - _lastPoll <= AttendedWindow; }
    }

    public ConsentQuestion? Current
    {
        get { lock (_gate) return _pending?.Question; }
    }

    /// <summary>The prompt that <see cref="ConsentGate"/> calls. Never throws for a refusal; a cancelled wait (the gate's timeout) is reported by cancelling.</summary>
    public async Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending pending;
        lock (_gate)
        {
            if (_pending is not null || _clock() - _lastPoll > AttendedWindow) return ConsentDecision.Deny;
            var question = new ConsentQuestion
            {
                Id = Guid.NewGuid().ToString("N"),
                ControllerName = request.ControllerName,
                Address = request.Address,
                ExpiresUtc = _clock() + _timeout,
            };
            _pending = pending = new Pending(question, answer);
        }
        try
        {
            using var registration = ct.Register(() => answer.TrySetCanceled(ct));
            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
        }
    }

    /// <summary>Delivers the person's answer. False when there is no open question with that id (already answered, timed out, or a made-up id).</summary>
    public bool Answer(string? questionId, string? decision)
    {
        Pending? pending;
        lock (_gate)
        {
            pending = _pending;
            if (pending is null || questionId is null || !string.Equals(pending.Question.Id, questionId, StringComparison.Ordinal)) return false;
        }
        // Only an explicit "once" lets anyone in; "Always" is not offered through the service (see docs/unattended.md), and anything unknown is a refusal.
        return pending.Answer.TrySetResult(decision == PipeDecisions.AllowOnce ? ConsentDecision.AllowOnce : ConsentDecision.Deny);
    }
}
