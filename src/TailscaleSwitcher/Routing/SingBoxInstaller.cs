using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using TailscaleSwitcher.Update;

namespace TailscaleSwitcher.Routing;

/// <summary>Скачивает и проверяет sing-box + wintun.dll (версии и SHA-256 зафиксированы в коде).</summary>
public static class SingBoxInstaller
{
    public const string SingBoxVersion = "1.14.4";
    public const string SingBoxUrl =
        "https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip";
    public const string SingBoxSha256 = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df";

    public const string WintunVersion = "0.14.1";
    public const string WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
    public const string WintunSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";

    public static string ToolDir => Path.Combine(RoutingPaths.ToolsDir, $"sing-box-{SingBoxVersion}");
    public static string SingBoxExe => Path.Combine(ToolDir, "sing-box.exe");

    public static bool IsInstalled() =>
        File.Exists(SingBoxExe) && File.Exists(Path.Combine(ToolDir, "wintun.dll"));

    public static async Task<string> EnsureAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        if (IsInstalled()) return SingBoxExe;

        Directory.CreateDirectory(RoutingPaths.ToolsDir);
        var staging = Path.Combine(RoutingPaths.ToolsDir, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("tailscaleswitcher");

            var sbZip = Path.Combine(staging, "sing-box.zip");
            log?.Invoke($"Скачиваю sing-box {SingBoxVersion}...");
            await DownloadVerifiedAsync(http, SingBoxUrl, sbZip, SingBoxSha256, ct);

            var wtZip = Path.Combine(staging, "wintun.zip");
            log?.Invoke($"Скачиваю wintun {WintunVersion}...");
            await DownloadVerifiedAsync(http, WintunUrl, wtZip, WintunSha256, ct);

            var outDir = Path.Combine(staging, "out");
            Directory.CreateDirectory(outDir);
            ExtractFlat(sbZip, outDir, e => e.Name is "sing-box.exe" or "libcronet.dll" or "LICENSE");
            ExtractFlat(wtZip, outDir, e => e.FullName.Equals("wintun/bin/amd64/wintun.dll", StringComparison.OrdinalIgnoreCase));

            if (!File.Exists(Path.Combine(outDir, "sing-box.exe")) || !File.Exists(Path.Combine(outDir, "wintun.dll")))
                throw new InvalidDataException("В архивах нет sing-box.exe или wintun.dll.");

            if (Directory.Exists(ToolDir)) Directory.Delete(ToolDir, recursive: true);
            Directory.Move(outDir, ToolDir);
            log?.Invoke($"Установлено: {ToolDir}");
            return SingBoxExe;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static async Task DownloadVerifiedAsync(HttpClient http, string url, string dest, string expectedSha, CancellationToken ct)
    {
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(dest);
            await resp.Content.CopyToAsync(fs, ct);
        }

        await using var read = File.OpenRead(dest);
        var actual = ReleaseChecksum.Format(await SHA256.HashDataAsync(read, ct));
        if (!ReleaseChecksum.Matches(expectedSha, actual))
            throw new InvalidDataException($"SHA-256 не совпал для {url}: ожидался {expectedSha}, получен {actual}.");
    }

    private static void ExtractFlat(string zipPath, string outDir, Func<ZipArchiveEntry, bool> pick)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0 || !pick(entry)) continue;
            // берём только имя файла — защита от zip-slip
            entry.ExtractToFile(Path.Combine(outDir, Path.GetFileName(entry.Name)), overwrite: true);
        }
    }
}
