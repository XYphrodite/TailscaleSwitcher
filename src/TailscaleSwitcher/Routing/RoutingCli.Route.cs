using TailscaleSwitcher.Proxy;

namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    private static readonly SingBoxRunner ClientRunner = new("client");

    private static string ProxyRestoreMarker => Path.Combine(RoutingPaths.RunDir, "client.proxy-restore");

    public static RunnerStatus ClientStatus() => ClientRunner.GetStatus();

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
            case "mode": return RouteMode(cfg, rest, a);
            case "profile": return RouteProfileCmd(cfg, rest, a);
            case "app": return RouteListEdit(cfg, rest, a, isApp: true);
            case "host": return RouteListEdit(cfg, rest, a, isApp: false);
            case "bypass":
            {
                var what = rest.FirstOrDefault()?.ToLowerInvariant();
                if (what is not ("app" or "host"))
                    return Fail("Использование: route bypass app|host add|remove <значение> [--profile имя]");
                return RouteListEdit(cfg, rest.Skip(1).ToList(), a, isApp: what == "app", bypass: true);
            }
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

    private int RouteMode(RoutingConfig cfg, List<string> rest, CliArgs a)
    {
        var mode = rest.FirstOrDefault()?.ToLowerInvariant();
        var profile = a.Get("--profile") is { } n ? cfg.Find(n) : cfg.FindActiveOrOnly();
        if (profile == null) return Fail("Профиль не найден. См. 'route list'.");
        if (mode is null)
        {
            _out.WriteLine($"Профиль '{profile.Name}': режим {ModeName(profile)}.");
            return 0;
        }
        if (mode is not ("all" or "selective"))
            return Fail("Использование: route mode all|selective [--profile имя]");
        profile.AllTraffic = mode == "all";
        _store.Save(cfg);
        _out.WriteLine($"Профиль '{profile.Name}': режим {ModeName(profile)}. Если маршрутизация включена — повтори 'route on'.");
        return 0;
    }

    public static string ModeName(RouteProfile p) =>
        p.AllTraffic ? "весь трафик" : "только выбранные приложения и адреса";

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
        _out.WriteLine($"Режим:         {ModeName(p)}");
        if (p.AllTraffic)
            _out.WriteLine($"Исключения:    приложений {p.BypassApps.Count}, адресов {p.BypassHosts.Count}");
        else
            _out.WriteLine($"Правила:       приложений {p.Apps.Count}, адресов {p.Hosts.Count}");
        var ok = await XeonProxyService.ProbeTcpAsync($"{p.Node}:{p.Port}", 2000);
        _out.WriteLine($"Relay:         {(ok ? "доступен" : "недоступен")}");
        if (File.Exists(ProxyRestoreMarker))
            _out.WriteLine("Системный прокси Windows временно отключён (вернётся при 'route off').");
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
            _out.WriteLine($"    режим:      {ModeName(p)}");
            if (p.AllTraffic)
            {
                _out.WriteLine($"    не через узел (приложения): {(p.BypassApps.Count == 0 ? "-" : string.Join(", ", p.BypassApps))}");
                _out.WriteLine($"    не через узел (адреса):     {(p.BypassHosts.Count == 0 ? "-" : string.Join(", ", p.BypassHosts))}");
            }
            else
            {
                _out.WriteLine($"    приложения: {(p.Apps.Count == 0 ? "-" : string.Join(", ", p.Apps))}");
                _out.WriteLine($"    адреса:     {(p.Hosts.Count == 0 ? "-" : string.Join(", ", p.Hosts))}");
            }
        }
        return 0;
    }

    private async Task<int> RouteOnAsync(RoutingConfig cfg, string? name, bool force)
    {
        var p = name != null ? cfg.Find(name) : cfg.FindActiveOrOnly();
        if (p == null) return Fail(name != null ? $"Профиль '{name}' не найден." : "Профиль не выбран. См. 'route list'.");
        if (!p.AllTraffic && p.Apps.Count == 0 && p.Hosts.Count == 0)
            return Fail($"В профиле '{p.Name}' нет ни приложений, ни адресов. Добавь их (route app add / route host add) или включи режим: route mode all.");
        if (!IsAdmin())
            return Fail("Для TUN нужен запуск от имени Администратора.", 3);

        if (!await XeonProxyService.ProbeTcpAsync($"{p.Node}:{p.Port}", 3000) && !force)
            return Fail($"Relay {p.Node}:{p.Port} не отвечает. Запущен ли 'relay start' на выходном ПК и открыт ли firewall? (--force чтобы игнорировать)", 2);

        var exe = await SingBoxInstaller.EnsureAsync(m => _out.WriteLine(m));
        if (ClientRunner.GetStatus().Running) ClientRunner.Stop();

        var err = await ClientRunner.StartAsync(exe, SingBoxConfigBuilder.BuildClient(p, ClientRunner.LogPath));
        if (err != null) return Fail(err);

        cfg.Active = p.Name;
        _store.Save(cfg);

        if (p.AllTraffic) SuspendSystemProxy(); else RestoreSystemProxy();

        _out.WriteLine(p.AllTraffic
            ? $"Включено: профиль '{p.Name}' -> {p.Node}:{p.Port}. ВЕСЬ трафик идёт через этот узел (исключения: приложений {p.BypassApps.Count}, адресов {p.BypassHosts.Count})."
            : $"Включено: профиль '{p.Name}' -> {p.Node}:{p.Port}. Приложений: {p.Apps.Count}, адресов: {p.Hosts.Count}.");
        return 0;
    }

    private int RouteOff()
    {
        var wasRunning = ClientRunner.GetStatus().Running;
        if (wasRunning) ClientRunner.Stop();
        var restored = RestoreSystemProxy();
        if (!wasRunning && !restored)
        {
            _out.WriteLine("Уже выключено.");
            return 0;
        }
        _out.WriteLine("Маршрутизация выключена." + (restored ? " Системный прокси Windows возвращён." : ""));
        return 0;
    }

    /// <summary>
    /// В режиме «весь трафик» системный прокси (например, Happ 127.0.0.1:10809) отключаем:
    /// иначе приложения ходят в него по loopback и мимо TUN. При выключении возвращаем.
    /// </summary>
    private void SuspendSystemProxy()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!XeonProxyService.GetState().Enabled) return;
            Directory.CreateDirectory(RoutingPaths.RunDir);
            File.WriteAllText(ProxyRestoreMarker, "1");
            XeonProxyService.SetEnabled(false);
            _out.WriteLine("Системный прокси Windows временно отключён (вернётся при 'route off').");
        }
        catch (Exception ex) { _err.WriteLine($"Не удалось отключить системный прокси: {ex.Message}"); }
    }

    private bool RestoreSystemProxy()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(ProxyRestoreMarker)) return false;
        try
        {
            XeonProxyService.SetEnabled(true);
            File.Delete(ProxyRestoreMarker);
            return true;
        }
        catch (Exception ex) { _err.WriteLine($"Не удалось вернуть системный прокси: {ex.Message}"); return false; }
    }
}
