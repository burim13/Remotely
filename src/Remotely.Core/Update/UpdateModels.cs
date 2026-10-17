namespace Remotely.Core.Update;

/// <summary>Which installer an app looks for in a release.</summary>
public enum UpdateProduct
{
    /// <summary>The Remotely app (<c>Remotely-Setup-&lt;version&gt;-&lt;arch&gt;.exe</c>).</summary>
    Client,

    /// <summary>The server with the Server Manager (<c>Remotely-Server-Setup-&lt;version&gt;-x64.exe</c>, x64 only).</summary>
    Server,
}

public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record ReleaseInfo(string Tag, ReleaseVersion Version, string Name, string Notes, string PageUrl, bool IsPreRelease, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>A newer release whose package has a published SHA-256. Nothing is downloaded yet.</summary>
public sealed record UpdateOffer(ReleaseVersion Version, string Tag, string Notes, string PageUrl, string AssetName, string AssetUrl, long Size, string Sha256);

public enum UpdateCheckStatus
{
    UpToDate,
    Available,

    /// <summary>A newer release exists but it does not publish a SHA-256 for the package (or the sums file is missing or damaged), so it is not offered.</summary>
    Unverifiable,

    /// <summary>A newer release exists but has no installer for this kind of PC.</summary>
    NoPackage,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateOffer? Offer, ReleaseVersion? Latest, string? Detail);

public sealed record DownloadProgress(long BytesDone, long BytesTotal);

public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>Where updates come from. The defaults are this project's own GitHub releases and nothing else.</summary>
public sealed class UpdateOptions
{
    public const string DefaultFeedUrl = "https://api.github.com/repos/burim13/Remotely/releases";
    public const string DefaultDownloadPrefix = "https://github.com/burim13/Remotely/releases/download/";
    public const string DefaultSumsName = "SHA256SUMS.txt";

    public string FeedUrl { get; init; } = DefaultFeedUrl;

    /// <summary>Every asset URL (package and sums file) must start with one of these.</summary>
    public IReadOnlyList<string> TrustedDownloadPrefixes { get; init; } = [DefaultDownloadPrefix];

    public string SumsAssetName { get; init; } = DefaultSumsName;
    public bool IncludePreReleases { get; init; }

    /// <summary>Only for tests against a local feed: allows plain http to 127.0.0.1 / localhost. Never set by the apps.</summary>
    public bool AllowLoopbackHttp { get; init; }

    public long MaxFeedBytes { get; init; } = 2 * 1024 * 1024;
    public long MaxSumsBytes { get; init; } = 64 * 1024;
    public long MaxPackageBytes { get; init; } = 400L * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsTrustedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && !(AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return TrustedDownloadPrefixes.Any(p => url!.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>The feed itself: https (or loopback http in tests).</summary>
    public bool IsAllowedFeedUrl() =>
        Uri.TryCreate(FeedUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

public static class UpdateAssets
{
    /// <summary>The installer file name the build produces (<c>installer/*.iss</c>, <c>OutputBaseFilename</c>).</summary>
    public static string PackageName(UpdateProduct product, ReleaseVersion version, string architecture) => product switch
    {
        UpdateProduct.Client => $"Remotely-Setup-{version}-{architecture}.exe",
        UpdateProduct.Server => $"Remotely-Server-Setup-{version}-x64.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(product)),
    };
}
