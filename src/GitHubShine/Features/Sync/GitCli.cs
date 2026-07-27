using System.Diagnostics;
using System.Text;
using Shiny;

namespace GitHubShine.Sync;

/// <summary>HTTPS credentials for a git remote — the account's login plus its personal access token.</summary>
public sealed record GitCredential(string Username, string Token);

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => this.ExitCode == 0;

    /// <summary>The most useful text to show a user — git writes progress and errors to stderr.</summary>
    public string Message
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(this.StdErr) ? this.StdOut : this.StdErr;
            return string.IsNullOrWhiteSpace(text) ? $"git exited with {this.ExitCode}" : text.Trim();
        }
    }
}

/// <summary>Thrown when <c>git</c> is missing or too old to drive a sync.</summary>
public sealed class GitUnavailableException(string message) : Exception(message);

public interface IGitCli
{
    /// <summary>The installed git version, or null when <c>git</c> isn't on PATH. Probed once and cached.</summary>
    Task<Version?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Throws <see cref="GitUnavailableException"/> unless a new enough git is installed.</summary>
    Task EnsureAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs git, optionally authenticating to the remote as <paramref name="credential"/>.
    /// Never throws on a non-zero exit — inspect <see cref="GitResult.Success"/>.
    /// </summary>
    Task<GitResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        GitCredential? credential = null,
        CancellationToken ct = default);
}

/// <summary>
/// Thin <c>git</c> process wrapper for the sync engine.
///
/// Credentials never touch the command line or the repo's config. The token goes into an
/// environment variable and git is told to read the <c>http.extraHeader</c> config value out of
/// that variable with <c>--config-env</c>, so a token can't leak through <c>ps</c>/argv, a
/// shell history, or a <c>.git/config</c> left behind in the cache directory. That flag needs
/// git 2.31 (March 2021), which is the floor enforced by <see cref="EnsureAvailableAsync"/>.
/// </summary>
[Singleton]
public sealed class GitCli(ILogger<GitCli> logger) : IGitCli
{
    // git 2.31 introduced --config-env. See the class remarks for why it's required.
    static readonly Version MinimumVersion = new(2, 31);
    const string AuthEnvVar = "GITHUBSHINE_GIT_AUTH";

    readonly SemaphoreSlim probeLock = new(1, 1);
    bool probed;
    Version? version;

    public async Task<Version?> GetVersionAsync(CancellationToken ct = default)
    {
        if (this.probed)
            return this.version;

        await this.probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!this.probed)
            {
                this.version = await this.ProbeAsync(ct).ConfigureAwait(false);
                this.probed = true;
            }
        }
        finally
        {
            this.probeLock.Release();
        }
        return this.version;
    }

    public async Task EnsureAvailableAsync(CancellationToken ct = default)
    {
        var found = await this.GetVersionAsync(ct).ConfigureAwait(false);
        if (found is null)
            throw new GitUnavailableException("git wasn't found on PATH. Install git (or add it to PATH) to sync repositories.");

        if (found < MinimumVersion)
            throw new GitUnavailableException($"git {found} is too old — {MinimumVersion} or newer is required so credentials can be passed without exposing them on the command line.");
    }

    async Task<Version?> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var result = await this.RunAsync(null, ["--version"], null, ct).ConfigureAwait(false);
            if (!result.Success)
                return null;

            // "git version 2.39.5 (Apple Git-154)" — take the first dotted number.
            var token = result.StdOut
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(t => t.Length > 0 && char.IsDigit(t[0]) && t.Contains('.'));

            // Trim any trailing vendor suffix ("2.39.5.windows.1") down to numeric parts.
            var numeric = string.Join('.', (token ?? "")
                .Split('.')
                .TakeWhile(p => p.Length > 0 && p.All(char.IsDigit))
                .Take(3));

            return Version.TryParse(numeric.Contains('.') ? numeric : numeric + ".0", out var v) ? v : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "git probe failed — treating git as unavailable");
            return null;
        }
    }

    public async Task<GitResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        GitCredential? credential = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        if (credential is not null)
        {
            psi.Environment[AuthEnvVar] = BuildAuthHeader(credential);
            psi.ArgumentList.Add($"--config-env=http.extraHeader={AuthEnvVar}");
        }

        // Never let git block on a prompt or reach for the OS keychain: the empty helper clears
        // the inherited helper list, and the env vars turn interactive fallbacks into fast errors.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("credential.helper=");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // Missing binary (Win32Exception) or a platform with no process support (mobile).
            logger.LogDebug(ex, "Failed to start git");
            return new GitResult(-1, "", ex.Message);
        }

        process.StandardInput.Close();

        // Both pipes must be drained concurrently or a chatty fetch can fill one and deadlock.
        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Kill on cancel, then still wait/drain so the pipes close and no zombie is left behind.
        using (ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Racing a normal exit — nothing to do.
            }
        }))
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        logger.LogDebug("git {Args} exited {Code}", string.Join(' ', args), process.ExitCode);
        return new GitResult(process.ExitCode, stdOut, stdErr);
    }

    // GitHub, Gitea and Forgejo all accept a PAT as the HTTP Basic password. GitHub ignores the
    // username entirely; Gitea matches it against the token's owner, so the caller passes the
    // account's real login when it knows one.
    static string BuildAuthHeader(GitCredential credential)
    {
        var raw = $"{credential.Username}:{credential.Token}";
        return "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }
}
