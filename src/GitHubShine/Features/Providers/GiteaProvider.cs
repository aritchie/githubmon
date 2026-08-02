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
public sealed class GiteaProvider(HttpClient client, string? hostUrl, string token, string accountId) : IGitProvider
{
    /// <summary>
    /// Items per page. Gitea's default <c>MAX_RESPONSE_ITEMS</c> is 50 and it silently clamps
    /// anything larger, which would make a "short page means last page" loop stop after one
    /// page — so ask for exactly what it will give.
    /// </summary>
    const int PageSize = 50;

    readonly HttpClient http = Configure(client, hostUrl, token);

    /// <summary>
    /// Points the injected client at <c>{host}/api/v1/</c> and attaches the token. Unlike GitHub,
    /// there is no default host to fall back on — a Gitea account without one can't be used at all.
    /// </summary>
    static HttpClient Configure(HttpClient client, string? hostUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(hostUrl))
            throw new InvalidOperationException("A Gitea account requires a host URL (e.g. https://gitea.com).");

        client.BaseAddress = new Uri(hostUrl.TrimEnd('/') + "/api/v1/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubShine/1.0");
        return client;
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
            GetPushedAt(root),
            GetBool(root, "private"));
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
                return [];

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
            return [];
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

    public async Task<GitPersonProfile?> GetPersonAsync(string login, CancellationToken ct = default)
    {
        // Gitea stores organisations as a flavour of user, so /users/{login} usually answers for
        // both — but the org payload carries the fields we want (description, website) under
        // different names and without the follower counters. Probe /orgs first: a hit settles the
        // kind outright, and a miss costs one 404 on a flow the user triggered by hand.
        using (var org = await GetJsonOrNullAsync($"orgs/{login}", ct).ConfigureAwait(false))
        {
            if (org is not null)
            {
                var o = org.RootElement;
                return new GitPersonProfile(
                    GetString(o, "username") ?? login,
                    GitPersonKind.Organization,
                    GetString(o, "full_name"),
                    GetString(o, "avatar_url"),
                    GetString(o, "description"),
                    GetString(o, "location"),
                    // Gitea organisations have no company or gists, and expose no creation date
                    // or follower counts. Null so the grid shows "—" rather than a fake zero.
                    null,
                    GetString(o, "website"),
                    null,
                    null,
                    // Filled in by GetPersonSnapshotAsync from the repo listing it pages anyway.
                    null,
                    null,
                    null);
            }
        }

        using var user = await GetJsonOrNullAsync($"users/{login}", ct).ConfigureAwait(false);
        if (user is null)
            return null;

        var u = user.RootElement;
        return new GitPersonProfile(
            GetString(u, "login") ?? GetString(u, "username") ?? login,
            GitPersonKind.User,
            GetString(u, "full_name"),
            GetString(u, "avatar_url"),
            GetString(u, "description"),
            GetString(u, "location"),
            null, // no company field on a Gitea user
            GetString(u, "website"),
            GetInt(u, "followers_count"),
            GetInt(u, "following_count"),
            null, // no repo count on the payload — the star listing below supplies it
            null, // Gitea has no gists
            GetDateOrNull(u, "created"));
    }

    public async Task<GitPersonSnapshotData?> GetPersonSnapshotAsync(string login, GitPersonKind kind, CancellationToken ct = default)
    {
        var profile = await GetPersonAsync(login, ct).ConfigureAwait(false);
        if (profile is null)
            return null;

        // The repo listing has to be paged for the star sum regardless, so take the repo count
        // off the same pass — Gitea publishes neither number on the profile payload.
        var stars = await SumStarsAsync(login, profile.Kind, ct).ConfigureAwait(false);

        var members = profile.Kind == GitPersonKind.Organization
            ? await CountMembersAsync(login, ct).ConfigureAwait(false)
            : null;

        return new GitPersonSnapshotData(
            profile with { PublicRepos = stars.Repos },
            stars.Stars,
            members?.Total,
            stars.Truncated,
            members?.Truncated ?? false);
    }

    /// <summary>A tally over a capped listing, and whether the cap cut it short.</summary>
    readonly record struct PagedTally(int Total, bool Truncated);

    async Task<(int Stars, int Repos, bool Truncated)> SumStarsAsync(string login, GitPersonKind kind, CancellationToken ct)
    {
        var root = kind == GitPersonKind.Organization ? $"orgs/{login}/repos" : $"users/{login}/repos";
        var stars = 0;
        var repos = 0;

        for (var page = 1; page <= GitPersonLimits.MaxRepos / PageSize; page++)
        {
            using var doc = await GetJsonAsync($"{root}?page={page}&limit={PageSize}", ct).ConfigureAwait(false);
            var count = 0;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                count++;
                stars += GetInt(r, "stars_count");
            }
            repos += count;
            if (count < PageSize)
                return (stars, repos, false); // ran out before the cap — a complete tally
        }
        // Every page came back full, so the cap — not the data — is what stopped us.
        return (stars, repos, true);
    }

    async Task<PagedTally?> CountMembersAsync(string login, CancellationToken ct)
    {
        var members = 0;
        for (var page = 1; page <= GitPersonLimits.MaxMembers / PageSize; page++)
        {
            // A token that can't read the member list gets 403 — "unknown", not "no members".
            var doc = await GetJsonOrNullAsync($"orgs/{login}/members?page={page}&limit={PageSize}", ct)
                .ConfigureAwait(false);
            if (doc is null)
                return null;

            using (doc)
            {
                var count = doc.RootElement.EnumerateArray().Count();
                members += count;
                if (count < PageSize)
                    return new PagedTally(members, false);
            }
        }
        return new PagedTally(members, true);
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
    /// Like <see cref="GetJsonAsync"/> but null for the two "you can't have this" statuses instead
    /// of throwing. Both are expected answers on the person lookups: a 404 is how the org probe
    /// says "that login is a user", and a 403 is a token without permission to read a member list.
    /// Every other failure still throws — a 500 must not be mistaken for an absent resource.
    /// </summary>
    async Task<JsonDocument?> GetJsonOrNullAsync(string relativeUrl, CancellationToken ct)
    {
        var resp = await http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            resp.Dispose();
            return null;
        }
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
