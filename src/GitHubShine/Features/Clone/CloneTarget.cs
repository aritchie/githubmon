using System.Text;

namespace GitHubShine.Clone;

/// <summary>
/// One working copy the clone page keeps: a repo, the branch it should be on, and the folder it
/// lives in. The same repo can appear any number of times — once per branch, say — as long as
/// each entry has a folder of its own. Stored in the DocumentDb next to the accounts, so a
/// database backup restored onto a freshly wiped machine brings the whole folder layout back,
/// and one "Clone / update" recreates it.
/// </summary>
/// <param name="AccountId">The monitored account whose token reads the repo.</param>
/// <param name="Branch">
/// The branch this working copy should be on. Null means the repo's default branch, and is
/// replaced with that branch's actual name once the first clone has found out what it is.
/// Changing it switches the working copy over; a run also switches back to it when the folder
/// has been left on some other branch, provided nothing uncommitted would come along.
/// </param>
/// <param name="Directory">
/// Where the working copy lives. A relative path is resolved against the clone root
/// (<see cref="ClonePrefs.RootDirectory"/>, or <see cref="AppPaths.CloneDirectory"/> on mobile),
/// which is what keeps a restored setup usable on a machine whose home folder is named
/// differently. An absolute path is used as it is (desktop only).
/// </param>
public sealed record CloneTarget(
    string Id,
    string AccountId,
    MonitoredRepo Repo,
    string? Branch,
    string Directory)
{
    /// <summary>The folder this entry's working copy lives in, given the clone root.</summary>
    public string ResolvePath(string? rootDirectory)
    {
        if (Path.IsPathRooted(this.Directory))
            return this.Directory;

        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new InvalidOperationException("Choose a folder to clone into first.");

        return Path.GetFullPath(Path.Combine(rootDirectory, this.Directory));
    }

    /// <summary>
    /// The folder, relative to the clone root, offered for a new entry: the layout's usual
    /// <c>owner/name</c> or <c>name</c>, with the branch appended when that's already taken — which
    /// is what adding the same repo a second time for another branch runs into.
    /// </summary>
    public static string SuggestDirectory(MonitoredRepo repo, CloneLayout layout, string? branch, Func<string, bool> taken)
    {
        var name = Sanitize(repo.Name);
        // Always '/', which Windows accepts too, so a backup restored on another OS still resolves.
        var plain = layout == CloneLayout.NameOnly ? name : $"{Sanitize(repo.Owner)}/{name}";
        if (!taken(plain))
            return plain;

        var suffix = string.IsNullOrWhiteSpace(branch) ? "copy" : Sanitize(branch.Replace('/', '-'));
        var candidate = $"{plain}-{suffix}";
        for (var n = 2; taken(candidate); n++)
            candidate = $"{plain}-{suffix}-{n}";
        return candidate;
    }

    /// <summary>Folder-safe form of an owner, repo or branch name — all can legally contain a dot.</summary>
    static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) ? '-' : c);

        // A name of "." or ".." would resolve to the parent folder rather than a child of it.
        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 || cleaned.All(c => c == '.') ? "-" : cleaned;
    }
}
