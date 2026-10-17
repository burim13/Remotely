using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Remotely.Core.Update;

/// <summary>
/// Asks the releases feed whether a newer version exists. A release is offered only when it also publishes a SHA-256 for the package in its sums file
/// (<see cref="UpdateOptions.SumsAssetName"/>), so a download can always be checked. This class downloads nothing but the feed and the sums file.
/// </summary>
public sealed class UpdateChecker(HttpClient http, UpdateOptions? options = null)
{
    private readonly UpdateOptions _options = options ?? new UpdateOptions();

    public async Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, UpdateProduct product, string architecture, CancellationToken ct = default)
    {
        try
        {
            if (!_options.IsAllowedFeedUrl()) return Failed("The update feed address is not allowed.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Timeout);

            var json = await GetTextAsync(_options.FeedUrl, _options.MaxFeedBytes, timeout.Token).ConfigureAwait(false);
            var releases = ReleaseFeed.Parse(json).Where(r => _options.IncludePreReleases || !r.IsPreRelease).ToList();
            var newest = releases.OrderByDescending(r => r.Version).FirstOrDefault();
            if (newest is null) return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, current, null);
            if (newest.Version <= current) return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, newest.Version, null);

            var packageName = UpdateAssets.PackageName(product, newest.Version, architecture);
            var package = newest.Assets.FirstOrDefault(a => a.Name == packageName);
            if (package is null) return new UpdateCheckResult(UpdateCheckStatus.NoPackage, null, newest.Version, $"{newest.Tag} has no {packageName}.");
            if (!_options.IsTrustedUrl(package.Url)) return Failed($"The download address of {packageName} is not one this app trusts.", newest.Version);

            var sums = newest.Assets.FirstOrDefault(a => a.Name == _options.SumsAssetName);
            if (sums is null || !_options.IsTrustedUrl(sums.Url))
                return new UpdateCheckResult(UpdateCheckStatus.Unverifiable, null, newest.Version, $"{newest.Tag} does not publish {_options.SumsAssetName}.");
            var sumsText = await GetTextAsync(sums.Url, _options.MaxSumsBytes, timeout.Token).ConfigureAwait(false);
            if (!ReleaseFeed.ParseSums(sumsText).TryGetValue(packageName, out var hash))
                return new UpdateCheckResult(UpdateCheckStatus.Unverifiable, null, newest.Version, $"{_options.SumsAssetName} of {newest.Tag} has no usable SHA-256 for {packageName}.");

            return new UpdateCheckResult(UpdateCheckStatus.Available,
                new UpdateOffer(newest.Version, newest.Tag, newest.Notes, newest.PageUrl, package.Name, package.Url, package.Size, hash), newest.Version, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed("The update server did not answer in time."); }
        catch (HttpRequestException ex) { return Failed("Could not reach the update server: " + ex.Message); }
        catch (JsonException) { return Failed("The update feed was not in the expected format."); }
        catch (InvalidDataException ex) { return Failed(ex.Message); }
    }

    private static UpdateCheckResult Failed(string detail, ReleaseVersion? latest = null) => new(UpdateCheckStatus.Failed, null, latest, detail);

    private async Task<string> GetTextAsync(string url, long limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Remotely-UpdateCheck", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The answer from the update server is larger than allowed.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("The answer from the update server is larger than allowed.");
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(false, false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
