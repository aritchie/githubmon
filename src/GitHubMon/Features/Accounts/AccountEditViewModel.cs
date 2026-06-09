using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using Shiny;

namespace GitHubMon.Accounts;

[ShellMap<AccountEditPage>("AccountEdit")]
public sealed partial class AccountEditViewModel(
    ITokenVault vault,
    IConfigStore config,
    IGitHubClientFactory factory,
    INavigator navigator) : ObservableObject
{
    string? originalId;
    List<string> existingRepoFullNames = new();

    [ObservableProperty] string label = "";
    [ObservableProperty] string token = "";
    [ObservableProperty] string? validationMessage;
    [ObservableProperty] string? validatedUsername;
    [ObservableProperty] bool isBusy;
    [ObservableProperty] bool isLoadingRepos;
    [ObservableProperty] bool hasFetchedRepos;
    [ObservableProperty] string filterText = "";
    [ObservableProperty] int selectedRepoCount;

    public ObservableCollection<RepoSelection> AvailableRepos { get; } = new();
    public ObservableCollection<RepoSelection> FilteredRepos { get; } = new();

    public bool HasRepos => AvailableRepos.Count > 0;
    public string SelectionSummary =>
        AvailableRepos.Count == 0
            ? ""
            : $"{SelectedRepoCount} selected of {AvailableRepos.Count} available";

    public void Load(MonitoredAccount? account)
    {
        AvailableRepos.Clear();
        FilteredRepos.Clear();
        FilterText = "";
        HasFetchedRepos = false;
        SelectedRepoCount = 0;
        Token = ""; // never display existing token

        if (account is null)
        {
            originalId = null;
            Label = "";
            ValidatedUsername = null;
            ValidationMessage = null;
            existingRepoFullNames = new();
            return;
        }
        originalId = account.Id;
        Label = account.Label;
        ValidatedUsername = account.Username;
        ValidationMessage = null;
        existingRepoFullNames = account.Repos.Select(r => r.FullName).ToList();
    }

    partial void OnFilterTextChanged(string value) => RebuildFiltered();

    void RebuildFiltered()
    {
        FilteredRepos.Clear();
        IEnumerable<RepoSelection> source = AvailableRepos;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            source = source.Where(r =>
                r.FullName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (r.Description?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        foreach (var r in source) FilteredRepos.Add(r);
    }

    void OnRepoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoSelection.IsSelected))
            RecalculateSelectedCount();
    }

    void RecalculateSelectedCount()
    {
        SelectedRepoCount = AvailableRepos.Count(r => r.IsSelected);
        OnPropertyChanged(nameof(SelectionSummary));
    }

    [RelayCommand]
    async Task ValidateAsync()
    {
        if (string.IsNullOrWhiteSpace(Token)) { ValidationMessage = "Enter a token first"; return; }
        IsBusy = true;
        try
        {
            var client = new GitHubClient(new ProductHeaderValue("GitHubMon-Validate"))
            {
                Credentials = new Credentials(Token)
            };
            var user = await client.User.Current();
            ValidatedUsername = user.Login;
            ValidationMessage = $"Authenticated as @{user.Login}";

            await FetchReposAsync(client);
        }
        catch (Exception ex)
        {
            ValidatedUsername = null;
            ValidationMessage = $"Failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    async Task FetchReposAsync(GitHubClient client)
    {
        IsLoadingRepos = true;
        try
        {
            // Reset and rebuild from the API response. Existing selections from a
            // prior fetch attempt are discarded; existing-monitored repos that the
            // token can't see are preserved as not-visible entries below.
            foreach (var old in AvailableRepos) old.PropertyChanged -= OnRepoPropertyChanged;
            AvailableRepos.Clear();
            FilteredRepos.Clear();

            var existing = new HashSet<string>(existingRepoFullNames, StringComparer.OrdinalIgnoreCase);
            var fetched = await client.Repository.GetAllForCurrent(new ApiOptions
            {
                PageSize = 100,
                PageCount = 5,    // up to 500 repos — enough for any fine-grained PAT
                StartPage = 1
            });

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in fetched.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var sel = new RepoSelection
                {
                    Owner = r.Owner.Login,
                    Name = r.Name,
                    Description = r.Description,
                    Private = r.Private,
                    IsVisibleToToken = true,
                    IsSelected = existing.Contains(r.FullName)
                };
                sel.PropertyChanged += OnRepoPropertyChanged;
                AvailableRepos.Add(sel);
                seen.Add(r.FullName);
            }

            // Preserve already-monitored repos that the token can't see — keep them
            // checked so Save doesn't silently drop them.
            foreach (var fn in existingRepoFullNames.Where(n => !seen.Contains(n)))
            {
                var parts = fn.Split('/', 2);
                if (parts.Length != 2) continue;
                var sel = new RepoSelection
                {
                    Owner = parts[0],
                    Name = parts[1],
                    IsVisibleToToken = false,
                    IsSelected = true
                };
                sel.PropertyChanged += OnRepoPropertyChanged;
                AvailableRepos.Add(sel);
            }

            HasFetchedRepos = true;
            OnPropertyChanged(nameof(HasRepos));
            RebuildFiltered();
            RecalculateSelectedCount();
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Authenticated, but couldn't list repos: {ex.Message}";
        }
        finally { IsLoadingRepos = false; }
    }

    [RelayCommand]
    void SelectAllVisible()
    {
        foreach (var r in FilteredRepos) r.IsSelected = true;
    }

    [RelayCommand]
    void ClearSelection()
    {
        foreach (var r in AvailableRepos) r.IsSelected = false;
    }

    [RelayCommand]
    void ToggleRepo(RepoSelection? repo)
    {
        if (repo is null) return;
        repo.IsSelected = !repo.IsSelected;
    }

    [RelayCommand]
    async Task SaveAsync()
    {
        // Set this FIRST so user sees the click registered even if validation bails.
        ValidationMessage = "Saving…";
        if (string.IsNullOrWhiteSpace(Label)) { ValidationMessage = "Label required"; return; }
        IsBusy = true;
        try
        {
            var id = originalId ?? Guid.NewGuid().ToString("n");
            var repos = AvailableRepos
                .Where(r => r.IsSelected)
                .Select(r => new MonitoredRepo(r.Owner, r.Name))
                .ToList();
            var account = new MonitoredAccount(id, Label.Trim(), ValidatedUsername, repos);
            await config.UpsertAccountAsync(account);
            if (!string.IsNullOrWhiteSpace(Token))
                await vault.SetTokenAsync(id, Token);
            factory.Invalidate(id);
            ValidationMessage = $"Saved {repos.Count} repo(s).";
            try { await navigator.NavigateTo<AccountsViewModel>(relativeNavigation: false); }
            catch (Exception navEx) { ValidationMessage = $"Saved, but couldn't switch tab: {navEx.Message}"; }
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Save failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    Task CancelAsync() => navigator.NavigateTo<AccountsViewModel>(relativeNavigation: false);
}
