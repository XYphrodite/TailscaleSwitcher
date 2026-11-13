using TailscaleSwitcher.Proxy;

namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    private static readonly SingBoxRunner ClientRunner = new("client");

    private async Task<int> RouteAsync(CliArgs a)
    {
        var sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var rest = a.Positional.Skip(1).ToList();
        var cfg = _store.Load();

        switch (sub)
        {
            case "status": return await RouteStatusAsync(cfg);
            case "list": return RouteList(cfg);
            case "on": return await RouteOnAsync(cfg, rest.FirstOrDefault(), a.Has("--force", "-f"));
            case "off": return RouteOff();
            case "toggle":
                return ClientRunner.GetStatus().Running
                    ? RouteOff()
                    : await RouteOnAsync(cfg, rest.FirstOrDefault(), a.Has("--force", "-f"));
            case "profile": return RouteProfileCmd(cfg, rest, a);
            case "app": return RouteListEdit(cfg, rest, a, isApp: true);
            case "host": return RouteListEdit(cfg, rest, a, isApp: false);
            case "config":
            {
                var p = a.Get("--profile") is { } n ? cfg.Find(n) : cfg.FindActiveOrOnly();
                if (p == null) return Fail("Профиль не найден. См. 'route list'.");
                var json = SingBoxConfigBuilder.BuildClient(p);
                _out.WriteLine(string.IsNullOrEmpty(p.Password) ? json : json.Replace(p.Password, "***"));
                return 0;
            }
            default: return Fail($"Неизвестная подкоманда '{sub}'. См. 'route --help'.");
        }
    }

    private async Task<int> RouteStatusAsync(RoutingConfig cfg)
    {
        var st = ClientRunner.GetStatus();
        _out.WriteLine($"Маршрутизация: {(st.Running ? $"ВКЛЮЧЕНА (sing-box pid {st.Pid})" : "выключена")}");
        var p = cfg.FindActiveOrOnly();
        if (p == null)
        {
            _out.WriteLine(cfg.Profiles.Count == 0
                ? "Профилей нет. Добавь: route profile add <имя> --node <tailscale-ip>"
                : "Активный профиль не выбран: route profile use <имя>");
            return 0;
        }
        _out.WriteLine($"Профиль:       {p.Name} -> {p.Node}:{p.Port}");
        _out.WriteLine($"Приложений: {p.Apps.Count}, адресов: {p.Hosts.Count}");
        var ok = await XeonProxyService.ProbeTcpAsync($"{p.Node}:{p.Port}", 2000);
        _out.WriteLine($"Relay:         {(ok ? "доступен" : "недоступен")}");
        return 0;
    }

    private int RouteList(RoutingConfig cfg)
    {
        if (cfg.Profiles.Count == 0)
        {
            _out.WriteLine("Профилей нет.");
            return 0;
        }
        foreach (var p in cfg.Profiles)
        {
            var mark = string.Equals(p.Name, cfg.Active, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            _out.WriteLine($"{mark} {p.Name}  ->  {p.Node}:{p.Port}  (iface {p.TailscaleInterface}, dns {(p.RemoteDns ? p.DnsServer + " через relay" : "локальный")})");
            _out.WriteLine($"    приложения: {(p.Apps.Count == 0 ? "-" : string.Join(", ", p.Apps))}");
            _out.WriteLine($"    адреса:     {(p.Hosts.Count == 0 ? "-" : string.Join(", ", p.Hosts))}");
        }
        return 0;
    }

    private async Task<int> RouteOnAsync(RoutingConfig cfg, string? name, bool force)
    {
        var p = name != null ? cfg.Find(name) : cfg.FindActiveOrOnly();
        if (p == null) return Fail(name != null ? $"Профиль '{name}' не найден." : "Профиль не выбран. См. 'route list'.");
        if (p.Apps.Count == 0 && p.Hosts.Count == 0)
            return Fail($"В профиле '{p.Name}' нет ни приложений, ни адресов. Добавь: route app add / route host add.");
        if (!IsAdmin())
            return Fail("Для TUN нужен запуск от имени Администратора.", 3);

        if (!await XeonProxyService.ProbeTcpAsync($"{p.Node}:{p.Port}", 3000) && !force)
            return Fail($"Relay {p.Node}:{p.Port} не отвечает. Запущен ли 'relay start' на клубном ПК и открыт ли firewall? (--force чтобы игнорировать)", 2);

        var exe = await SingBoxInstaller.EnsureAsync(m => _out.WriteLine(m));
        if (ClientRunner.GetStatus().Running) ClientRunner.Stop();

        var err = await ClientRunner.StartAsync(exe, SingBoxConfigBuilder.BuildClient(p, ClientRunner.LogPath));
        if (err != null) return Fail(err);

        cfg.Active = p.Name;
        _store.Save(cfg);
        _out.WriteLine($"Включено: профиль '{p.Name}' -> {p.Node}:{p.Port}. Приложений: {p.Apps.Count}, адресов: {p.Hosts.Count}.");
        return 0;
    }

    private int RouteOff()
    {
        if (!ClientRunner.GetStatus().Running)
        {
            _out.WriteLine("Уже выключено.");
            return 0;
        }
        ClientRunner.Stop();
        _out.WriteLine("Маршрутизация выключена.");
        return 0;
    }
}
