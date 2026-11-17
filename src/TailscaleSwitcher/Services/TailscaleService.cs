using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace TailscaleSwitcher.Services;

public sealed record ExitNodeInfo(string HostName, string TailscaleIP, string Location, bool Online, bool IsExitNode);

public sealed record TailnetPeerInfo(string HostName, string TailscaleIP, string Os, bool Online, bool IsExitNodeOption);

public sealed class TailscalePrefs
{
    public bool RouteAll { get; set; }
    public string ExitNodeID { get; set; } = "";
    public string ExitNodeIP { get; set; } = "";
    public bool ExitNodeAllowLANAccess { get; set; }
}

public sealed record CommandResult(bool Ok, string Output, string Error, int ExitCode);

/// <summary>Общие операции с tailscale.exe, системными маршрутами и правами Windows.</summary>
public static class TailscaleService
{
    public const string DefaultXeonTailscaleIp = "100.119.48.15";

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool RelaunchAsAdmin(string? arguments = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            };
            if (!string.IsNullOrWhiteSpace(arguments))
                psi.Arguments = arguments;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    public static string? FindTailscaleExe()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Tailscale\tailscale.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            "tailscale.exe",
            "tailscale"
        };
        foreach (var c in candidates)
        {
            if (c.Contains(":\\") && File.Exists(c)) return c;
            var res = RunRaw(c, "--version", 3000);
            if (res.Ok && res.Output.Contains("tailscale", StringComparison.OrdinalIgnoreCase)) return c;
        }
        var where = RunRaw("where", "tailscale.exe", 3000);
        if (where.Ok && !string.IsNullOrWhiteSpace(where.Output))
        {
            var first = where.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (File.Exists(first)) return first;
        }
        return null;
    }

    public static CommandResult RunTailscale(string args, int timeoutMs = 10000)
    {
        var exe = FindTailscaleExe();
        if (exe == null)
            return new CommandResult(false, "", @"Не найден tailscale.exe (ожидается C:\Program Files\Tailscale\tailscale.exe).", -1);
        return RunRaw(exe, args, timeoutMs);
    }

    public static CommandResult RunRaw(string exe, string args, int timeoutMs = 10000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return new CommandResult(false, "", $"Не удалось запустить {exe}.", -1);
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(false, "", "timeout", -1);
            }
            Task.WaitAll(stdoutTask, stderrTask);
            return new CommandResult(p.ExitCode == 0, stdoutTask.Result, stderrTask.Result, p.ExitCode);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, "", ex.Message, -1);
        }
    }

    public static TailscalePrefs? GetPrefs()
    {
        var res = RunTailscale("debug prefs", 5000);
        if (!res.Ok || string.IsNullOrWhiteSpace(res.Output)) return null;
        try { return JsonSerializer.Deserialize<TailscalePrefs>(res.Output); }
        catch { return null; }
    }

    public static string? GetTailscaleIp4()
    {
        var res = RunTailscale("ip -4", 5000);
        if (!res.Ok) return null;
        return res.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    public static string GetStatusText()
    {
        var res = RunTailscale("status", 8000);
        if (res.Ok && !string.IsNullOrWhiteSpace(res.Output)) return res.Output.Trim();
        return string.IsNullOrWhiteSpace(res.Error) ? "tailscale status не вернул данных." : res.Error.Trim();
    }

    public static List<ExitNodeInfo> GetExitNodes()
    {
        var resList = RunTailscale("exit-node list", 6000);
        if (resList.Ok && !string.IsNullOrWhiteSpace(resList.Output) &&
            !resList.Output.Contains("no exit nodes", StringComparison.OrdinalIgnoreCase))
        {
            var fromList = ParseExitNodesFromList(resList.Output);
            if (fromList.Count > 0) return fromList;
        }

        var resJson = RunTailscale("status --json", 6000);
        if (resJson.Ok && !string.IsNullOrWhiteSpace(resJson.Output))
            return ParseExitNodesFromJson(resJson.Output);

        return [];
    }

    public static List<ExitNodeInfo> ParseExitNodesFromList(string output)
    {
        var parsed = new List<ExitNodeInfo>();
        if (string.IsNullOrWhiteSpace(output)) return parsed;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith('#') || t.StartsWith("IP", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(t))
                continue;
            var parts = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var ip = parts[0].Contains('.') ? parts[0] : parts[1];
            var host = parts[0].Contains('.') ? parts[1] : parts[0];
            var loc = parts.Length >= 3 ? parts[^1] : "";
            parsed.Add(new ExitNodeInfo(host, ip, loc, Online: true, IsExitNode: true));
        }
        return parsed;
    }

    public static List<ExitNodeInfo> ParseExitNodesFromJson(string json)
    {
        var parsed = new List<ExitNodeInfo>();
        if (string.IsNullOrWhiteSpace(json)) return parsed;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Peer", out var peers)) return parsed;
            foreach (var peer in peers.EnumerateObject())
            {
                var v = peer.Value;
                var isOption = v.TryGetProperty("ExitNodeOption", out var e1) && e1.GetBoolean();
                var isExit = v.TryGetProperty("ExitNode", out var e2) && e2.GetBoolean();
                if (!isOption && !isExit) continue;
                var host = v.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : "";
                var ip = v.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0 ? ips[0].GetString() ?? "" : "";
                var online = v.TryGetProperty("Online", out var on) && on.GetBoolean();
                parsed.Add(new ExitNodeInfo(host, ip, "", online, isExit || isOption));
            }
        }
        catch { }
        return parsed;
    }

    public static List<TailnetPeerInfo> GetTailnetPeers()
    {
        var res = RunTailscale("status --json", 6000);
        if (!res.Ok || string.IsNullOrWhiteSpace(res.Output)) return [];
        return ParseTailnetPeersFromJson(res.Output);
    }

    public static List<TailnetPeerInfo> ParseTailnetPeersFromJson(string json)
    {
        var result = new List<TailnetPeerInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Peer", out var peers)) return result;
            foreach (var peer in peers.EnumerateObject())
            {
                var v = peer.Value;
                var host = v.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : "";
                var ip = v.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0 ? ips[0].GetString() ?? "" : "";
                var online = v.TryGetProperty("Online", out var o) && o.GetBoolean();
                var os = v.TryGetProperty("OS", out var osEl) ? osEl.GetString() ?? "" : "";
                var isExit = (v.TryGetProperty("ExitNodeOption", out var e1) && e1.GetBoolean())
                             || (v.TryGetProperty("ExitNode", out var e2) && e2.GetBoolean());
                if (ip.Length > 0)
                    result.Add(new TailnetPeerInfo(host, ip, os, online, isExit));
            }
        }
        catch { }
        return result
            .OrderByDescending(p => p.Online)
            .ThenByDescending(p => string.Equals(p.Os, "windows", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static CommandResult SetExitNode(string? targetIpOrHost, bool allowLanAccess)
    {
        if (string.IsNullOrWhiteSpace(targetIpOrHost))
            return RunTailscale("set --exit-node=", 12000);

        var lanFlag = allowLanAccess ? "true" : "false";
        return RunTailscale($"set --exit-node={targetIpOrHost.Trim()} --exit-node-allow-lan-access={lanFlag}", 12000);
    }

    public static void OpenAdmin()
    {
        Process.Start(new ProcessStartInfo("https://login.tailscale.com/admin/machines") { UseShellExecute = true });
    }

    public static string? ParsePhysicalGatewayFromRoutePrint(string routeOutput)
    {
        foreach (var line in routeOutput.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("0.0.0.0", StringComparison.Ordinal)) continue;
            var parts = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var g = parts[2];
            if (string.Equals(g, "On-link", StringComparison.OrdinalIgnoreCase)) continue;
            if (g.StartsWith("172.19.", StringComparison.Ordinal) || g.StartsWith("100.", StringComparison.Ordinal))
                continue;
            if (IPAddress.TryParse(g, out _)) return g;
        }
        return null;
    }

    public static HashSet<string> ParseHappIpsFromNetstat(string netstatOutput)
    {
        var ips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in netstatOutput.Split('\n'))
        {
            if (!line.Contains("ESTABLISHED", StringComparison.Ordinal)) continue;
            if (line.Contains("100.108.", StringComparison.Ordinal) ||
                line.Contains("100.119.", StringComparison.Ordinal) ||
                line.Contains("127.0.0.1", StringComparison.Ordinal) ||
                line.Contains("172.19.224.", StringComparison.Ordinal))
                continue;

            if (!line.Contains("172.19.0.1:", StringComparison.Ordinal) &&
                !line.Contains("10.217.", StringComparison.Ordinal))
                continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var remote = parts[2];
            var ip = remote.Split(':')[0];
            if (IPAddress.TryParse(ip, out var a) &&
                !a.ToString().StartsWith("172.", StringComparison.Ordinal) &&
                !a.ToString().StartsWith("192.168.", StringComparison.Ordinal) &&
                !a.ToString().StartsWith("100.", StringComparison.Ordinal))
            {
                ips.Add(ip);
            }
        }
        return ips;
    }

    public static async Task<bool> AutoFixHappExitNodeAsync(Action<string> log)
    {
        if (!IsAdmin())
        {
            log("Нужны права Администратора для изменения таблицы маршрутов и exit-node.");
            return false;
        }

        var nodes = GetExitNodes();
        var xeon = nodes.FirstOrDefault(n => n.TailscaleIP == DefaultXeonTailscaleIp);
        if (xeon == null)
        {
            log($"Xeon ({DefaultXeonTailscaleIp}) не найден среди exit-node. Доступные: {nodes.Count}");
            foreach (var n in nodes) log($"  • {n.HostName} ({n.TailscaleIP})");
        }
        else
        {
            log($"Найден xeon: {xeon.HostName} ({xeon.TailscaleIP}), {(xeon.Online ? "online" : "offline")}");
        }

        log("Ищу физический шлюз...");
        var routeRes = RunRaw("route", "print 0.0.0.0", 5000);
        var gateway = ParsePhysicalGatewayFromRoutePrint(routeRes.Output);
        if (string.IsNullOrEmpty(gateway))
        {
            var ipRes = RunRaw("ipconfig", "", 5000);
            foreach (var line in ipRes.Output.Split('\n'))
            {
                if (line.Contains("Основной шлюз", StringComparison.OrdinalIgnoreCase) && line.Contains("10.", StringComparison.Ordinal))
                {
                    var p = line.Split(':');
                    if (p.Length == 2 && IPAddress.TryParse(p[1].Trim(), out _))
                    {
                        gateway = p[1].Trim();
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(gateway))
        {
            gateway = "10.47.226.95";
            log($"Физический шлюз не определён, использую {gateway} по умолчанию.");
        }
        else
        {
            log($"Физический шлюз: {gateway}");
        }

        log("Ищу IP сервера Happ...");
        var nsRes = RunRaw("netstat", "-an", 5000);
        var happIps = ParseHappIpsFromNetstat(nsRes.Output);
        var psRes = RunRaw(
            "powershell",
            "-NoProfile -Command \"Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -like '172.19.*' } | Select-Object -ExpandProperty RemoteAddress | Sort-Object -Unique\"",
            5000);
        foreach (var line in psRes.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (IPAddress.TryParse(t, out _)) happIps.Add(t);
        }

        if (happIps.Count == 0)
        {
            log("Активные подключения Happ (172.19.0.1) не найдены — маршруты не добавлялись.");
        }
        else
        {
            log($"Найдены IP Happ: {string.Join(", ", happIps.Take(5))}");
            foreach (var hip in happIps.Take(5))
            {
                var addRes = RunRaw("route", $"add {hip} mask 255.255.255.255 {gateway} metric 1", 5000);
                if (addRes.Ok) log($"  route add {hip} -> {gateway}: OK");
                else if (addRes.Error.Contains("already", StringComparison.OrdinalIgnoreCase) || addRes.Error.Contains("уже", StringComparison.OrdinalIgnoreCase))
                    log($"  route add {hip} -> {gateway}: уже существует");
                else
                    log($"  route add {hip}: ошибка ({addRes.Error.Trim()})");
            }
        }

        log($"Включаю exit-node {DefaultXeonTailscaleIp} (LAN разрешён)...");
        var setRes = await Task.Run(() => SetExitNode(DefaultXeonTailscaleIp, allowLanAccess: true)).ConfigureAwait(false);
        if (!setRes.Ok)
        {
            log($"Ошибка tailscale set: {setRes.Error} {setRes.Output}".Trim());
            return false;
        }

        log("Exit-node включён. Проверяю пинг до xeon...");
        var pingRes = await Task.Run(() => RunTailscale($"ping -c 3 {DefaultXeonTailscaleIp}", 8000)).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(pingRes.Output))
            log(pingRes.Output.Trim());
        return true;
    }

    public static List<(string ProcessExe, string WindowTitle)> GetRunningWindowApps()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(proc.MainWindowTitle)) continue;
                var exe = proc.ProcessName + ".exe";
                seen.TryAdd(exe, proc.MainWindowTitle.Trim());
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return seen
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
