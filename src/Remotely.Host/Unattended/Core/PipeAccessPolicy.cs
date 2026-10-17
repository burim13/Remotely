using System.Text.RegularExpressions;

namespace Remotely.Host.Unattended;

// Everything under Unattended/Core is free of Windows-only APIs, so the unit tests (which run on Linux) compile these files directly.

/// <summary>What the service can find out about the program on the other end of the pipe.</summary>
/// <param name="Sid">User SID of the client, null when it could not be read.</param>
/// <param name="Session">Windows session the client runs in.</param>
/// <param name="IsRemote">The client reached the pipe over the network (SMB), not from this PC.</param>
public sealed record PeerInfo(string? Sid, uint? Session, bool IsRemote);

public enum PeerVerdict { Allowed, NoUser, WrongUser, WrongSession, Remote, Unidentified }

/// <summary>
/// Who may open the pipe, written as the security descriptor that Windows enforces and as the second check the service makes after a client has connected.
/// Both are plain functions of their input so they can be tested without Windows; whether Windows really enforces the descriptor can only be shown on Windows.
/// </summary>
public static partial class PipeAccessPolicy
{
    /// <summary>
    /// Rights given to the signed-in user: read data, write data, read/write attributes and extended attributes, read permissions, synchronize (0x12019B).
    /// Deliberately without FILE_CREATE_PIPE_INSTANCE (0x4), so the user cannot create another instance of the pipe.
    /// </summary>
    public const int UserRights = 0x0012019B;

    /// <summary>FILE_ALL_ACCESS, for the service (SYSTEM) and for the network deny. Written as a number, not as "GA", so no generic-rights mapping is involved.</summary>
    public const int AllAccess = 0x001F01FF;

    /// <summary>
    /// <c>D:P</c> = a DACL that inherits nothing. First a deny for network logons (a client coming through SMB), then SYSTEM (the service) with all access, then the one user.
    /// With no user (nobody is signed in) only SYSTEM remains. Administrators, Everyone, Users and Authenticated Users are not mentioned, so they get nothing.
    /// </summary>
    public static string Sddl(string? userSid)
    {
        var text = $"D:P(D;;0x{AllAccess:X};;;NU)(A;;0x{AllAccess:X};;;SY)";
        if (userSid is null) return text;
        if (!IsSid(userSid)) throw new ArgumentException("Not a SID.", nameof(userSid));
        return text + $"(A;;0x{UserRights:X};;;{userSid})";
    }

    [GeneratedRegex(@"^S-1-\d{1,10}(-\d{1,10}){1,14}$")]
    private static partial Regex SidShape();

    public static bool IsSid(string? text) => text is not null && SidShape().IsMatch(text);

    /// <summary>
    /// The check after a client is connected (the pipe's access list already refused other users when it opened, this is the second line): it must be the signed-in user,
    /// in the console session, on this PC. <paramref name="expectedSession"/> null means the session is not known and then the user check alone decides.
    /// </summary>
    public static PeerVerdict Decide(PeerInfo peer, string? expectedSid, uint? expectedSession)
    {
        if (expectedSid is null) return PeerVerdict.NoUser;
        if (peer.IsRemote) return PeerVerdict.Remote;
        if (peer.Sid is null) return PeerVerdict.Unidentified;
        if (!string.Equals(peer.Sid, expectedSid, StringComparison.OrdinalIgnoreCase)) return PeerVerdict.WrongUser;
        if (expectedSession is { } session && peer.Session != session) return PeerVerdict.WrongSession;
        return PeerVerdict.Allowed;
    }
}
