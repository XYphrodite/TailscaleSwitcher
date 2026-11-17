using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TailscaleSwitcher.Update;

namespace TailscaleSwitcher.Services;

internal static class AppUpdater
{
    public static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static async Task<SelfUpdateReport> CheckAsync(CancellationToken ct = default)
    {
        var path = RequireExecutablePath();
        using var source = new GitHubReleaseSource();
        var service = new SelfUpdateService(path, AppVersion.Current, source);
        return await service.CheckAsync(ct).ConfigureAwait(false);
    }

    public static async Task<SelfUpdateReport> ApplyInProcessAsync(Action<long, long?>? progress = null, CancellationToken ct = default)
    {
        var path = RequireExecutablePath();
        using var source = new GitHubReleaseSource();
        var service = new SelfUpdateService(path, AppVersion.Current, source);
        return await service.ApplyAsync(ct, progress).ConfigureAwait(false);
    }

    public static async Task<SelfUpdateReport> ApplyWithElevationFallbackAsync(
        Action<long, long?>? progress = null,
        CancellationToken ct = default)
    {
        var path = RequireExecutablePath();
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Не удалось определить папку установки.");

        if (IsDirectoryWritable(dir))
            return await ApplyInProcessAsync(progress, ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo(path)
        {
            Arguments = "--apply-update",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = dir
        };

        Process? elevated;
        try
        {
            elevated = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Обновление отменено (требовались права администратора).");
        }

        if (elevated is null)
            throw new InvalidOperationException("Не удалось запустить процесс обновления.");

        using (elevated)
        {
            await elevated.WaitForExitAsync(ct).ConfigureAwait(false);
            if (elevated.ExitCode != 0)
                throw new InvalidOperationException($"Обновление завершилось с кодом {elevated.ExitCode}.");
        }

        var check = await CheckAsync(ct).ConfigureAwait(false);
        return new SelfUpdateReport(SelfUpdateStatus.Updated, AppVersion.Current, check.Release, check.Tag);
    }

    public static void CleanUpRetiredCopies()
    {
        var path = Environment.ProcessPath;
        if (!AppVersion.CanSelfUpdate(path)) return;
        try
        {
            new ExecutableReplacer().RemoveRetiredCopies(Path.GetFullPath(path!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string RequireExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!AppVersion.CanSelfUpdate(path))
            throw new InvalidOperationException("Самообновление доступно только для установленной копии TailscaleSwitcher.exe.");
        return Path.GetFullPath(path!);
    }
}
