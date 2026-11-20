using System.Diagnostics;

namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    private static readonly SingBoxRunner RelayRunner = new("relay");

    public static RunnerStatus RelayStatusSnapshot() => RelayRunner.GetStatus();
    public static string RelayLogTail(int lines = 40) => RelayRunner.TailLog(lines);
    public static string ClientLogTail(int lines = 40) => ClientRunner.TailLog(lines);
    public static string? ResolveRelayListenAddress(RelaySettings r) => ResolveListenAddress(r);

    public static string BuildClientConnectCommand(RelaySettings r, string? listen = null)
    {
        var ip = string.IsNullOrWhiteSpace(listen) ? (ResolveListenAddress(r) ?? "<ip>") : listen;
        return $"TailscaleSwitcher route profile add club --node {ip} --port {r.Port} --user {r.Username} --pass {r.Password}";
    }

    private async Task<int> RelayAsync(CliArgs a)
    {
        var sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var cfg = _store.Load();

        switch (sub)
        {
            case "init": return RelayInit(cfg, a);
            case "status": return RelayStatus(cfg);
            case "info": return RelayInfo(cfg);
            case "start": return await RelayStartAsync(cfg, a.Has("--persist"));
            case "stop":
                _out.WriteLine(RelayRunner.Stop(deleteTask: false) ? "Relay остановлен." : "Relay не запущен.");
                return 0;
            case "uninstall":
                RelayRunner.Stop(deleteTask: true);
                _out.WriteLine("Relay остановлен, задание автозапуска удалено.");
                return 0;
            case "firewall": return RelayFirewall(cfg);
            default: return Fail($"Неизвестная подкоманда '{sub}'. См. 'relay --help'.");
        }
    }

    private int RelayInit(RoutingConfig cfg, CliArgs a)
    {
        var r = cfg.Relay;
        if (a.Get("--listen") is { } l) r.Listen = l.Trim();
        if (a.Get("--user") is { } u) r.Username = u;
        if (a.Get("--pass") is { } pw) r.Password = pw;
        if (a.Get("--upstream") is { } up)
        {
            if (!up.Equals("direct", StringComparison.OrdinalIgnoreCase) && SingBoxConfigBuilder.ParseUpstream(up) == null)
                return Fail("--upstream: ожидается host:port, http://host:port, socks://host:port или 'direct'.");
            r.Upstream = up;
        }
        if (a.Get("--port") is { } ps)
        {
            if (!int.TryParse(ps, out var port) || port is < 1 or > 65535) return Fail("Неверный --port.");
            r.Port = port;
        }
        if (string.IsNullOrEmpty(r.Password)) r.Password = SecretGenerator.NewPassword();
        _store.Save(cfg);
        _out.WriteLine($"Настройки relay сохранены: {_store.Path}");
        return RelayInfo(cfg);
    }

    private int RelayInfo(RoutingConfig cfg)
    {
        var r = cfg.Relay;
        if (string.IsNullOrEmpty(r.Password)) return Fail("Relay не инициализирован: relay init");
        var listen = ResolveListenAddress(r);
        _out.WriteLine($"Relay: {listen ?? "<tailscale-ip не определён>"}:{r.Port}, апстрим: {(string.IsNullOrWhiteSpace(r.Upstream) ? "direct" : r.Upstream)}");
        _out.WriteLine("Команда для клиента (moscow_enjoyer):");
        _out.WriteLine($"  TailscaleSwitcher route profile add club --node {listen ?? "<ip>"} --port {r.Port} --user {r.Username} --pass {r.Password}");
        return 0;
    }

    private int RelayStatus(RoutingConfig cfg)
    {
        var st = RelayRunner.GetStatus();
        _out.WriteLine($"Relay: {(st.Running ? $"РАБОТАЕТ (pid {st.Pid})" : "остановлен")}");
        _out.WriteLine($"Слушает: {ResolveListenAddress(cfg.Relay) ?? "?"}:{cfg.Relay.Port}");
        if (st.Running)
        {
            var log = RelayRunner.TailLog(5).Trim();
            if (log.Length > 0) _out.WriteLine($"Лог:\n{log}");
        }
        return 0;
    }

    private async Task<int> RelayStartAsync(RoutingConfig cfg, bool persist)
    {
        if (!IsAdmin()) return Fail("Запуск relay идёт через Планировщик заданий (SYSTEM): нужен запуск от имени Администратора.", 3);
        var r = cfg.Relay;
        if (string.IsNullOrEmpty(r.Password)) return Fail("Relay не инициализирован: relay init");
        var listen = ResolveListenAddress(r);
        if (listen == null) return Fail("Не удалось определить Tailscale IP (tailscale ip -4). Укажи: relay init --listen <ip>");

        var exe = await SingBoxInstaller.EnsureAsync(m => _out.WriteLine(m));
        var err = await RelayRunner.StartAsync(exe, SingBoxConfigBuilder.BuildRelay(r, listen, RelayRunner.LogPath), persist);
        if (err != null) return Fail(err);
        _out.WriteLine($"Relay запущен: {listen}:{r.Port}{(persist ? " (автозапуск при загрузке включён)" : "")}. Если клиент не достучится — 'relay firewall' (от Администратора).");
        return 0;
    }

    private int RelayFirewall(RoutingConfig cfg)
    {
        if (!IsAdmin()) return Fail("Нужен запуск от имени Администратора.", 3);
        if (!SingBoxInstaller.IsInstalled()) return Fail("sing-box ещё не скачан: выполни 'relay start'.");
        const string rule = "TailscaleSwitcher relay";
        RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
        var (ok, output) = RunNetsh(
            $"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP " +
            $"localport={cfg.Relay.Port} remoteip={SingBoxConfigBuilder.TailscaleCgnat} program=\"{SingBoxInstaller.SingBoxExe}\"");
        if (!ok) return Fail($"netsh: {output.Trim()}");
        _out.WriteLine($"Правило firewall создано: TCP {cfg.Relay.Port} только из {SingBoxConfigBuilder.TailscaleCgnat}.");
        return 0;
    }

    private static string FindTailscalePath() => new[]
    {
        @"C:\Program Files\Tailscale\tailscale.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")
    }.FirstOrDefault(File.Exists) ?? "tailscale";

    private static string? ResolveListenAddress(RelaySettings r)
    {
        if (!string.IsNullOrWhiteSpace(r.Listen)) return r.Listen;
        var exe = FindTailscalePath();
        var (ok, output) = RunProcess(exe, "ip -4");
        if (!ok) return null;
        var ip = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return System.Net.IPAddress.TryParse(ip, out _) ? ip : null;
    }

    public sealed record RelayClient(string Ip, string Name, int Connections);

    /// <summary>Кто сейчас держит соединение с relay (ESTABLISHED на listen:port).</summary>
    public static List<RelayClient> GetConnectedClients(string? listenIp, int port)
    {
        if (string.IsNullOrWhiteSpace(listenIp)) return [];
        var net = RunProcess("netstat", "-an");
        var status = RunProcess(FindTailscalePath(), "status");
        return ParseConnectedClients(net.output, listenIp, port, ParseTailscaleNames(status.output));
    }

    public static Dictionary<string, string> ParseTailscaleNames(string status)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in status.Split('\n'))
        {
            var parts = raw.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !System.Net.IPAddress.TryParse(parts[0], out _)) continue;
            map[parts[0]] = parts[1];
        }
        return map;
    }

    public static List<RelayClient> ParseConnectedClients(
        string netstat, string listenIp, int port, IReadOnlyDictionary<string, string>? names)
    {
        var local = $"{listenIp}:{port}";
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in netstat.Split('\n'))
        {
            var parts = raw.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !parts[3].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[1].Equals(local, StringComparison.OrdinalIgnoreCase)) continue;
            var remote = parts[2];
            var colon = remote.LastIndexOf(':');
            if (colon <= 0) continue;
            var ip = remote[..colon];
            counts[ip] = counts.GetValueOrDefault(ip) + 1;
        }
        return counts
            .Select(kv => new RelayClient(kv.Key, names != null && names.TryGetValue(kv.Key, out var name) ? name : kv.Key, kv.Value))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (bool ok, string output) RunNetsh(string args) => RunProcess("netsh", args);

    private static (bool ok, string output) RunProcess(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return (false, "timeout"); }
            return (p.ExitCode == 0, o.Result + e.Result);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
