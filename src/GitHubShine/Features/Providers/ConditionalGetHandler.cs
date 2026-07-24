using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace GitHubShine.Providers;

/// <summary>
/// Turns every GET into an HTTP conditional request. It remembers the <c>ETag</c> (and body) of
/// each URL's last <c>200</c>, sends <c>If-None-Match</c> on the next GET, and when the server
/// replies <c>304 Not Modified</c> it replays the cached body as a synthetic <c>200</c> so the
/// caller — Octokit or Gitea's JSON parser — never sees the 304.
///
/// Why it matters: on GitHub a conditional request answered with 304 does NOT count against the
/// REST rate limit, so an unchanged repo can be polled essentially for free. Both provider
/// HttpClients share this one handler. It also forwards the <c>X-RateLimit-*</c> headers of every
/// response to <see cref="RateLimitMonitor"/> so the poller can back off as the budget runs low.
///
/// One instance is created per provider (i.e. per account), so its ETag cache is naturally scoped
/// to a single account/host and never crosses credentials.
/// </summary>
sealed class ConditionalGetHandler : DelegatingHandler
{
    // Don't buffer large payloads (repo zip archives, etc.) into the in-memory ETag cache.
    const long MaxCachedBytes = 5 * 1024 * 1024;

    readonly record struct Entry(string ETag, byte[] Body, string? MediaType);

    readonly ConcurrentDictionary<string, Entry> cache = new();
    readonly RateLimitMonitor monitor;
    readonly ILogger logger;

    public ConditionalGetHandler(HttpMessageHandler inner, RateLimitMonitor monitor, ILogger logger) : base(inner)
    {
        this.monitor = monitor;
        this.logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cacheable = request.Method == HttpMethod.Get && request.RequestUri is not null;
        var key = request.RequestUri?.AbsoluteUri ?? string.Empty;

        Entry entry = default;
        var haveEntry = cacheable && this.cache.TryGetValue(key, out entry);
        if (haveEntry)
            request.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        ReportRateLimit(response);

        if (!cacheable)
            return response;

        if (response.StatusCode == HttpStatusCode.NotModified && haveEntry)
        {
            // Server confirmed nothing changed (and didn't charge the rate limit). Hand the caller
            // the cached body as a 200, carrying the live response's headers (rate-limit, etc.).
            var replay = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Version = response.Version,
                ReasonPhrase = "OK (ETag cache)",
                Content = new ByteArrayContent(entry.Body)
            };
            foreach (var header in response.Headers)
                replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (entry.MediaType is not null)
                replay.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(entry.MediaType);

            response.Dispose();
            this.logger.LogDebug("[ETag] 304 FREE {Url}", key);
            return replay;
        }

        // A fresh 200 carrying an ETag: buffer it so the next GET can be a free 304. Restrict to
        // JSON so we never pull a big binary (repo zip archives, etc.) into memory, and skip any
        // response that declares an oversized length up front. GitHub sometimes chunks large list
        // responses (no Content-Length), so we can't gate on the header alone — we re-check the
        // buffered size below instead.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (response.IsSuccessStatusCode
            && response.Headers.ETag is { } etag
            && (response.Content.Headers.ContentLength ?? 0) <= MaxCachedBytes
            && IsJson(mediaType))
        {
            // ReadAsByteArrayAsync buffers the content internally, so the caller can still read it.
            var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (body.LongLength <= MaxCachedBytes)
            {
                this.cache[key] = new Entry(etag.ToString(), body, mediaType);
                this.logger.LogDebug("[ETag] 200 store {Url}", key);
            }
        }

        return response;
    }

    void ReportRateLimit(HttpResponseMessage response)
    {
        if (!TryGetInt(response, "X-RateLimit-Remaining", out var remaining))
            return;

        int? limit = TryGetInt(response, "X-RateLimit-Limit", out var l) ? l : null;
        DateTimeOffset? reset = TryGetLong(response, "X-RateLimit-Reset", out var r)
            ? DateTimeOffset.FromUnixTimeSeconds(r)
            : null;

        this.monitor.Report(remaining, limit, reset);
        this.logger.LogDebug("[ETag] rate-limit remaining={Remaining}/{Limit}", remaining, limit);
    }

    // GitHub/Gitea API bodies are application/json (or application/vnd.github+json); archives are
    // application/zip and must never be buffered into the ETag cache.
    static bool IsJson(string? mediaType)
        => mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    static bool TryGetInt(HttpResponseMessage response, string header, out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(header, out var values)
            && int.TryParse(values.FirstOrDefault(), out value);
    }

    static bool TryGetLong(HttpResponseMessage response, string header, out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(header, out var values)
            && long.TryParse(values.FirstOrDefault(), out value);
    }
}
