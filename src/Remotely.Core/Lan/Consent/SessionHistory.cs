namespace Remotely.Core.Lan.Consent;

/// <summary>One row of the session history: who connected (or tried to), how they were let in, and for how long.</summary>
public sealed record SessionRecord(Guid Id, string ControllerName, string? DeviceId, string Address, string Via, string Outcome, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, string? EndReason);

/// <summary>
/// What happened on this PC, newest first: allowed sessions (with when they ended and why) and refused ones. Kept on this PC only, capped at
/// <see cref="MaxEntries"/>, and clearable. A session still open when the app was closed shows as ended for the reason <see cref="SessionEndReasons.AppClosed"/>.
/// </summary>
public sealed class SessionHistory
{
    public const int MaxEntries = 300;
    private sealed class FileShape { public int Version { get; set; } = 1; public List<SessionRecord> Sessions { get; set; } = []; }

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly List<SessionRecord> _items = [];

    public SessionHistory(string? path)
    {
        _path = path;
        var shape = JsonFile.TryRead<FileShape>(path);
        if (shape is null) return;
        foreach (var s in shape.Sessions.Take(MaxEntries))
            _items.Add(s.EndedUtc is null ? s with { EndedUtc = s.StartedUtc, EndReason = SessionEndReasons.AppClosed } : s);
    }

    public event Action? Changed;

    public IReadOnlyList<SessionRecord> All { get { lock (_gate) return [.. _items]; } }

    /// <summary>Records that a PC was let in. Returns the id to pass to <see cref="End"/>.</summary>
    public Guid Begin(string controller, string? deviceId, string address, string via, string outcome, DateTimeOffset nowUtc)
    {
        var record = new SessionRecord(Guid.NewGuid(), controller, deviceId, address, via, outcome, nowUtc, null, null);
        Insert(record);
        return record.Id;
    }

    /// <summary>Records a PC that was turned away (denied, or nobody answered in time).</summary>
    public void AddRefused(string controller, string? deviceId, string address, string via, string outcome, DateTimeOffset nowUtc) =>
        Insert(new SessionRecord(Guid.NewGuid(), controller, deviceId, address, via, outcome, nowUtc, nowUtc, null));

    public void End(Guid id, string reason, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var i = _items.FindIndex(r => r.Id == id);
            if (i < 0) return;
            _items[i] = _items[i] with { EndedUtc = nowUtc, EndReason = reason };
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) { _items.Clear(); Save(); }
        Changed?.Invoke();
    }

    private void Insert(SessionRecord record)
    {
        lock (_gate)
        {
            _items.Insert(0, record);
            if (_items.Count > MaxEntries) _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
            Save();
        }
        Changed?.Invoke();
    }

    private void Save() => JsonFile.TryWrite(_path, new FileShape { Sessions = [.. _items] });
}

/// <summary>Codes stored in the history (the screen turns them into sentences in the person's language).</summary>
public static class SessionEndReasons
{
    public const string StoppedByHost = "stopped-by-host", SharingStopped = "sharing-stopped", ControllerLeft = "controller-left", AppClosed = "app-closed";
}
