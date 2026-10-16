namespace Remotely.ViewerCore.Mapping;

/// <summary>When to retry after a session dropped, and whether to retry on its own at all.</summary>
public static class ReconnectSchedule
{
    private static readonly int[] DelaysSeconds = [2, 4, 8, 15, 30, 30];

    public static int MaxAttempts => DelaysSeconds.Length;

    /// <summary>Seconds to wait before attempt number <paramref name="attemptIndex"/> (0 based); null once the attempts are used up.</summary>
    public static int? DelayBefore(int attemptIndex) => attemptIndex >= 0 && attemptIndex < DelaysSeconds.Length ? DelaysSeconds[attemptIndex] : null;

    /// <summary>
    /// A session that ended with an error reason (link lost, host unresponsive, tampering) is retried automatically. One that ended without a
    /// reason was closed by the other PC, which may mean its owner stopped sharing on purpose, so it is only retried when the user asks.
    /// </summary>
    public static bool ShouldAutoRetry(string? closeReason) => !string.IsNullOrEmpty(closeReason);

    public static int SecondsLeft(DateTime dueUtc, DateTime nowUtc) => (int)Math.Max(0, Math.Ceiling((dueUtc - nowUtc).TotalSeconds));
}
