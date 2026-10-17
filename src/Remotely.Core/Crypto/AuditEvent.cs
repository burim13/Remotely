using System.Globalization;
using System.Text;

namespace Remotely.Core.Crypto;

/// <summary>What happened. The wire names (see <see cref="AuditEvent.Name"/>) are part of the audit log format and do not change; new kinds are added at the end.</summary>
public enum AuditKind
{
    ConnectionAttempt,
    ConnectionAccepted,
    ConnectionRefused,
    ConnectionClosed,
    TokenIssued,
    TokenRejected,
    TokenExpired,
    PairingSucceeded,
    PairingFailed,
    DeviceRevoked,
    ConsentGranted,
    ConsentDenied,
}

/// <summary>
/// One line of the audit log for connection events. Format (version 1), one event per line, UTF-8:
/// <c>2026-10-16T23:46:05.123Z remotely-audit v=1 event=connection.accepted mode="lan" peer="viewer-pc" addr="192.168.1.5" detail=""</c>.
/// Every value is a quoted string in which <c>\</c>, <c>"</c> and every control character are escaped, so a name chosen by another PC cannot start a new line or add a field.
/// There is no field for a token, a pairing code, a key or any message content, so none can be written by mistake.
/// </summary>
public sealed record AuditEvent(DateTimeOffset TimeUtc, AuditKind Kind, string Mode, string Peer = "", string Address = "", string Detail = "")
{
    public const int Version = 1;
    public const int MaxValueLength = 200;
    private const string Tag = "remotely-audit";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Name(AuditKind kind) => kind switch
    {
        AuditKind.ConnectionAttempt => "connection.attempt",
        AuditKind.ConnectionAccepted => "connection.accepted",
        AuditKind.ConnectionRefused => "connection.refused",
        AuditKind.ConnectionClosed => "connection.closed",
        AuditKind.TokenIssued => "token.issued",
        AuditKind.TokenRejected => "token.rejected",
        AuditKind.TokenExpired => "token.expired",
        AuditKind.PairingSucceeded => "pairing.succeeded",
        AuditKind.PairingFailed => "pairing.failed",
        AuditKind.DeviceRevoked => "device.revoked",
        AuditKind.ConsentGranted => "consent.granted",
        AuditKind.ConsentDenied => "consent.denied",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The single-line text. Values longer than <see cref="MaxValueLength"/> are cut.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(TimeUtc.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
        sb.Append(' ').Append(Tag).Append(" v=").Append(Version.ToString(CultureInfo.InvariantCulture));
        sb.Append(" event=").Append(Name(Kind));
        Append(sb, "mode", Mode);
        Append(sb, "peer", Peer);
        Append(sb, "addr", Address);
        Append(sb, "detail", Detail);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string? value)
    {
        sb.Append(' ').Append(key).Append("=\"");
        var text = value ?? "";
        if (text.Length > MaxValueLength) text = text[..MaxValueLength];
        foreach (var c in text)
        {
            if (c is '\\' or '"') sb.Append('\\').Append(c);
            else if (char.IsControl(c) || (int)c is 0x2028 or 0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
    }

    /// <summary>Reads a line written by <see cref="Format"/>. Returns false for anything else.</summary>
    public static bool TryParse(string line, out AuditEvent? parsed)
    {
        parsed = null;
        var parts = line.Split(' ', 4);
        if (parts.Length < 4 || parts[1] != Tag || parts[2] != "v=" + Version.ToString(CultureInfo.InvariantCulture)) return false;
        if (!DateTimeOffset.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)) return false;
        var rest = parts[3];
        if (!rest.StartsWith("event=", StringComparison.Ordinal)) return false;
        var space = rest.IndexOf(' ');
        if (space < 0) return false;
        var kindName = rest[6..space];
        AuditKind? kind = null;
        foreach (var k in Enum.GetValues<AuditKind>()) if (Name(k) == kindName) kind = k;
        if (kind is null) return false;

        var values = new Dictionary<string, string>();
        var i = space + 1;
        while (i < rest.Length)
        {
            var eq = rest.IndexOf("=\"", i, StringComparison.Ordinal);
            if (eq < 0) return false;
            var key = rest[i..eq];
            var value = new StringBuilder();
            var j = eq + 2;
            for (; j < rest.Length && rest[j] != '"'; j++)
            {
                if (rest[j] != '\\') { value.Append(rest[j]); continue; }
                if (j + 1 >= rest.Length) return false;
                if (rest[j + 1] is '\\' or '"') { value.Append(rest[j + 1]); j++; }
                else if (rest[j + 1] == 'u' && j + 5 < rest.Length && int.TryParse(rest.AsSpan(j + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) { value.Append((char)code); j += 5; }
                else return false;
            }
            if (j >= rest.Length) return false;
            values[key] = value.ToString();
            i = j + 1;
            if (i < rest.Length) { if (rest[i] != ' ') return false; i++; }
        }
        string Get(string k) => values.TryGetValue(k, out var v) ? v : "";
        parsed = new AuditEvent(time, kind.Value, Get("mode"), Get("peer"), Get("addr"), Get("detail"));
        return true;
    }
}
