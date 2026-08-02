using GitHubShine.Providers;

namespace GitHubShine.Persons;

/// <summary>
/// Per-login display state for the people grid. Mutable on purpose, like
/// <see cref="RepoCardModel"/>: the page folds each snapshot in via <see cref="Apply"/>, which
/// reports whether a rendered field moved so an unchanged poll costs no render.
/// </summary>
public sealed class PersonCardModel(MonitoredAccount account, FollowedPerson person)
{
    public MonitoredAccount Account { get; } = account;
    public FollowedPerson Person { get; } = person;

    // Read off the property, not the primary-ctor parameter: capturing `person` here as well as
    // using it in the initializers below is CS9124 (two copies of the same value on the instance).
    public string Key => Person.Id;
    public string Login => Person.Login;
    public string AccountLabel => Account.Label;

    /// <summary>
    /// Kind as a sortable label. Grouping the organisations together is the point of sorting on
    /// this column, and two values sort fine as text.
    /// </summary>
    public string TypeLabel { get; private set; } =
        person.Kind == GitPersonKind.Organization ? "Organization" : "User";

    public bool IsOrganization { get; private set; } = person.Kind == GitPersonKind.Organization;

    public string? DisplayName { get; private set; } = person.DisplayName;
    public string? AvatarUrl { get; private set; }
    public string? Bio { get; private set; }
    public string? Location { get; private set; }
    public string? Company { get; private set; }
    public string? WebsiteUrl { get; private set; }

    public int? Followers { get; private set; }
    public int? Following { get; private set; }
    public int? PublicRepos { get; private set; }
    public int? Gists { get; private set; }
    public int TotalStars { get; private set; }
    public int? Members { get; private set; }
    public DateTimeOffset? JoinedAt { get; private set; }

    /// <summary>
    /// The listing behind the number stopped at its cap, so the figure is a floor. Rendered as a
    /// trailing "+" — see GitPersonLimits for the ceilings and why they exist.
    /// </summary>
    public bool StarsTruncated { get; private set; }
    public bool MembersTruncated { get; private set; }

    public DateTimeOffset? LastUpdated { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool HasError { get; private set; }

    /// <summary>True until the first snapshot lands, so the row can say "loading" not "zero".</summary>
    public bool IsPending { get; private set; } = true;

    public string ProfileUrl => $"{Account.WebBaseUrl}/{Login}";
    public string ReposUrl => $"{ProfileUrl}?tab=repositories";
    public string FollowersUrl => $"{ProfileUrl}?tab=followers";
    public string FollowingUrl => $"{ProfileUrl}?tab=following";

    /// <summary>
    /// The organisation's member list. The two backends put it at different paths, and this is
    /// the only person link that isn't shared, so it's the one place that branches on provider.
    /// </summary>
    public string MembersUrl => Account.ProviderType == GitProviderType.Gitea
        ? $"{Account.WebBaseUrl}/org/{Login}/members"
        : $"{Account.WebBaseUrl}/orgs/{Login}/people";

    /// <summary>Hover text for the name cell — whatever profile detail is actually known.</summary>
    public string Tooltip
    {
        get
        {
            var parts = new List<string>();
            if (DisplayName is { Length: > 0 })
                parts.Add(DisplayName);
            if (Bio is { Length: > 0 })
                parts.Add(Bio);
            if (Company is { Length: > 0 })
                parts.Add(Company);
            return parts.Count == 0 ? Login : string.Join(" • ", parts);
        }
    }

    // Everything the grid renders for this row. LastUpdated is excluded on purpose: it isn't
    // shown, so a poll that only moved the fetch timestamp must not count as a change.
    (string, bool, string?, string?, string?, string?, string?, int?, int?, int?, int?, int, int?, DateTimeOffset?, bool, string?, bool, bool, bool) RenderSignature()
        => (TypeLabel, IsOrganization, DisplayName, AvatarUrl, Bio, Location, Company,
            Followers, Following, PublicRepos, Gists, TotalStars, Members, JoinedAt,
            HasError, ErrorMessage, IsPending, StarsTruncated, MembersTruncated);

    /// <summary>
    /// Folds a fresh snapshot in. Returns true only when a rendered field actually changed —
    /// the poll re-reads every followed login each cycle even when nothing moved, and
    /// re-rendering on an identical snapshot ships a wasted render batch into the WebView
    /// for the life of the app.
    /// </summary>
    public bool Apply(PersonSnapshot snap)
    {
        var before = RenderSignature();

        ErrorMessage = snap.ErrorMessage;
        HasError = snap.HasError;
        LastUpdated = snap.FetchedAt;
        IsPending = false;

        if (snap.HasError)
            return RenderSignature() != before;

        // A login can change hands between a person and an organisation, so the kind is taken
        // from the snapshot rather than the stored row (which the poll also rewrites).
        IsOrganization = snap.IsOrganization;
        TypeLabel = snap.IsOrganization ? "Organization" : "User";

        DisplayName = snap.DisplayName;
        AvatarUrl = snap.AvatarUrl;
        Bio = snap.Bio;
        Location = snap.Location;
        Company = snap.Company;
        WebsiteUrl = snap.WebsiteUrl;

        Followers = snap.Followers;
        Following = snap.Following;
        PublicRepos = snap.PublicRepos;
        Gists = snap.PublicGists;
        TotalStars = snap.TotalStars;
        Members = snap.MemberCount;
        JoinedAt = snap.JoinedAt;
        StarsTruncated = snap.StarsTruncated;
        MembersTruncated = snap.MembersTruncated;

        return RenderSignature() != before;
    }
}
