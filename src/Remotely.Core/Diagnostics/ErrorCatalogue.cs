using System.Reflection;
using Remotely.Core.Admin;

namespace Remotely.Core.Diagnostics;

/// <summary>Which program shows a code, and so which program's string file holds its text.</summary>
public enum ErrorScope { App, Manager, Both }

/// <summary>
/// One entry of the error catalogue. <see cref="Code"/> is what a person quotes ("RMT-1006"); the texts are in <c>Strings/*/Resources.resw</c> under
/// <c>Diag_Err_{number}_Title</c>, <c>_Cause</c> and <c>_Fix</c>, and the page is <c>docs/errors/{Code}.md</c>.
/// </summary>
public sealed record ErrorInfo(string Code, string Name, ErrorScope Scope)
{
    public string Number => Code[4..];
    public string TitleKey => $"Diag_Err_{Number}_Title";
    public string CauseKey => $"Diag_Err_{Number}_Cause";
    public string FixKey => $"Diag_Err_{Number}_Fix";
    public string DocFile => $"{Code}.md";
}

/// <summary>
/// The codes for errors a person actually sees. Numbers: 1xxx same-network connect, 2xxx handshake, 3xxx server and pairing, 4xxx server admin, 5xxx file transfer,
/// 6xxx setup and invites, 7xxx the Manager's elevated helper, 9xxx anything else. A code never changes meaning and is never reused.
/// <see cref="Classify(Exception)"/> finds the code of an exception by its type name and the fixed words of its message, because the exception classes belong to other
/// parts of the program and carry no code of their own. Every rule below is exercised by a test that makes the real failure happen (see DiagnosticsTests), so a reworded
/// message makes that test fail instead of silently losing the code.
/// </summary>
public static class ErrorCatalogue
{
    private static ErrorInfo E(int number, string name, ErrorScope scope = ErrorScope.App) => new($"RMT-{number}", name, scope);

    public static readonly ErrorInfo TokenNotValid = E(1001, nameof(TokenNotValid));
    public static readonly ErrorInfo AddressIsIPv6 = E(1002, nameof(AddressIsIPv6));
    public static readonly ErrorInfo AddressNotValid = E(1003, nameof(AddressNotValid));
    public static readonly ErrorInfo NoNetwork = E(1004, nameof(NoNetwork), ErrorScope.Both);
    public static readonly ErrorInfo OnlyIPv6 = E(1005, nameof(OnlyIPv6), ErrorScope.Both);
    public static readonly ErrorInfo PcNotFound = E(1006, nameof(PcNotFound), ErrorScope.Both);
    public static readonly ErrorInfo PcUnreachable = E(1007, nameof(PcUnreachable), ErrorScope.Both);
    public static readonly ErrorInfo TokenRefused = E(1008, nameof(TokenRefused));
    public static readonly ErrorInfo TokenLocked = E(1009, nameof(TokenLocked));
    public static readonly ErrorInfo SavedPcRefused = E(1010, nameof(SavedPcRefused));
    public static readonly ErrorInfo ConsentDenied = E(1011, nameof(ConsentDenied));

    public static readonly ErrorInfo NotRemotely = E(2001, nameof(NotRemotely));
    public static readonly ErrorInfo WrongDevice = E(2002, nameof(WrongDevice));
    public static readonly ErrorInfo BadSignature = E(2003, nameof(BadSignature));
    public static readonly ErrorInfo NotTrusted = E(2004, nameof(NotTrusted));

    public static readonly ErrorInfo NotPaired = E(3001, nameof(NotPaired));
    public static readonly ErrorInfo InsecureTransport = E(3002, nameof(InsecureTransport));
    public static readonly ErrorInfo PinRequired = E(3003, nameof(PinRequired));
    public static readonly ErrorInfo ServerProtocol = E(3004, nameof(ServerProtocol));
    public static readonly ErrorInfo ServerReplyUnexpected = E(3005, nameof(ServerReplyUnexpected));
    public static readonly ErrorInfo PairingRefused = E(3006, nameof(PairingRefused));
    public static readonly ErrorInfo TooManyAttempts = E(3007, nameof(TooManyAttempts));
    public static readonly ErrorInfo DeviceLimit = E(3008, nameof(DeviceLimit));
    public static readonly ErrorInfo DeviceBlocked = E(3009, nameof(DeviceBlocked));
    public static readonly ErrorInfo DeviceOffline = E(3010, nameof(DeviceOffline));
    public static readonly ErrorInfo DeviceNotAvailable = E(3011, nameof(DeviceNotAvailable));
    public static readonly ErrorInfo ServerUnreachable = E(3012, nameof(ServerUnreachable), ErrorScope.Both);
    public static readonly ErrorInfo ServerAddressNotValid = E(3013, nameof(ServerAddressNotValid), ErrorScope.Both);

    public static readonly ErrorInfo AdminTokenUnreadable = E(4001, nameof(AdminTokenUnreadable), ErrorScope.Manager);
    public static readonly ErrorInfo AdminPortUnreachable = E(4002, nameof(AdminPortUnreachable), ErrorScope.Manager);
    public static readonly ErrorInfo AdminRefused = E(4003, nameof(AdminRefused), ErrorScope.Manager);

    public static readonly ErrorInfo TransferDeclined = E(5001, nameof(TransferDeclined));
    public static readonly ErrorInfo TransferTooLarge = E(5002, nameof(TransferTooLarge));
    public static readonly ErrorInfo TransferQuotaExceeded = E(5003, nameof(TransferQuotaExceeded));
    public static readonly ErrorInfo TransferDiskFull = E(5004, nameof(TransferDiskFull));
    public static readonly ErrorInfo TransferNameClash = E(5005, nameof(TransferNameClash));
    public static readonly ErrorInfo TransferAccessDenied = E(5006, nameof(TransferAccessDenied));
    public static readonly ErrorInfo TransferHashMismatch = E(5007, nameof(TransferHashMismatch));
    public static readonly ErrorInfo TransferNotFound = E(5008, nameof(TransferNotFound));
    public static readonly ErrorInfo TransferBusy = E(5010, nameof(TransferBusy));
    public static readonly ErrorInfo TransferProtocol = E(5011, nameof(TransferProtocol));
    public static readonly ErrorInfo TransferUnsafeArchive = E(5012, nameof(TransferUnsafeArchive));
    public static readonly ErrorInfo TransferUnsupported = E(5013, nameof(TransferUnsupported));
    public static readonly ErrorInfo TransferLinkLost = E(5014, nameof(TransferLinkLost));
    public static readonly ErrorInfo TransferOther = E(5015, nameof(TransferOther));
    public static readonly ErrorInfo TransferSourceChanged = E(5016, nameof(TransferSourceChanged));

    public static readonly ErrorInfo InviteNotValid = E(6001, nameof(InviteNotValid));
    public static readonly ErrorInfo PublicNameNotValid = E(6002, nameof(PublicNameNotValid), ErrorScope.Manager);
    public static readonly ErrorInfo FirewallChangeFailed = E(6003, nameof(FirewallChangeFailed), ErrorScope.Manager);

    public static readonly ErrorInfo HelperStepFailed = E(7001, nameof(HelperStepFailed), ErrorScope.Manager);
    public static readonly ErrorInfo HelperBadUsage = E(7002, nameof(HelperBadUsage), ErrorScope.Manager);
    public static readonly ErrorInfo ServerExeMissing = E(7003, nameof(ServerExeMissing), ErrorScope.Manager);
    public static readonly ErrorInfo NotElevated = E(7004, nameof(NotElevated), ErrorScope.Manager);
    public static readonly ErrorInfo ElevationDeclined = E(7005, nameof(ElevationDeclined), ErrorScope.Manager);

    public static readonly ErrorInfo Unexpected = E(9001, nameof(Unexpected), ErrorScope.Both);

    public static IReadOnlyList<ErrorInfo> All { get; } =
        [.. typeof(ErrorCatalogue).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(ErrorInfo)).Select(f => (ErrorInfo)f.GetValue(null)!).OrderBy(e => e.Code, StringComparer.Ordinal)];

    public static ErrorInfo? Find(string? code) => All.FirstOrDefault(e => string.Equals(e.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---- file transfer: one code per TransferError (the enum lives in Remotely.HostCore, which this assembly cannot see) ----

    /// <summary>The code for a <c>TransferError</c> by its name. None and Cancelled are not errors and have no code.</summary>
    public static ErrorInfo? FromTransferError(string? name) => name switch
    {
        "Declined" => TransferDeclined,
        "TooLarge" => TransferTooLarge,
        "QuotaExceeded" => TransferQuotaExceeded,
        "DiskFull" => TransferDiskFull,
        "NameClash" => TransferNameClash,
        "AccessDenied" => TransferAccessDenied,
        "HashMismatch" => TransferHashMismatch,
        "NotFound" => TransferNotFound,
        "Busy" => TransferBusy,
        "Protocol" => TransferProtocol,
        "UnsafeArchive" => TransferUnsafeArchive,
        "Unsupported" => TransferUnsupported,
        "LinkLost" => TransferLinkLost,
        "Other" => TransferOther,
        "SourceChanged" => TransferSourceChanged,
        _ => null,
    };

    // ---- the Manager's elevated helper (HelperExit in Remotely.Core.ServerManager) ----

    public static ErrorInfo? FromHelperExit(int exitCode) => exitCode switch
    {
        0 => null,
        1 => HelperStepFailed,
        2 => HelperBadUsage,
        3 => ServerExeMissing,
        4 => NotElevated,
        1223 => ElevationDeclined,
        _ => HelperStepFailed,
    };

    // ---- setup results that carry a string-resource key instead of an exception ----

    public static ErrorInfo? FromSetupKey(string? resourceKey) => resourceKey switch
    {
        "Setup_Err_NameHasScheme" or "Setup_Err_NameInvalid" or "Setup_Err_NamePort" or "Setup_Err_NameIpv6Brackets" => PublicNameNotValid,
        "Setup_Err_FirewallFailed" => FirewallChangeFailed,
        _ => null,
    };

    // ---- exceptions and messages ----

    private sealed record Rule(ErrorInfo Error, string[] Types, string[] Words);

    // Order matters: the first rule that fits wins, so specific words come before the catch-all of the same type.
    private static readonly Rule[] Rules =
    [
        new(TokenNotValid, ["LanConnectException"], ["Type the token shown", "A token has 12 letters", "A token never contains"]),
        new(AddressIsIPv6, ["LanConnectException"], ["That is an IPv6 address"]),
        new(AddressNotValid, ["LanConnectException"], ["That address is not valid"]),
        new(NoNetwork, ["LanConnectException"], ["is not connected to a network"]),
        new(OnlyIPv6, ["LanConnectException"], ["has only an IPv6 network address"]),
        new(PcNotFound, ["LanConnectException"], ["No PC on this network is showing a token"]),
        new(PcUnreachable, ["LanConnectException"], ["Could not reach the PC at"]),
        new(TokenRefused, ["LanConnectException", "LanAuthException"], ["did not accept the token", "Wrong token"]),
        new(TokenLocked, ["LanAuthException"], ["tried too often"]),
        new(SavedPcRefused, ["LanConnectException"], ["did not accept this PC", "not the one saved for this entry"]),
        new(ConsentDenied, ["ConsentDeniedException"], []),

        new(WrongDevice, ["HandshakeException"], ["not the one expected", "meant for a different device", "token is for a different PC"]),
        new(BadSignature, ["HandshakeException"], ["signature is invalid"]),
        new(NotTrusted, ["HandshakeException"], ["not trusted by this device"]),
        new(NotRemotely, ["HandshakeException"], []),

        new(NotPaired, ["CoordinatorException"], ["not paired"]),
        new(InsecureTransport, ["CoordinatorException"], ["Refusing an unencrypted connection"]),
        new(PinRequired, ["CoordinatorException"], ["pinned server key is required"]),
        new(ServerProtocol, ["CoordinatorException"], ["different protocol version"]),
        new(ServerReplyUnexpected, ["CoordinatorException"], ["Unexpected reply from server", "Server closed the connection"]),
        new(DeviceLimit, ["CoordinatorException"], ["reached its limit of"]),
        new(DeviceBlocked, ["CoordinatorException"], ["blocked by the server"]),
        new(TooManyAttempts, ["CoordinatorException"], ["Too many attempts", "Too many connection attempts", "Too many sessions"]),
        new(PairingRefused, ["CoordinatorException"], ["Invalid or expired code", "Pairing refused"]),
        new(DeviceOffline, ["CoordinatorException"], ["That device is offline"]),
        new(DeviceNotAvailable, ["CoordinatorException"], ["Unknown device", "cannot connect to itself", "does not accept connections"]),
        new(ServerUnreachable, ["SocketException", "WebSocketException", "HttpRequestException"], []),

        new(AdminTokenUnreadable, ["ServerAdminException"], ["admin token at"]),
        new(AdminPortUnreachable, ["ServerAdminException"], ["admin port cannot be reached"]),

        new(InviteNotValid, [], ["That is not a Remotely invite", "That invite is not complete", "That invite has a typing mistake", "This invite was made by a newer Remotely", "Paste the invite from your server"]),
    ];

    /// <summary>The code of an exception (looking through inner exceptions), or null when the catalogue has none for it.</summary>
    public static ErrorInfo? Classify(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex.GetType().Name == "TransferException" && ex.GetType().GetProperty("Code")?.GetValue(ex) is { } transfer)
                return FromTransferError(transfer.ToString());
            foreach (var rule in Rules)
                if (Fits(rule, ex)) return rule.Error;
            if (ex is ServerAdminException { StatusCode: > 0 }) return AdminRefused;
        }
        return null;
    }

    /// <summary>The code for a plain message, for failures that are reported as text (a bad invite, a refusal from the server). Null when nothing fits.</summary>
    public static ErrorInfo? Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        foreach (var rule in Rules)
            if (rule.Words.Length > 0 && rule.Words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))) return rule.Error;
        return null;
    }

    /// <summary>Like <see cref="Classify(Exception)"/> but never null: an error the catalogue does not know is <see cref="Unexpected"/>.</summary>
    public static ErrorInfo Describe(Exception? exception) => Classify(exception) ?? Unexpected;

    /// <summary>"RMT-1006" for display next to a message, or an empty string when there is no code.</summary>
    public static string Label(Exception? exception) => Classify(exception)?.Code ?? "";

    private static bool Fits(Rule rule, Exception ex)
    {
        if (rule.Types.Length > 0 && !TypeNames(ex).Any(n => rule.Types.Contains(n, StringComparer.Ordinal))) return false;
        if (rule.Words.Length == 0) return true;
        return rule.Words.Any(w => ex.Message.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> TypeNames(Exception ex)
    {
        for (var t = ex.GetType(); t is not null && t != typeof(object); t = t.BaseType) yield return t.Name;
    }
}
