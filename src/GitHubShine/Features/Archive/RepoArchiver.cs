using Shiny;

namespace GitHubShine.Archive;

public interface IRepoArchiver
{
    /// <summary>
    /// Downloads a zip of the repo's default branch (the "main" branch) into
    /// <paramref name="destinationFolder"/> and returns the written file path.
    /// </summary>
    Task<string> ArchiveAsync(MonitoredAccount account, MonitoredRepo repo, string destinationFolder, CancellationToken ct = default);
}

[Singleton]
public sealed class RepoArchiver(IGitProviderFactory factory) : IRepoArchiver
{
    public async Task<string> ArchiveAsync(MonitoredAccount account, MonitoredRepo repo, string destinationFolder, CancellationToken ct = default)
    {
        var provider = await factory.CreateAsync(account, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No access token configured for this account.");

        Directory.CreateDirectory(destinationFolder);
        var path = Path.Combine(destinationFolder, $"{repo.Owner}-{repo.Name}.zip");

        await using var source = await provider.DownloadArchiveAsync(repo, ct).ConfigureAwait(false);
        await using var dest = File.Create(path);
        await source.CopyToAsync(dest, ct).ConfigureAwait(false);
        return path;
    }
}
