using Octokit;
using Octokit.Internal;

namespace GitHubShine.Providers;

/// <summary>
/// GitHub-backed provider. Wraps a single Octokit <see cref="GitHubClient"/>; the
/// calls here are the ones previously inlined in the snapshot/inbox handlers, the
/// repo archiver, and the account-edit validate flow. A non-null <c>hostUrl</c>
/// targets a GitHub Enterprise server; null uses github.com.
/// </summary>
public sealed class GitHubProvider(
    string? hostUrl,
    string token,
    string accountId,
    RateLimitMonitor rateLimits,
    ILogger etagLogger) : IGitProvider
{
    static readonly ProductHeaderValue Product = new("GitHubShine", "1.0");

    /// <summary>GitHub's maximum items per page — the fewest round-trips per capped listing.</summary>
    const int PageSize = 100;

    readonly GitHubClient client = BuildClient(hostUrl, token, rateLimits, etagLogger);

    /// <summary>
    /// Builds the Octokit connection over a <see cref="ConditionalGetHandler"/> so every request
    /// carries If-None-Match and unchanged resources come back as free 304s (see the handler for
    /// why that matters to the rate limit). Octokit has no built-in ETag support, so we own the
    /// HTTP pipeline here rather than using the convenience GitHubClient constructors.
    /// </summary>
    static GitHubClient BuildClient(string? hostUrl, string token, RateLimitMonitor rateLimits, ILogger etagLogger)
    {
        // One handler, captured by the adapter's factory — a fresh instance per request would
        // throw away the ETag cache that makes the 304s possible.
        var handler = new ConditionalGetHandler(new HttpClientHandler(), rateLimits, etagLogger);
        return new GitHubClient(new Connection(
            Product,
            string.IsNullOrWhiteSpace(hostUrl) ? GitHubClient.GitHubApiUrl : new Uri(hostUrl),
            new InMemoryCredentialStore(new Credentials(token)),
            new HttpClientAdapter(() => handler),
            new SimpleJsonSerializer()));
    }

    public async Task<RepoSnapshotData> GetRepoSnapshotAsync(MonitoredRepo repo, int runCount, CancellationToken ct = default)
    {
        // One fan-out of three calls: repo, open PRs, recent runs. The PR list is reused for both
        // the issue-count math (GitHub's OpenIssuesCount includes PRs, so subtract them) and the
        // PR summaries — that's one fewer call per repo per poll than fetching PRs twice.
        var repoTask = client.Repository.Get(repo.Owner, repo.Name);
        var prsTask = client.PullRequest.GetAllForRepository(repo.Owner, repo.Name, new PullRequestRequest
        {
            State = ItemStateFilter.Open
        });
        var runsTask = client.Actions.Workflows.Runs
            .List(repo.Owner, repo.Name, new WorkflowRunsRequest(), new ApiOptions { PageSize = runCount, PageCount = 1 });
        await Task.WhenAll(repoTask, prsTask, runsTask).ConfigureAwait(false);

        var r = repoTask.Result;
        var prs = prsTask.Result;

        var stats = new RepoStats(
            Math.Max(0, r.OpenIssuesCount - prs.Count),
            r.StargazersCount,
            r.ForksCount,
            r.SubscribersCount,
            r.PushedAt,
            r.Private);

        var prSummaries = prs
            .Select(p => new PullRequestSummary(
                p.Number,
                p.Title,
                p.User?.Login ?? "?",
                p.HtmlUrl,
                p.Draft,
                p.Mergeable ?? false))
            .ToArray();

        var runSummaries = runsTask.Result.WorkflowRuns
            .Select(run => new WorkflowRunSummary(
                run.Id,
                run.Name ?? "(workflow)",
                run.HeadBranch ?? "?",
                run.Status.ToString() ?? "unknown",
                run.Conclusion?.ToString(),
                run.CreatedAt,
                run.HtmlUrl ?? string.Empty))
            .ToArray();

        return new RepoSnapshotData(stats, prSummaries, runSummaries);
    }

    public async Task<IReadOnlyList<InboxItem>> GetInboxAsync(CancellationToken ct = default)
    {
        var notifications = await client.Activity.Notifications
            .GetAllForCurrent(new NotificationsRequest { All = false, Participating = true })
            .ConfigureAwait(false);

        return notifications
            .Select(n => new InboxItem(
                accountId,
                n.Id,
                n.Repository?.FullName ?? "?",
                n.Subject?.Title ?? "(no title)",
                MapReason(n.Reason),
                n.Subject?.Type ?? "",
                ParseDate(n.UpdatedAt),
                n.Subject?.Url,
                n.Unread))
            .ToArray();
    }

    public async Task<Stream> DownloadArchiveAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        // An empty reference makes GitHub serve the repo's default branch.
        var bytes = await client.Repository.Content
            .GetArchive(repo.Owner, repo.Name, ArchiveFormat.Zipball, reference: string.Empty, timeout: TimeSpan.FromMinutes(10))
            .ConfigureAwait(false);
        return new MemoryStream(bytes, writable: false);
    }

    public async Task<GitUserInfo> ValidateAndGetUserAsync(CancellationToken ct = default)
    {
        var user = await client.User.Current().ConfigureAwait(false);
        return new GitUserInfo(user.Login);
    }

    public async Task<IReadOnlyList<AccessibleRepo>> ListAccessibleReposAsync(CancellationToken ct = default)
    {
        var fetched = await client.Repository
            // 100 x 20 = up to 2000 repos. Octokit follows Link headers and stops as soon as the
            // pages run out, so a generous ceiling costs nothing for smaller accounts — but the
            // old 5-page (500 repo) cap silently truncated the list for accounts in many orgs.
            // Only ever called on demand (account edit / all-repos page), never from the poller.
            .GetAllForCurrent(new ApiOptions { PageSize = 100, PageCount = 20, StartPage = 1 })
            .ConfigureAwait(false);

        return fetched
            .Select(r => new AccessibleRepo(r.Owner.Login, r.Name, r.Description, r.Private))
            .ToArray();
    }

    public async Task<GitPersonProfile?> GetPersonAsync(string login, CancellationToken ct = default)
    {
        try
        {
            // /users/{login} answers for organisations too (with type=Organization), so one
            // endpoint covers both kinds — no "try org, fall back to user" dance needed here.
            return ToProfile(await client.User.Get(login).ConfigureAwait(false));
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<GitPersonSnapshotData?> GetPersonSnapshotAsync(string login, GitPersonKind kind, CancellationToken ct = default)
    {
        var profile = await GetPersonAsync(login, ct).ConfigureAwait(false);
        if (profile is null)
            return null;

        // Fan out: the star sum and the member count are independent, and the member count is
        // skipped entirely for a person. Trust the profile's Kind over the caller's stored one —
        // a login can change hands between a user and an org after it was followed.
        var starsTask = SumStarsAsync(login, profile.Kind);
        var membersTask = profile.Kind == GitPersonKind.Organization
            ? CountMembersAsync(login)
            : Task.FromResult<PagedTally?>(null);
        await Task.WhenAll(starsTask, membersTask).ConfigureAwait(false);

        var stars = starsTask.Result;
        var members = membersTask.Result;

        return new GitPersonSnapshotData(
            profile,
            stars.Total,
            members?.Total,
            stars.Truncated,
            members?.Truncated ?? false);
    }

    /// <summary>
    /// Sums stargazers across the login's repos. GitHub publishes no aggregate star count, so
    /// this is the only way to get one — a paged listing, capped, and cheap in steady state
    /// because the pages come back as ETag 304s while nothing changes.
    /// </summary>
    Task<PagedTally> SumStarsAsync(string login, GitPersonKind kind)
        => PageAsync(
            GitPersonLimits.MaxRepos,
            page => kind == GitPersonKind.Organization
                ? client.Repository.GetAllForOrg(login, page)
                : client.Repository.GetAllForUser(login, page),
            repos => repos.Sum(r => r.StargazersCount));

    async Task<PagedTally?> CountMembersAsync(string login)
    {
        try
        {
            return await PageAsync(
                GitPersonLimits.MaxMembers,
                page => client.Organization.Member.GetAll(login, page),
                members => members.Count).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotFoundException or ForbiddenException)
        {
            // A fine-grained token without org-members read gets 403, and a private membership
            // list reads as 404. Neither means "no members", so report "unknown" instead of 0.
            return null;
        }
    }

    /// <summary>A tally over a capped listing, and whether the cap cut it short.</summary>
    readonly record struct PagedTally(int Total, bool Truncated);

    /// <summary>
    /// Walks a listing one explicitly-numbered page at a time, accumulating
    /// <paramref name="tally"/> over each page and stopping on the first short page or once
    /// <paramref name="maxItems"/> has been read.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>ApiOptions.PageCount</c>, which is how Octokit is normally asked to
    /// fetch several pages. That relies on following the <c>Link</c> response header, and this app's
    /// Apple heads never see it — the platform's native HTTP handler drops <c>Link</c> before it
    /// reaches managed code, so Octokit concludes there is no next page and silently returns only
    /// the first 100 items. Asking for each page by number needs no header at all, so the count
    /// comes out the same on every head.
    /// </remarks>
    async Task<PagedTally> PageAsync<T>(
        int maxItems,
        Func<ApiOptions, Task<IReadOnlyList<T>>> fetchPage,
        Func<IReadOnlyList<T>, int> tally)
    {
        var total = 0;
        var pages = maxItems / PageSize;

        for (var page = 1; page <= pages; page++)
        {
            var batch = await fetchPage(new ApiOptions
            {
                PageSize = PageSize,
                PageCount = 1,
                StartPage = page
            }).ConfigureAwait(false);

            total += tally(batch);

            if (batch.Count < PageSize)
                return new PagedTally(total, false); // ran out before the cap — a complete tally
        }

        // Every page came back full, so the cap — not the data — is what stopped us.
        return new PagedTally(total, true);
    }

    static GitPersonProfile ToProfile(User u) => new(
        u.Login,
        // StringValue rather than the parsed enum: Octokit throws when GitHub introduces an
        // account type it doesn't know, and an unrecognised type is simply "not an org".
        string.Equals(u.Type?.ToString(), "Organization", StringComparison.OrdinalIgnoreCase)
            ? GitPersonKind.Organization
            : GitPersonKind.User,
        u.Name,
        u.AvatarUrl,
        u.Bio,
        u.Location,
        u.Company,
        u.Blog,
        u.Followers,
        u.Following,
        u.PublicRepos,
        u.PublicGists,
        u.CreatedAt);

    public async Task<GitRepoInfo?> GetRepoInfoAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        try
        {
            var r = await client.Repository.Get(repo.Owner, repo.Name).ConfigureAwait(false);
            return ToInfo(r);
        }
        catch (NotFoundException)
        {
            // "Doesn't exist" and "not visible to this token" are indistinguishable on GitHub —
            // both come back as 404. The sync engine treats either as "create it".
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        var branches = await client.Repository.Branch
            .GetAll(repo.Owner, repo.Name, new ApiOptions { PageSize = 100, PageCount = 5 })
            .ConfigureAwait(false);
        return branches.Select(b => b.Name).ToArray();
    }

    public async Task<GitRepoInfo> CreateRepoAsync(string owner, string name, string? description, bool isPrivate, CancellationToken ct = default)
    {
        var newRepo = new NewRepository(name)
        {
            Description = description,
            Private = isPrivate,
            AutoInit = false
        };

        // GitHub splits repo creation across two endpoints — /user/repos for the authenticated
        // user and /orgs/{org}/repos for everything else — so resolve which one this owner is.
        var me = await client.User.Current().ConfigureAwait(false);
        var created = string.Equals(me.Login, owner, StringComparison.OrdinalIgnoreCase)
            ? await client.Repository.Create(newRepo).ConfigureAwait(false)
            : await client.Repository.Create(owner, newRepo).ConfigureAwait(false);

        return ToInfo(created);
    }

    public async Task SetDefaultBranchAsync(MonitoredRepo repo, string branch, CancellationToken ct = default)
        => await client.Repository
            .Edit(repo.Owner, repo.Name, new RepositoryUpdate { DefaultBranch = branch })
            .ConfigureAwait(false);

    static GitRepoInfo ToInfo(Repository r) => new(
        r.Owner.Login,
        r.Name,
        r.Description,
        r.Private,
        string.IsNullOrWhiteSpace(r.DefaultBranch) ? "main" : r.DefaultBranch,
        r.CloneUrl,
        r.PushedAt);

    static DateTimeOffset ParseDate(string? s)
        => DateTimeOffset.TryParse(s, out var d) ? d : DateTimeOffset.UtcNow;

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
