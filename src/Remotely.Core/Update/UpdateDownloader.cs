using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Remotely.Core.Update;

/// <summary>A package that was downloaded and passed every check. Running it is always the person's own click.</summary>
public sealed record VerifiedPackage(string Path, string Sha256, SignatureResult Signature)
{
    /// <summary>Hashes the file again, to be called right before it is run: false when the file was removed or changed since the download.</summary>
    public async Task<bool> StillMatchesAsync(CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            return hash == Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Downloads the package of an <see cref="UpdateOffer"/> into a folder, hashing while it streams. A file whose SHA-256 differs from the published one is deleted and
/// refused; so is a file whose Authenticode signature is present but not valid. An unsigned file passes (the installers are unsigned for now) and says so in
/// <see cref="VerifiedPackage.Signature"/>. Nothing here ever starts the package.
/// </summary>
public sealed partial class UpdateDownloader(HttpClient http, UpdateOptions? options = null, IAuthenticodeVerifier? signature = null)
{
    private readonly UpdateOptions _options = options ?? new UpdateOptions();
    private readonly IAuthenticodeVerifier _signature = signature ?? new PowerShellAuthenticodeVerifier();

    public async Task<VerifiedPackage> DownloadAsync(UpdateOffer offer, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (!_options.IsTrustedUrl(offer.AssetUrl)) throw new UpdateVerificationException("The download address is not one this app trusts.");
        if (!HexHash().IsMatch(offer.Sha256)) throw new UpdateVerificationException("The published SHA-256 is not valid.");
        if (!SafeName().IsMatch(offer.AssetName) || !offer.AssetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException("The package name is not valid.");

        Directory.CreateDirectory(directory);
        var final = System.IO.Path.Combine(directory, offer.AssetName);
        var partial = final + ".part";
        DeleteQuietly(partial);
        DeleteQuietly(final);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, offer.AssetUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Remotely-UpdateCheck", "1"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? offer.Size;
            if (total > _options.MaxPackageBytes) throw new UpdateVerificationException("The package is larger than allowed.");

            string actual;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > _options.MaxPackageBytes) throw new UpdateVerificationException("The package is larger than allowed.");
                    hasher.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(new DownloadProgress(done, total));
                }
                actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (!string.Equals(actual, offer.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateVerificationException("The downloaded file does not match the SHA-256 published with the release. It was deleted.");

            File.Move(partial, final);
            var sig = await _signature.VerifyAsync(final, ct).ConfigureAwait(false);
            if (sig.Status == SignatureStatus.Invalid)
                throw new UpdateVerificationException("The file has a digital signature that Windows does not accept. " + sig.Detail);
            return new VerifiedPackage(final, actual, sig);
        }
        catch
        {
            DeleteQuietly(partial);
            DeleteQuietly(final);
            throw;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexHash();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,120}$")]
    private static partial Regex SafeName();
}
