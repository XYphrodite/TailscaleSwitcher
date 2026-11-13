namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    private int RouteProfileCmd(RoutingConfig cfg, List<string> rest, CliArgs a)
    {
        var action = rest.FirstOrDefault()?.ToLowerInvariant();
        var name = rest.Skip(1).FirstOrDefault();
        if (action is null or "list") return RouteList(cfg);
        if (string.IsNullOrWhiteSpace(name)) return Fail($"Укажи имя профиля: route profile {action} <имя>");

        switch (action)
        {
            case "add":
            {
                if (cfg.Find(name) != null) return Fail($"Профиль '{name}' уже есть.");
                var node = a.Get("--node");
                if (string.IsNullOrWhiteSpace(node)) return Fail("Нужен --node <tailscale-ip или имя узла>.");
                var port = RelaySettings.DefaultPort;
                if (a.Get("--port") is { } ps && (!int.TryParse(ps, out port) || port is < 1 or > 65535))
                    return Fail("Неверный --port.");
                var p = new RouteProfile
                {
                    Name = name,
                    Node = node.Trim(),
                    Port = port,
                    Username = a.Get("--user") ?? "",
                    Password = a.Get("--pass") ?? "",
                    TailscaleInterface = a.Get("--iface") ?? "Tailscale",
                    DnsServer = a.Get("--dns") ?? "1.1.1.1",
                    RemoteDns = !a.Has("--no-remote-dns")
                };
                cfg.Profiles.Add(p);
                cfg.Active ??= p.Name;
                _store.Save(cfg);
                _out.WriteLine($"Профиль '{name}' добавлен.");
                return 0;
            }
            case "remove":
            {
                var p = cfg.Find(name);
                if (p == null) return Fail($"Профиль '{name}' не найден.");
                cfg.Profiles.Remove(p);
                if (string.Equals(cfg.Active, p.Name, StringComparison.OrdinalIgnoreCase))
                    cfg.Active = cfg.Profiles.FirstOrDefault()?.Name;
                _store.Save(cfg);
                _out.WriteLine($"Профиль '{name}' удалён.");
                return 0;
            }
            case "use":
            {
                var p = cfg.Find(name);
                if (p == null) return Fail($"Профиль '{name}' не найден.");
                cfg.Active = p.Name;
                _store.Save(cfg);
                _out.WriteLine($"Активный профиль: {p.Name}. Применить: route on");
                return 0;
            }
            default: return Fail($"Неизвестное действие '{action}'. Ожидается: add | remove | use | list.");
        }
    }

    private int RouteListEdit(RoutingConfig cfg, List<string> rest, CliArgs a, bool isApp)
    {
        var action = rest.FirstOrDefault()?.ToLowerInvariant();
        var value = rest.Skip(1).FirstOrDefault();
        var kind = isApp ? "app" : "host";
        if (action is not ("add" or "remove") || string.IsNullOrWhiteSpace(value))
            return Fail($"Использование: route {kind} add|remove <значение> [--profile имя]");

        var profile = a.Get("--profile") is { } n ? cfg.Find(n) : cfg.FindActiveOrOnly();
        if (profile == null) return Fail("Профиль не найден. Создай: route profile add ... или укажи --profile.");

        value = value.Trim().Trim('"');
        List<string> list = isApp ? profile.Apps : profile.Hosts;

        if (!isApp)
        {
            var parsed = HostRuleParser.Parse(value);
            if (parsed.Kind == HostRuleKind.Invalid) return Fail($"Не удалось разобрать адрес '{value}'.");
        }

        var existing = list.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (action == "add")
        {
            if (existing != null) { _out.WriteLine("Уже есть."); return 0; }
            list.Add(value);
        }
        else
        {
            if (existing == null) return Fail($"'{value}' нет в профиле '{profile.Name}'.");
            list.Remove(existing);
        }

        _store.Save(cfg);
        _out.WriteLine($"{(action == "add" ? "Добавлено" : "Удалено")}: {value} (профиль {profile.Name}). Если маршрутизация включена — повтори 'route on'.");
        return 0;
    }
}
