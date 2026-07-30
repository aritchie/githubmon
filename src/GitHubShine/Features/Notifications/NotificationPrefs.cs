namespace GitHubShine.Notifications;

/// <summary>
/// The alert kinds a user can switch on and off independently. Append-only — the values are
/// never persisted numerically (each maps to a named property on <see cref="NotificationPrefs"/>),
/// but the settings page renders them in declaration order.
/// </summary>
public enum NotificationCategory
{
    PullRequests,
    Issues,
    Stars,
    Forks,
    Builds,
    Inbox
}

/// <summary>
/// Which notification categories are live, plus the global mute. Lives in the document store
/// rather than the key/value prefs store — the desktop heads' key/value store is in-memory, so
/// anything written there wouldn't survive a restart, and a raw SQLite backup carries this
/// along with the accounts it applies to. Single row, fixed id.
/// </summary>
public sealed record NotificationPrefs(
    string Id,
    bool Muted = false,
    bool PullRequests = true,
    bool Issues = true,
    bool Stars = true,
    bool Forks = true,
    bool Builds = true,
    bool Inbox = true)
{
    public const string DefaultId = "default";

    public static NotificationPrefs Default => new(DefaultId);

    /// <summary>True when this category should actually post — i.e. it's on AND nothing is muted.</summary>
    public bool IsEnabled(NotificationCategory category) => !this.Muted && this.IsCategoryOn(category);

    /// <summary>The category's own switch, ignoring the global mute — this is what the settings UI binds.</summary>
    public bool IsCategoryOn(NotificationCategory category) => category switch
    {
        NotificationCategory.PullRequests => this.PullRequests,
        NotificationCategory.Issues => this.Issues,
        NotificationCategory.Stars => this.Stars,
        NotificationCategory.Forks => this.Forks,
        NotificationCategory.Builds => this.Builds,
        NotificationCategory.Inbox => this.Inbox,
        // A category added after this row was written has no column yet: default it to audible
        // rather than silently swallowing a new alert kind.
        _ => true
    };

    public NotificationPrefs With(NotificationCategory category, bool enabled) => category switch
    {
        NotificationCategory.PullRequests => this with { PullRequests = enabled },
        NotificationCategory.Issues => this with { Issues = enabled },
        NotificationCategory.Stars => this with { Stars = enabled },
        NotificationCategory.Forks => this with { Forks = enabled },
        NotificationCategory.Builds => this with { Builds = enabled },
        NotificationCategory.Inbox => this with { Inbox = enabled },
        _ => this
    };
}

/// <summary>Display strings for the settings page and anywhere else a category is named.</summary>
public static class NotificationCategoryInfo
{
    public static IReadOnlyList<NotificationCategory> All { get; } = Enum.GetValues<NotificationCategory>();

    public static string Label(NotificationCategory category) => category switch
    {
        NotificationCategory.PullRequests => "New pull requests",
        NotificationCategory.Issues => "New issues",
        NotificationCategory.Stars => "New stars",
        NotificationCategory.Forks => "New forks",
        NotificationCategory.Builds => "Build status",
        NotificationCategory.Inbox => "Mentions & review requests",
        _ => category.ToString()
    };

    public static string Description(NotificationCategory category) => category switch
    {
        NotificationCategory.PullRequests =>
            "A pull request was opened on a monitored repository.",
        NotificationCategory.Issues =>
            "The open-issue count went up. Reported as a count — the repository summary doesn't carry the titles.",
        NotificationCategory.Stars =>
            "Someone starred a monitored repository. Reported as a count of new stars since the last refresh.",
        NotificationCategory.Forks =>
            "A monitored repository was forked. Reported as a count of new forks since the last refresh.",
        NotificationCategory.Builds =>
            "A workflow run failed, and when a previously failing workflow goes green again.",
        NotificationCategory.Inbox =>
            "You were mentioned, assigned, or asked to review. Other inbox activity stays silent.",
        _ => string.Empty
    };
}
