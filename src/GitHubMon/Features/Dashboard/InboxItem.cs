namespace GitHubMon.Dashboard;

public enum InboxReason
{
    Mention,
    Assigned,
    ReviewRequested,
    Author,
    Comment,
    Subscribed,
    Other
}

public sealed record InboxItem(
    string AccountId,
    string ThreadId,
    string RepoFullName,
    string Title,
    InboxReason Reason,
    string Type,
    DateTimeOffset UpdatedAt,
    string? Url,
    bool Unread);
