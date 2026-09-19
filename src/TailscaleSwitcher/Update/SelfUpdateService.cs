using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace TailscaleSwitcher.Update;

internal enum SelfUpdateStatus { AlreadyCurrent, UpdateAvailable, Updated }

internal sealed record SelfUpdateReport(SelfUpdateStatus Status, ReleaseVersion Installed, ReleaseVersion Release, string Tag);

internal sealed class SelfUpdateService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private readonly string _executablePath;
    private readonly ReleaseVersion _installedVersion;
    private readonly IReleaseSource _source;
    private readonly IExecutableReplacer _replacer;

    public SelfUpdateService(string executablePath, ReleaseVersion installedVersion, IReleaseSource source, IExecutableReplacer? replacer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("The executable path must be fully qualified.", nameof(executablePath));
        _executablePath = Path.GetFullPath(executablePath);
        _installedVersion = installedVersion;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _replacer = replacer ?? new ExecutableReplacer();
    }

    public async Task<SelfUpdateReport> CheckAsync(CancellationToken cancellationToken)
    {
        _replacer.RemoveRetiredCopies(_executablePath);
        var release = await _source.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        if (release.Version <= _installedVersion)
            return new SelfUpdateReport(SelfUpdateStatus.AlreadyCurrent, _installedVersion, release.Version, release.Tag);
        return new SelfUpdateReport(SelfUpdateStatus.UpdateAvailable, _installedVersion, release.Version, release.Tag);
    }

    public async Task<SelfUpdateReport> ApplyAsync(CancellationToken cancellationToken, Action<long, long?>? progress = null)
    {
        var release = await _source.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        if (release.Version <= _installedVersion)
            return new SelfUpdateReport(SelfUpdateStatus.AlreadyCurrent, _installedVersion, release.Version, release.Tag);
        await InstallAsync(release, cancellationToken, progress).ConfigureAwait(false);
        return new SelfUpdateReport(SelfUpdateStatus.Updated, _installedVersion, release.Version, release.Tag);
    }

    private async Task InstallAsync(ReleaseDescriptor release, CancellationToken cancellationToken, Action<long, long?>? progress)
    {
        var installDirectory = Path.GetDirectoryName(_executablePath) ?? throw new InvalidOperationException("The install directory could not be determined.");
        var staging = Path.Combine(installDirectory, ".tailscaleswitcher-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, GitHubReleaseSource.PackageAsset);
            await _source.DownloadAsync(release.PackageUrl, zipPath, cancellationToken, progress).ConfigureAwait(false);
            var checksum = await _source.ReadTextAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
            await VerifyZipAsync(zipPath, ReleaseChecksum.Parse(checksum), cancellationToken).ConfigureAwait(false);

            var payload = Path.Combine(staging, "payload");
            ZipFile.ExtractToDirectory(zipPath, payload);
            File.Delete(zipPath);

            // ТерминалV проверяет 3 файла (exe+com+wwwroot), здесь достаточно exe
            var stagedExe = Path.Combine(payload, AppVersion.ExecutableName);
            // zip может содержать вложенную папку
            if (!File.Exists(stagedExe))
            {
                var found = Directory.EnumerateFiles(payload, AppVersion.ExecutableName, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) stagedExe = found;
            }
            if (!File.Exists(stagedExe))
                throw new InvalidDataException($"The downloaded release does not contain {AppVersion.ExecutableName}.");

            // проба: новый exe должен отвечать на --help
            if (!await ProbeAsync(stagedExe, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The downloaded release did not run (--help probe failed).");

            var retired = _replacer.Replace(_executablePath, stagedExe);
            try
            {
                if (!await ProbeAsync(_executablePath, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("The downloaded release did not run after installation.");
                _replacer.RemoveRetiredCopies(_executablePath);
            }
            catch
            {
                _replacer.Restore(retired, _executablePath);
                throw;
            }
            // очистка staging
            TryDeleteDirectory(staging);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static async Task VerifyZipAsync(string zipPath, string expectedHash, CancellationToken cancellationToken)
    {
        var info = new FileInfo(zipPath);
        if (!info.Exists || info.Length == 0) throw new InvalidDataException("The downloaded release is empty.");
        string actual;
        await using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var header = new byte[2];
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 2 || header[0] != (byte)'P' || header[1] != (byte)'K')
                throw new InvalidDataException("The downloaded release is not a zip archive.");
            stream.Position = 0;
            actual = ReleaseChecksum.Format(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        if (!ReleaseChecksum.Matches(expectedHash, actual))
            throw new InvalidDataException("The downloaded release failed its checksum.");
    }

    private static async Task<bool> ProbeAsync(string executablePath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--help");
        using var process = Process.Start(psi);
        if (process is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        return process.ExitCode == 0;
    }

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path) && IsRegularTree(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static bool IsRegularTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        if (!Directory.Exists(path)) return true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            if (!IsRegularTree(entry)) return false;
        return true;
    }
}
