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

    public async Task<RepoStats> GetRepoStatsAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"repos/{repo.Owner}/{repo.Name}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        // Gitea already separates issues from PRs, so open_issues_count is a true
        // issue count — no PR subtraction needed (unlike GitHub).
        return new RepoStats(
            GetInt(root, "open_issues_count"),
            GetInt(root, "stars_count"),
            GetInt(root, "forks_count"),
            GetInt(root, "watchers_count"));
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetOpenPullRequestsAsync(MonitoredRepo repo, CancellationToken ct = default)
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

    public async Task<IReadOnlyList<WorkflowRunSummary>> GetRecentWorkflowRunsAsync(MonitoredRepo repo, int count, CancellationToken ct = default)
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
        // Paginate a few pages like the GitHub path (50 * 10 = up to 500 repos).
        for (var page = 1; page <= 10; page++)
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

    async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        var resp = await http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Gitea {(int)resp.StatusCode} {resp.StatusCode} for {relativeUrl}: {Truncate(body)}", null, resp.StatusCode);
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
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
