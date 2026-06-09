using CommunityToolkit.Mvvm.ComponentModel;

namespace GitHubMon.Accounts;

public sealed partial class RepoSelection : ObservableObject
{
    [ObservableProperty] bool isSelected;

    public required string Owner { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Private { get; init; }

    // Whether this repo was returned by the GitHub API for the current token. False
    // means the repo is already saved on the account but the current token's scope
    // doesn't grant visibility to it — we keep it pre-selected so Save preserves it.
    public bool IsVisibleToToken { get; init; }

    public string FullName => $"{Owner}/{Name}";
    public string PrivacyLabel => Private ? "Private" : "Public";
}
