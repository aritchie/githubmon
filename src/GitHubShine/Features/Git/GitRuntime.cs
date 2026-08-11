using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Shiny;

namespace GitHubShine.Git;

/// <summary>HTTPS credentials for a git remote — the account's login plus its personal access token.</summary>
public sealed record GitCredential(string Username, string Token);

/// <summary>Thrown when the libgit2 native library isn't usable on this device.</summary>
public sealed class GitUnavailableException(string message) : Exception(message);

public interface IGitRuntime
{
    /// <summary>The libgit2 build behind the managed API, or null when it couldn't be loaded.</summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Throws <see cref="GitUnavailableException"/> unless libgit2 loaded successfully.</summary>
    Task EnsureAvailableAsync(CancellationToken ct = default);
}

/// <summary>
/// Guards the one thing that can still go wrong now git is in-process: the libgit2 native failing
/// to load. That used to be "is git on PATH", which every user had to solve themselves; it's now a
/// packaging question, so it fails on a RID with no native rather than on an unprepared machine.
///
/// The probe touches <see cref="GlobalSettings.Version"/>, which is the cheapest call that forces
/// the native library to resolve, and caches the answer — a failure here is permanent for the
/// life of the process, since the assembly's static initializer only runs once.
/// </summary>
[Singleton]
public sealed class GitRuntime(ILogger<GitRuntime> logger) : IGitRuntime
{
    readonly SemaphoreSlim probeLock = new(1, 1);
    bool probed;
    string? version;
    string? failure;

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        await this.ProbeOnceAsync(ct).ConfigureAwait(false);
        return this.version;
    }

    public async Task EnsureAvailableAsync(CancellationToken ct = default)
    {
        await this.ProbeOnceAsync(ct).ConfigureAwait(false);
        if (this.failure is { } message)
            throw new GitUnavailableException(message);
    }

    async Task ProbeOnceAsync(CancellationToken ct)
    {
        if (this.probed)
            return;

        await this.probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.probed)
                return;

            try
            {
                var build = GlobalSettings.Version;
                this.version = build.InformationalVersion;
                logger.LogDebug("libgit2 {Version} loaded with {Features}", this.version, build.Features);

                // A native built without HTTPS support loads perfectly and then fails on the first
                // clone with an obscure "unsupported URL protocol" — worth catching here instead,
                // since the mobile natives are built by hand rather than taken from the package.
                if (!build.Features.HasFlag(BuiltInFeatures.Https))
                    this.failure = "This build of libgit2 has no HTTPS support, so repositories can't be cloned or fetched.";
                else
                    this.TrustDeviceCertificates();
            }
            catch (Exception ex)
            {
                // DllNotFoundException on a RID with no native; TypeInitializationException when the
                // static initializer already failed once and every later call rethrows it.
                logger.LogError(ex, "libgit2 failed to load");
                this.failure = "Git support isn't available on this device — the libgit2 native library couldn't be loaded.";
            }

            this.probed = true;
        }
        finally
        {
            this.probeLock.Release();
        }
    }

#if ANDROID
    /// <summary>
    /// Points libgit2's mbedTLS backend at the device's trust store.
    ///
    /// Only Android needs this. Every other head gets its CA certificates from the platform: the
    /// desktop natives use OpenSSL/Schannel/SecureTransport, and so does iOS. Android has no system
    /// TLS library to link against, so the native we build there carries mbedTLS, which knows about
    /// no certificates until it is handed a directory of them — and that directory moved out of
    /// /system into an APEX module in Android 14, so the path baked in at build time isn't enough
    /// on its own.
    /// </summary>
    void TrustDeviceCertificates()
    {
        // Newest layout first: on Android 14+ /system/etc/security/cacerts can still exist while
        // the certificates that are actually current live in the Conscrypt APEX.
        string[] candidates =
        [
            "/apex/com.android.conscrypt/cacerts",
            "/system/etc/security/cacerts"
        ];

        foreach (var path in candidates)
        {
            if (!Directory.Exists(path) || !Directory.EnumerateFiles(path).Any())
                continue;

            // git_libgit2_opts is variadic in C. Declaring it with fixed parameters is only safe
            // because Android's ABIs pass variadic arguments in the same registers as ordinary
            // ones — do NOT copy this to an Apple target, where they go on the stack instead.
            // (Nothing does: iOS uses SecureTransport and needs no cert path at all.)
            var result = git_libgit2_opts(GitOptSetSslCertLocations, null, path);
            if (result == 0)
            {
                logger.LogDebug("libgit2 trusting certificates from {Path}", path);
                return;
            }

            logger.LogWarning("libgit2 refused the certificates at {Path} (code {Code})", path, result);
        }

        // Not fatal on its own: the path compiled into the native may already have loaded a usable
        // store. If none of it worked, the first fetch fails with a TLS error, which the page shows.
        logger.LogWarning("No usable CA certificate directory found — HTTPS may fail");
    }

    /// <summary>GIT_OPT_SET_SSL_CERT_LOCATIONS, the 13th member of libgit2 1.8.6's option enum.</summary>
    const int GitOptSetSslCertLocations = 12;

    [System.Runtime.InteropServices.DllImport("git2-5853918", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern int git_libgit2_opts(int option, string? file, string? path);
#else
    /// <summary>Every non-Android head takes its CA certificates from the platform trust store.</summary>
    void TrustDeviceCertificates()
    {
    }
#endif
}

/// <summary>
/// The callbacks every network operation needs: who to authenticate as, and how to notice the user
/// pressed Cancel.
///
/// Credentials go straight into libgit2's callback, so unlike the git CLI this replaces, a token is
/// never written to a config file, an argv, or an environment variable — there is no
/// <c>.git/config</c> left behind holding one, and nothing for another process to read.
/// </summary>
public static class GitCallbacks
{
    /// <summary>
    /// Hands <paramref name="credential"/> to libgit2 when it asks. Null when there's no token:
    /// a public repo needs none, and libgit2 fails clearly if it turns out one was required.
    /// </summary>
    public static CredentialsHandler? Credentials(GitCredential? credential)
        => credential is null
            ? null
            : (_, _, _) => new UsernamePasswordCredentials
            {
                Username = credential.Username,
                Password = credential.Token
            };

    /// <summary>
    /// Fetch options wired for cancellation and progress. libgit2 has no cancellation token: a
    /// callback returning false aborts the transfer, which surfaces as
    /// <see cref="UserCancelledException"/> — <see cref="Run"/> turns that back into an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static FetchOptions Fetch(
        GitCredential? credential,
        CancellationToken ct,
        bool prune = true,
        IProgress<string>? progress = null,
        TagFetchMode? tags = null)
    {
        var options = new FetchOptions
        {
            Prune = prune,
            CredentialsProvider = Credentials(credential),
            OnTransferProgress = _ => !ct.IsCancellationRequested,
            OnProgress = message =>
            {
                // The remote's own "Counting objects…" chatter, which is what the CLI wrapper used
                // to scrape off stderr and put in the run log.
                if (!string.IsNullOrWhiteSpace(message))
                    progress?.Report(message.Trim());
                return !ct.IsCancellationRequested;
            }
        };

        if (tags is { } mode)
            options.TagFetchMode = mode;

        return options;
    }

    /// <summary>Push options wired for cancellation, mirroring <see cref="Fetch"/>.</summary>
    public static PushOptions Push(GitCredential? credential, CancellationToken ct)
        => new()
        {
            CredentialsProvider = Credentials(credential),
            OnPackBuilderProgress = (_, _, _) => !ct.IsCancellationRequested,
            OnPushTransferProgress = (_, _, _) => !ct.IsCancellationRequested
        };

    /// <summary>
    /// Runs a libgit2 call off the UI thread. Everything in LibGit2Sharp is synchronous and a clone
    /// is minutes of work, so none of it may run on the caller's thread; and a cancel that reached
    /// libgit2 through a callback comes back as <see cref="UserCancelledException"/>, which callers
    /// up the stack are entitled to see as an ordinary <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<T> Run<T>(Func<T> work, CancellationToken ct)
    {
        try
        {
            return await Task.Run(work, ct).ConfigureAwait(false);
        }
        catch (UserCancelledException)
        {
            throw new OperationCanceledException(ct);
        }
    }

    /// <inheritdoc cref="Run{T}"/>
    public static Task Run(Action work, CancellationToken ct)
        => Run(() => { work(); return true; }, ct);
}
