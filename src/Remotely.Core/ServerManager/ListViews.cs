using System.Globalization;
using System.Text;
using Remotely.Core.Admin;

namespace Remotely.Core.ServerManager;

public enum DeviceStateFilter { All, Online, Offline, Blocked }

public enum LastSeenKind { Online, Never, JustNow, Minutes, Hours, Days, Date }

/// <summary>How to say when a device was last seen. The page turns the kind and count into a localised sentence.</summary>
public readonly record struct LastSeenInfo(LastSeenKind Kind, int Count, DateTimeOffset? Date);

public static class DeviceView
{
    public static LastSeenInfo DescribeLastSeen(AdminDevice device, DateTimeOffset now)
    {
        if (device.Online) return new LastSeenInfo(LastSeenKind.Online, 0, null);
        if (device.LastSeenUtc is not { } seen) return new LastSeenInfo(LastSeenKind.Never, 0, null);
        var age = now - seen;
        if (age < TimeSpan.FromMinutes(1)) return new LastSeenInfo(LastSeenKind.JustNow, 0, seen);
        if (age < TimeSpan.FromHours(1)) return new LastSeenInfo(LastSeenKind.Minutes, (int)age.TotalMinutes, seen);
        if (age < TimeSpan.FromDays(1)) return new LastSeenInfo(LastSeenKind.Hours, (int)age.TotalHours, seen);
        if (age < TimeSpan.FromDays(30)) return new LastSeenInfo(LastSeenKind.Days, (int)age.TotalDays, seen);
        return new LastSeenInfo(LastSeenKind.Date, 0, seen);
    }

    /// <summary>Search by name or device id (any case, any part), narrowed by state. Online devices first, then by name.</summary>
    public static IReadOnlyList<AdminDevice> Filter(IEnumerable<AdminDevice> devices, string? query, DeviceStateFilter state = DeviceStateFilter.All)
    {
        var q = (query ?? "").Trim();
        return devices
            .Where(d => q.Length == 0 || d.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || d.DeviceId.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(d => state switch
            {
                DeviceStateFilter.Online => d.Online && !d.Blocked,
                DeviceStateFilter.Offline => !d.Online && !d.Blocked,
                DeviceStateFilter.Blocked => d.Blocked,
                _ => true,
            })
            .OrderByDescending(d => d.Online)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The first eight characters of the id, which is how the lists show it.</summary>
    public static string ShortId(string deviceId) => deviceId.Length <= 8 ? deviceId : deviceId[..8];
}

/// <summary>What the Activity page narrows the audit log by. Empty fields match everything.</summary>
public sealed record AuditFilter(string? Text = null, string? Kind = null, string? Result = null, string? DeviceId = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null);

public static class AuditView
{
    /// <summary>Newest first. <paramref name="names"/> maps device ids to names so that a search by name also finds the entry.</summary>
    public static IReadOnlyList<AuditEntry> Apply(IEnumerable<AuditEntry> entries, AuditFilter filter, IReadOnlyDictionary<string, string>? names = null)
    {
        var text = (filter.Text ?? "").Trim();
        string NameOf(string? id) => id is not null && names is not null && names.TryGetValue(id, out var n) ? n : "";
        bool Has(string? haystack) => haystack is not null && haystack.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        return entries
            .Where(e => filter.Kind is not { Length: > 0 } k || string.Equals(e.Kind, k, StringComparison.OrdinalIgnoreCase))
            .Where(e => filter.Result is not { Length: > 0 } r || string.Equals(e.Result, r, StringComparison.OrdinalIgnoreCase))
            .Where(e => filter.DeviceId is not { Length: > 0 } d || e.DeviceId == d || e.PeerDeviceId == d)
            .Where(e => filter.FromUtc is not { } from || e.TimeUtc >= from)
            .Where(e => filter.ToUtc is not { } to || e.TimeUtc <= to)
            .Where(e => text.Length == 0 || Has(e.Kind) || Has(e.Result) || Has(e.Detail) || Has(e.DeviceId) || Has(e.PeerDeviceId) || Has(NameOf(e.DeviceId)) || Has(NameOf(e.PeerDeviceId)))
            .OrderByDescending(e => e.TimeUtc)
            .ToList();
    }

    /// <summary>The values that occur in the log, for the filter drop-downs. The server decides the vocabulary; nothing is assumed here.</summary>
    public static IReadOnlyList<string> DistinctKinds(IEnumerable<AuditEntry> entries) =>
        entries.Select(e => e.Kind).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> DistinctResults(IEnumerable<AuditEntry> entries) =>
        entries.Select(e => e.Result).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
}

public static class AuditCsv
{
    public const string Header = "time_utc,kind,device_id,device_name,peer_device_id,peer_device_name,result,detail";

    public static string Write(IEnumerable<AuditEntry> entries, IReadOnlyDictionary<string, string>? names = null)
    {
        string NameOf(string? id) => id is not null && names is not null && names.TryGetValue(id, out var n) ? n : "";
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var e in entries)
        {
            sb.Append(string.Join(',',
                Cell(e.TimeUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                Cell(e.Kind), Cell(e.DeviceId), Cell(NameOf(e.DeviceId)),
                Cell(e.PeerDeviceId), Cell(NameOf(e.PeerDeviceId)), Cell(e.Result), Cell(e.Detail)));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>RFC 4180 quoting. A cell that starts with = + - @ (or a tab or carriage return) gets a leading apostrophe so a spreadsheet shows it as text instead of running it as a formula.</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

public enum InviteStatus { Active, Expired, UsedUp }

public static class InviteRules
{
    public static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);
    public const int MaxUses = 100;

    /// <summary>The lifetimes the Invite page offers, shortest first. The server's own limits are not known to the Manager; these are the Manager's sanity limits.</summary>
    public static readonly IReadOnlyList<TimeSpan> LifetimeChoices = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(24), TimeSpan.FromDays(7)];

    public static readonly IReadOnlyList<int> UseChoices = [1, 3, 5, 10, 25];

    /// <summary>Null when the request is acceptable, otherwise the resource key of the message to show.</summary>
    public static string? Validate(TimeSpan lifetime, int uses)
    {
        if (lifetime < MinLifetime || lifetime > MaxLifetime) return "Invite_BadLifetime";
        if (uses < 1 || uses > MaxUses) return "Invite_BadUses";
        return null;
    }

    public static InviteStatus StatusOf(InviteInfo invite, DateTimeOffset now) =>
        invite.UsesLeft <= 0 ? InviteStatus.UsedUp : invite.ExpiresUtc <= now ? InviteStatus.Expired : InviteStatus.Active;
}
