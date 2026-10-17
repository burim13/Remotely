using System.Globalization;

namespace Remotely.Core.Update;

/// <summary>
/// A version as written in a release tag: <c>v0.5.0</c>, <c>0.5.0</c> or <c>0.5.0-beta.1</c> (semantic versioning; build metadata after <c>+</c> is ignored).
/// A version with a pre-release part sorts before the same version without one.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly string[] _pre;

    private ReleaseVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPreRelease => _pre.Length > 0;

    /// <summary>Without the leading "v", for example <c>0.5.0-beta.1</c>.</summary>
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (_pre.Length > 0 ? "-" + string.Join('.', _pre) : "");

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.Length > 64) return false;
        if (s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string[] pre = [];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var tail = s[(dash + 1)..];
            s = s[..dash];
            pre = tail.Split('.');
            if (pre.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return false;
        }
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || (parts[i].Length > 1 && parts[i][0] == '0')) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version such as 1.2.3 or v1.2.3-beta.1.");

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (_pre.Length == 0 || other._pre.Length == 0) return other._pre.Length.CompareTo(_pre.Length);   // no pre-release part is the newer one
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i];
            var b = other._pre[i];
            var aNum = a.All(char.IsAsciiDigit);
            var bNum = b.All(char.IsAsciiDigit);
            if (aNum && bNum)
            {
                // Compare by length first so very long numbers need no parsing.
                var t = a.TrimStart('0');
                var u = b.TrimStart('0');
                c = t.Length != u.Length ? t.Length.CompareTo(u.Length) : string.CompareOrdinal(t, u);
            }
            else if (aNum != bNum) c = aNum ? -1 : 1;   // numbers sort before words
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _pre));
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
}
