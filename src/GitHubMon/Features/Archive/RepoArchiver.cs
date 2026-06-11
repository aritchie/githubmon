using Octokit;
using Shiny;

namespace GitHubMon.Archive;

public interface IRepoArchiver
{
    /// <summary>
    /// Downloads a zip of the repo's default branch (the "main" branch) into
    /// <paramref name="destinationFolder"/> and returns the written file path.
    /// </summary>
    Task<string> ArchiveAsync(MonitoredAccount account, MonitoredRepo repo, string destinationFolder, CancellationToken ct = default);
}

[Singleton]
public sealed class RepoArchiver(IGitHubClientFactory factory) : IRepoArchiver
{
    public async Task<string> ArchiveAsync(MonitoredAccount account, MonitoredRepo repo, string destinationFolder, CancellationToken ct = default)
    {
        var client = await factory.CreateAsync(account.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No access token configured for this account.");

        // An empty reference makes GitHub serve the repo's default branch, which
        // is exactly what we want and avoids assuming the branch is named "main".
        // A generous timeout covers large histories; the archive is a single download.
        var bytes = await client.Repository.Content
            .GetArchive(repo.Owner, repo.Name, ArchiveFormat.Zipball, reference: string.Empty, timeout: TimeSpan.FromMinutes(10))
            .ConfigureAwait(false);

        Directory.CreateDirectory(destinationFolder);
        var path = Path.Combine(destinationFolder, $"{repo.Owner}-{repo.Name}.zip");
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return path;
    }
}
