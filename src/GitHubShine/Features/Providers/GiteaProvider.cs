using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GitHubShine.Providers;

/// <summary>
/// Gitea/Forgejo-backed provider. Gitea exposes a GitHub-v3-shaped REST API under
/// <c>{host}/api/v1</c>, so this maps that JSON onto the same dashboard records the
/// GitHub provider produces. Parsing is done with <see cref="JsonDocument"/> (AOT/trim
/// safe, no serializer context needed) since we only pluck a few fields per response.
/// </summary>
public sealed class GiteaProvider : IGitProvider
{
    readonly HttpClient http;
    readonly string accountId;

    public GiteaProvider(HttpClient http, string? hostUrl, string token, string accountId)
    {
        if (string.IsNullOrWhiteSpace(hostUrl))
            throw new InvalidOperationException("A Gitea account requires a host URL (e.g. https://gitea.com).");

        this.http = http;
        this.accountId = accountId;
        this.http.BaseAddress = new Uri(hostUrl.TrimEnd('/') + "/api/v1/");
        this.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubShine/1.0");
    }

    public async Task<RepoSnapshotData> GetRepoSnapshotAsync(MonitoredRepo repo, int runCount, CancellationToken ct = default)
    {
        // Gitea has no combined endpoint, so fan out the three reads concurrently. Each still goes
        // through the shared HttpClient's ConditionalGetHandler, so unchanged resources are 304s.
        var statsTask = GetRepoStatsAsync(repo, ct);
        var prsTask = GetOpenPullRequestsAsync(repo, ct);
        var runsTask = GetRecentWorkflowRunsAsync(repo, runCount, ct);
        await Task.WhenAll(statsTask, prsTask, runsTask).ConfigureAwait(false);
        return new RepoSnapshotData(statsTask.Result, prsTask.Result, runsTask.Result);
    }

    async Task<RepoStats> GetRepoStatsAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"repos/{repo.Owner}/{repo.Name}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        // Gitea already separates issues from PRs, so open_issues_count is a true
        // issue count — no PR subtraction needed (unlike GitHub).
        return new RepoStats(
            GetInt(root, "open_issues_count"),
            GetInt(root, "stars_count"),
            GetInt(root, "forks_count"),
            GetInt(root, "watchers_count"),
            GetPushedAt(root));
    }

    async Task<IReadOnlyList<PullRequestSummary>> GetOpenPullRequestsAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"repos/{repo.Owner}/{repo.Name}/pulls?state=open&limit=50", ct).ConfigureAwait(false);
        var list = new List<PullRequestSummary>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            list.Add(new PullRequestSummary(
                GetInt(p, "number"),
                GetString(p, "title") ?? "",
                GetString(GetProp(p, "user"), "login") ?? "?",
                GetString(p, "html_url") ?? "",
                GetBool(p, "draft"),
                GetBool(p, "mergeable")));
        }
        return list;
    }

    async Task<IReadOnlyList<WorkflowRunSummary>> GetRecentWorkflowRunsAsync(MonitoredRepo repo, int count, CancellationToken ct = default)
    {
        // Gitea Actions is comparatively young and the runs endpoint shape/availability
        // varies by version. Degrade to an empty list on any failure so the rest of the
        // snapshot still renders rather than erroring the whole repo tile.
        try
        {
            using var doc = await GetJsonAsync($"repos/{repo.Owner}/{repo.Name}/actions/tasks?limit={count}", ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                return Array.Empty<WorkflowRunSummary>();

            var list = new List<WorkflowRunSummary>();
            foreach (var r in runs.EnumerateArray())
            {
                list.Add(new WorkflowRunSummary(
                    GetLong(r, "id"),
                    GetString(r, "name") ?? "(workflow)",
                    GetString(r, "head_branch") ?? "?",
                    GetString(r, "status") ?? "unknown",
                    GetString(r, "conclusion"),
                    GetDate(r, "created_at"),
                    GetString(r, "html_url") ?? GetString(r, "url") ?? ""));
            }
            return list;
        }
        catch
        {
            return Array.Empty<WorkflowRunSummary>();
        }
    }

    public async Task<IReadOnlyList<InboxItem>> GetInboxAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("notifications?all=false&participating=true", ct).ConfigureAwait(false);
        var list = new List<InboxItem>();
        foreach (var n in doc.RootElement.EnumerateArray())
        {
            var subject = GetProp(n, "subject");
            list.Add(new InboxItem(
                accountId,
                GetString(n, "id") ?? GetLong(n, "id").ToString(),
                GetString(GetProp(n, "repository"), "full_name") ?? "?",
                GetString(subject, "title") ?? "(no title)",
                // Gitea populates far fewer notification reasons than GitHub; most map to Other.
                MapReason(GetString(n, "reason")),
                GetString(subject, "type") ?? "",
                GetDate(n, "updated_at"),
                GetString(subject, "html_url") ?? GetString(subject, "url"),
                GetBool(n, "unread")));
        }
        return list;
    }

    public async Task<Stream> DownloadArchiveAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        // Gitea's archive endpoint requires an explicit ref, so resolve the default branch first.
        string branch;
        using (var doc = await GetJsonAsync($"repos/{repo.Owner}/{repo.Name}", ct).ConfigureAwait(false))
            branch = GetString(doc.RootElement, "default_branch") ?? "main";

        var resp = await http.GetAsync($"repos/{repo.Owner}/{repo.Name}/archive/{branch}.zip", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    public async Task<GitUserInfo> ValidateAndGetUserAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("user", ct).ConfigureAwait(false);
        return new GitUserInfo(GetString(doc.RootElement, "login") ?? "?");
    }

    public async Task<IReadOnlyList<AccessibleRepo>> ListAccessibleReposAsync(CancellationToken ct = default)
    {
        var list = new List<AccessibleRepo>();
        // 50 x 40 = up to 2000 repos, matching the GitHub path. The loop breaks on the first
        // short page, so the higher ceiling costs nothing for smaller servers.
        for (var page = 1; page <= 40; page++)
        {
            using var doc = await GetJsonAsync($"user/repos?page={page}&limit=50", ct).ConfigureAwait(false);
            var count = 0;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                count++;
                list.Add(new AccessibleRepo(
                    GetString(GetProp(r, "owner"), "login") ?? "?",
                    GetString(r, "name") ?? "",
                    GetString(r, "description"),
                    GetBool(r, "private")));
            }
            if (count < 50)
                break; // last page
        }
        return list;
    }

    public async Task<GitRepoInfo?> GetRepoInfoAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync($"repos/{repo.Owner}/{repo.Name}", ct).ConfigureAwait(false);
            return ToInfo(doc.RootElement, repo.Owner, repo.Name);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        var names = new List<string>();
        for (var page = 1; page <= 5; page++)
        {
            using var doc = await GetJsonAsync($"repos/{repo.Owner}/{repo.Name}/branches?page={page}&limit=50", ct).ConfigureAwait(false);
            var count = 0;
            foreach (var b in doc.RootElement.EnumerateArray())
            {
                count++;
                if (GetString(b, "name") is { Length: > 0 } name)
                    names.Add(name);
            }
            if (count < 50)
                break; // last page
        }
        return names;
    }

    public async Task<GitRepoInfo> CreateRepoAsync(string owner, string name, string? description, bool isPrivate, CancellationToken ct = default)
    {
        // Gitea, like GitHub, splits creation between the authenticated user and orgs.
        var me = await ValidateAndGetUserAsync(ct).ConfigureAwait(false);
        var url = string.Equals(me.Login, owner, StringComparison.OrdinalIgnoreCase)
            ? "user/repos"
            : $"orgs/{owner}/repos";

        using var doc = await SendJsonAsync(HttpMethod.Post, url, w =>
        {
            w.WriteString("name", name);
            if (!string.IsNullOrWhiteSpace(description))
                w.WriteString("description", description);
            w.WriteBoolean("private", isPrivate);
            // See IGitProvider.CreateRepoAsync — an auto-initialised repo can't be fast-forwarded to.
            w.WriteBoolean("auto_init", false);
        }, ct).ConfigureAwait(false);

        return ToInfo(doc.RootElement, owner, name);
    }

    public async Task SetDefaultBranchAsync(MonitoredRepo repo, string branch, CancellationToken ct = default)
    {
        using var _ = await SendJsonAsync(
            HttpMethod.Patch,
            $"repos/{repo.Owner}/{repo.Name}",
            w => w.WriteString("default_branch", branch),
            ct).ConfigureAwait(false);
    }

    static GitRepoInfo ToInfo(JsonElement r, string fallbackOwner, string fallbackName) => new(
        GetString(GetProp(r, "owner"), "login") ?? fallbackOwner,
        GetString(r, "name") ?? fallbackName,
        GetString(r, "description"),
        GetBool(r, "private"),
        GetString(r, "default_branch") is { Length: > 0 } b ? b : "main",
        GetString(r, "clone_url") ?? "",
        GetPushedAt(r));

    async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        var resp = await http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, relativeUrl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST/PATCH with a body written by <paramref name="writeBody"/>. Bodies are built with a
    /// <see cref="Utf8JsonWriter"/> rather than a serializer so no JsonTypeInfo/reflection is
    /// needed — same AOT/trim-safe approach as the JsonDocument reads above.
    /// </summary>
    async Task<JsonDocument> SendJsonAsync(HttpMethod method, string relativeUrl, Action<Utf8JsonWriter> writeBody, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        using var request = new HttpRequestMessage(method, relativeUrl)
        {
            Content = new ByteArrayContent(buffer.ToArray())
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, relativeUrl, ct).ConfigureAwait(false);
    }

    static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp, string relativeUrl, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Gitea {(int)resp.StatusCode} {resp.StatusCode} for {relativeUrl}: {Truncate(body)}", null, resp.StatusCode);
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // 204 No Content (some Gitea PATCH responses) parses as an empty document rather than throwing.
        if (stream.CanSeek && stream.Length == 0)
            return JsonDocument.Parse("{}");
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    static string Truncate(string s) => s.Length <= 200 ? s : s[..200];

    // ---- JSON pluck helpers (null/missing tolerant) ----
    static JsonElement GetProp(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    static string? GetString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    static int GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    static long GetLong(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    static bool GetBool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            && v.GetBoolean();

    static DateTimeOffset GetDate(JsonElement e, string name)
        => DateTimeOffset.TryParse(GetString(e, name), out var d) ? d : DateTimeOffset.UtcNow;

    /// <summary>
    /// Like <see cref="GetDate"/> but null when the field is absent or unparseable. GetDate's
    /// "now" fallback would read as "just pushed" to the sync list's behind check.
    /// </summary>
    static DateTimeOffset? GetDateOrNull(JsonElement e, string name)
        => DateTimeOffset.TryParse(GetString(e, name), out var d) ? d : null;

    /// <summary>
    /// Gitea has no dedicated pushed_at (Forgejo and newer builds sometimes do), so fall back to
    /// updated_at. That also moves on non-push edits like a description change, which can only
    /// make the sync list read "behind" when it isn't — never the reverse.
    /// </summary>
    static DateTimeOffset? GetPushedAt(JsonElement r)
        => GetDateOrNull(r, "pushed_at") ?? GetDateOrNull(r, "updated_at");

    static InboxReason MapReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "mention" => InboxReason.Mention,
        "assign" => InboxReason.Assigned,
        "review_requested" => InboxReason.ReviewRequested,
        "author" => InboxReason.Author,
        "comment" => InboxReason.Comment,
        "subscribed" => InboxReason.Subscribed,
        _ => InboxReason.Other
    };
}
