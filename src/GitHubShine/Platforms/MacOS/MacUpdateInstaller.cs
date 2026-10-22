using System.Diagnostics;
using System.Security.Cryptography;
using AppKit;
using Foundation;
using GitHubShine.Updates;

namespace GitHubShine.Platforms.MacOS;

/// <summary>
/// Installs a release's DMG over the running bundle and relaunches.
///
/// The sequence: download the DMG, check it against the release's SHA256SUMS.txt, mount it, check
/// the app inside has a valid signature from the same team as this one, copy it next to the
/// running bundle, swap the two by rename, and restart once this process has exited. Replacing a
/// running bundle is safe on macOS — the process keeps the files it already mapped — and the swap
/// is two renames on one volume, so there's no window where the app is half-copied.
///
/// Anything that rules out an in-place swap (a read-only location, App Translocation, a Debug build
/// whose bundle lives in bin/) falls back to opening the DMG in Finder for the usual drag-install.
/// A failed verification does not fall back: that package isn't something to hand the user either.
/// </summary>
public sealed class MacUpdateInstaller(
    BrowserUpdateInstaller fallback,
    ILogger<MacUpdateInstaller> logger) : IUpdateInstaller
{
    public bool CanInstallInPlace => true;

    public async Task<UpdateInstallResult> InstallAsync(AvailableUpdate update, IProgress<string> progress, CancellationToken ct)
    {
        if (update.PackageUrl is null)
            return await fallback.InstallAsync(update, progress, ct).ConfigureAwait(false);

        var work = Path.Combine(Path.GetTempPath(), $"GitHubShine-update-{update.Version}");
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var dmg = Path.Combine(work, update.PackageName ?? "update.dmg");
        progress.Report("Downloading…");
        await ReleaseFeed.DownloadAsync(
            update.PackageUrl,
            dmg,
            new Progress<double>(p => progress.Report($"Downloading… {p:P0}")),
            ct
        ).ConfigureAwait(false);

        progress.Report("Verifying download…");
        if (update.ChecksumsUrl is not null)
        {
            var expected = FindChecksum(await ReleaseFeed.GetStringAsync(update.ChecksumsUrl, ct).ConfigureAwait(false), Path.GetFileName(dmg));
            if (expected is null)
                return UpdateInstallResult.Failed($"The release's checksum list has no entry for {Path.GetFileName(dmg)}");

            var actual = await HashFileAsync(dmg, ct).ConfigureAwait(false);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return UpdateInstallResult.Failed("The download doesn't match the release's checksum — not installing it");
        }

        var current = NSBundle.MainBundle.BundlePath;
        var reason = WhyNotInPlace(current);
        if (reason is not null)
        {
            logger.LogInformation("[Update] can't replace {Bundle} in place ({Reason}); opening the disk image", current, reason);
            await RunAsync("/usr/bin/open", [dmg], ct).ConfigureAwait(false);
            return UpdateInstallResult.HandedOff("Opened the disk image — drag GitHub Shine into Applications to finish");
        }

        var mount = Path.Combine(work, "mnt");
        Directory.CreateDirectory(mount);
        progress.Report("Opening the disk image…");
        await RunAsync("/usr/bin/hdiutil", ["attach", dmg, "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount], ct).ConfigureAwait(false);
        try
        {
            var source = Directory.EnumerateDirectories(mount, "*.app").FirstOrDefault();
            if (source is null)
                return UpdateInstallResult.Failed("The disk image doesn't contain an app");

            progress.Report("Checking the signature…");
            var signatureError = await VerifySignatureAsync(source, current, ct).ConfigureAwait(false);
            if (signatureError is not null)
                return UpdateInstallResult.Failed(signatureError);

            progress.Report("Installing…");
            var parent = Path.GetDirectoryName(current)!;
            var name = Path.GetFileName(current);
            var staged = Path.Combine(parent, $".{name}.update");
            var retired = Path.Combine(parent, $".{name}.previous");
            DeleteIfExists(staged);
            DeleteIfExists(retired);

            // ditto, not a managed copy: it keeps the symlinks, permissions and extended
            // attributes the code signature covers.
            await RunAsync("/usr/bin/ditto", [source, staged], ct).ConfigureAwait(false);

            Directory.Move(current, retired);
            try
            {
                Directory.Move(staged, current);
            }
            catch
            {
                Directory.Move(retired, current);
                throw;
            }
            DeleteIfExists(retired);
        }
        finally
        {
            try
            {
                await RunAsync("/usr/bin/hdiutil", ["detach", mount, "-force"], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Update] couldn't detach {Mount}", mount);
            }
        }

        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch { }

        progress.Report("Restarting…");
        Relaunch(current);
        return UpdateInstallResult.Restarting;
    }

    static string? WhyNotInPlace(string bundle)
    {
#if DEBUG
        // A Debug bundle lives in bin/ and is rebuilt over by the next build anyway.
        return "debug build";
#else
        if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal))
            return "running from App Translocation";

        var parent = Path.GetDirectoryName(bundle);
        if (parent is null)
            return "no parent folder";

        try
        {
            var probe = Path.Combine(parent, $".githubshine-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch
        {
            return $"{parent} isn't writable";
        }
#endif
    }

    /// <summary>
    /// Null when <paramref name="candidate"/> has a valid signature and, if this app was signed by a
    /// team, the same team. An ad-hoc local build has no team to compare against.
    /// </summary>
    static async Task<string?> VerifySignatureAsync(string candidate, string running, CancellationToken ct)
    {
        try
        {
            await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", candidate], ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"The new version's code signature isn't valid ({ex.Message})";
        }

        var ours = await TeamIdAsync(running, ct).ConfigureAwait(false);
        if (ours is null)
            return null;

        var theirs = await TeamIdAsync(candidate, ct).ConfigureAwait(false);
        return theirs == ours
            ? null
            : $"The new version is signed by a different developer ({theirs ?? "none"}) — not installing it";
    }

    static async Task<string?> TeamIdAsync(string bundle, CancellationToken ct)
    {
        // codesign -d writes its report to stderr.
        var (_, err) = await RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", bundle], ct, throwOnError: false).ConfigureAwait(false);
        var line = err.Split('\n').FirstOrDefault(l => l.StartsWith("TeamIdentifier=", StringComparison.Ordinal));
        var id = line?["TeamIdentifier=".Length..].Trim();
        return String.IsNullOrEmpty(id) || id == "not set" ? null : id;
    }

    static string? FindChecksum(string sums, string fileName)
    {
        // shasum format: "<hex>  <name>" (two spaces, or " *" for binary mode).
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ');
            if (space <= 0)
                continue;
            var name = line[(space + 1)..].TrimStart(' ', '*');
            if (name.Equals(fileName, StringComparison.Ordinal))
                return line[..space];
        }
        return null;
    }

    static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Starts the new bundle once this process is gone. It has to wait: SingleInstance would see
    /// this copy still running and hand straight back to it.
    /// </summary>
    static void Relaunch(string bundle)
    {
        const string script = "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$2\"";
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var arg in new[] { "-c", script, "sh", Environment.ProcessId.ToString(), bundle })
            psi.ArgumentList.Add(arg);
        Process.Start(psi);

        // Terminate, not Application.Quit — the app stays alive in the tray otherwise (see TrayIconHost.QuitApp).
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => NSApplication.SharedApplication.Terminate(null));
    }

    static async Task<(string Out, string Err)> RunAsync(string file, string[] args, CancellationToken ct, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {file}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var (o, e) = (await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));

        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited {process.ExitCode}: {e.Trim()}");
        return (o, e);
    }
}
