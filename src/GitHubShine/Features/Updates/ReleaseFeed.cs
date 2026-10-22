using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GitHubShine.Updates;

/// <summary>A published release that is newer than the running app.</summary>
/// <param name="Version">Normalised to three parts, so 1.2 and 1.2.0 compare equal.</param>
/// <param name="Tag">The tag as published — "v1.2.0-beta1" — for display.</param>
/// <param name="NotesHtml">Release notes as rendered (and sanitised) by the release host, or null when there are none.</param>
/// <param name="PackageUrl">The macOS disk image, when the release carries one.</param>
/// <param name="ChecksumsUrl">SHA256SUMS.txt, when the release carries one.</param>
public sealed record AvailableUpdate(
    Version Version,
    string Tag,
    string Title,
    string? NotesHtml,
    string PageUrl,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    string? PackageUrl,
    string? PackageName,
    long? PackageSize,
    string? ChecksumsUrl
);

/// <summary>
/// Reads this app's own releases. Deliberately not routed through IGitProvider: that is the
/// monitored accounts' provider, authenticated with the user's tokens, whereas this is one fixed,
/// public repository read anonymously — and it must work before any account is added.
/// </summary>
public static class ReleaseFeed
{
    public const string Owner = "aritchie";
    public const string Repository = "githubmon";

    // The checksum file the release workflow publishes next to the DMG. Load-bearing for the
    // macOS installer — see docs/RELEASING.md.
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    static readonly HttpClient http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubShine", "1.0"));
        // "full" returns body_html alongside body, so the notes can be shown without shipping a
        // markdown renderer.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.full+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>
    /// The highest-versioned published release, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Lists rather than asking for /releases/latest, which skips prereleases — and the release
    /// workflow marks any hyphenated tag as one. Prereleases are offered too: the version the app
    /// is stamped with drops the suffix (CFBundleShortVersionString is numbers only), so an
    /// installed 1.2.0-beta1 reports 1.2.0 and is correctly not offered 1.2.0-beta2, but is
    /// offered 1.2.1-anything.
    /// </remarks>
    public static async Task<AvailableUpdate?> GetNewestAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=20";
        var releases = await http
            .GetFromJsonAsync(url, ReleaseJsonContext.Default.ListReleaseDto, ct)
            .ConfigureAwait(false);

        var newest = (releases ?? [])
            .Where(r => !r.Draft)
            .Select(r => (Release: r, Version: ParseVersion(r.TagName)))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();

        if (newest.Release is null)
            return null;

        var r = newest.Release;
        var dmg = r.Assets?.FirstOrDefault(a => a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));
        var sums = r.Assets?.FirstOrDefault(a => a.Name.Equals(ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));

        return new AvailableUpdate(
            newest.Version!,
            r.TagName,
            String.IsNullOrWhiteSpace(r.Name) ? r.TagName : r.Name,
            String.IsNullOrWhiteSpace(r.BodyHtml) ? null : r.BodyHtml,
            r.HtmlUrl,
            r.PublishedAt,
            r.Prerelease,
            dmg?.BrowserDownloadUrl,
            dmg?.Name,
            dmg?.Size,
            sums?.BrowserDownloadUrl
        );
    }

    /// <summary>Downloads a release asset to <paramref name="destination"/>, reporting 0–1 progress when the size is known.</summary>
    public static async Task DownloadAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Assets come back as a redirect to storage; the API's JSON Accept header would get a
        // 415 from the asset endpoint, so ask for the bytes.
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0)
                progress?.Report((double)read / total.Value);
        }
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "v1.2.3-beta1" → 1.2.3. Missing parts are zero, so the result compares sanely against a
    /// two-part app version: <see cref="Version"/> itself ranks 1.2 below 1.2.0.
    /// </summary>
    public static Version? ParseVersion(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
            return null;

        var s = value.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
            s = s[..cut];

        var parts = s.Split('.');
        if (parts.Length is 0 or > 4)
            return null;

        var numbers = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!Int32.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return null;
        }
        return new Version(numbers[0], numbers[1], numbers[2]);
    }
}

sealed record ReleaseDto(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("body_html")] string? BodyHtml,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("assets")] List<ReleaseAssetDto>? Assets
);

sealed record ReleaseAssetDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl
);

[JsonSerializable(typeof(List<ReleaseDto>))]
sealed partial class ReleaseJsonContext : JsonSerializerContext;
