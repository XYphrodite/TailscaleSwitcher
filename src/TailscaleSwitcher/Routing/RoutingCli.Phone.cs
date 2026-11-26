using System.Net;

namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    private int PrintPhoneHelp()
    {
        _out.WriteLine("""
            Мост через телефон (точка доступа Wi-Fi / USB-модем + Happ с Allow LAN Connections).

            Использование:
              phone [status]                   статус моста и обнаруженного шлюза телефона
              phone probe [--ip IP]            проверить порты SOCKS5 (10808) и HTTP (10809) на телефоне
              phone on [опции]                 включить мост через телефон
              phone off [--clean]              выключить мост и вернуть исходный прокси/Happ (--clean = чистый сброс)
              phone toggle [опции]             переключить вкл/выкл
              phone set [опции]                сохранить настройки по умолчанию в routing.json
              phone config [--ip IP]           показать сгенерированный config sing-box для TUN

            Опции phone on / toggle:
              --mode tun|proxy                 tun = весь трафик через sing-box (нужен Администратор);
                                               proxy = только системный HTTP-прокси Windows (без Администратора)
              --ip <IP>                        IP телефона (по умолчанию определяется по шлюзу Wi-Fi / USB)
              --socks-port <N>                 порт SOCKS5 на телефоне (по умолчанию 10808)
              --http-port <N>                  порт HTTP на телефоне (по умолчанию 10809)
              --dns <IP>                       DNS через телефон для TUN (по умолчанию 1.1.1.1)
              --persist                        автозапуск TUN при старте Windows
              --force, -f                      не проверять доступность порта телефона перед запуском
              --skip-health                    не проверять google.com/generate_204 после старта TUN
              --no-stop-happ                   не останавливать локальный HappService на время работы TUN
              --no-ts-route                    не заворачивать Tailscale (0.0.0.0/0) через туннель телефона

            Опции phone set:
              --mode tun|proxy  --ip auto|<IP>  --socks-port <N>  --http-port <N>  --dns <IP>
              --stop-happ true|false  --ts-route true|false
            """);
        return 0;
    }

    private async Task<int> PhoneAsync(CliArgs a)
    {
        var sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var cfg = _store.Load();
        cfg.Phone ??= new PhoneSettings();

        switch (sub)
        {
            case "help":
                return PrintPhoneHelp();
            case "status":
                return await PhoneStatusAsync(cfg).ConfigureAwait(false);
            case "probe":
                return await PhoneProbeAsync(cfg, a).ConfigureAwait(false);
            case "on":
                return await PhoneOnAsync(cfg, a).ConfigureAwait(false);
            case "off":
                return await PhoneOffAsync(a.Has("--clean")).ConfigureAwait(false);
            case "toggle":
                return PhoneBridgeService.GetStatus().Active
                    ? await PhoneOffAsync(a.Has("--clean")).ConfigureAwait(false)
                    : await PhoneOnAsync(cfg, a).ConfigureAwait(false);
            case "set":
                return PhoneSet(cfg, a);
            case "config":
                return PhoneConfig(cfg, a);
            default:
                return Fail($"Неизвестная подкоманда '{sub}'. См. 'phone --help'.");
        }
    }

    private async Task<int> PhoneStatusAsync(RoutingConfig cfg)
    {
        var st = PhoneBridgeService.GetStatus();
        if (st.Active)
        {
            var modeText = st.ActiveMode == PhoneBridgeMode.Tun
                ? $"ВКЛЮЧЁН (TUN {SingBoxConfigBuilder.PhoneTunInterfaceName}, pid {st.TunPid})"
                : "ВКЛЮЧЁН (системный прокси Windows)";
            _out.WriteLine($"Мост телефона: {modeText}");
            _out.WriteLine($"Телефон:       {st.PhoneIp}:{st.Port}");
        }
        else
        {
            _out.WriteLine("Мост телефона: выключен");
        }

        _out.WriteLine($"Режим по ум.:  {(cfg.Phone.Mode == PhoneBridgeMode.Tun ? "TUN (весь трафик)" : "Proxy (системный HTTP-прокси)")}");
        _out.WriteLine(st.Gateway != null
            ? $"Шлюз сети:     {st.Gateway.GatewayIp} ({st.Gateway.Kind}: {st.Gateway.InterfaceName}, {st.Gateway.SubnetCidr})"
            : "Шлюз сети:     не найден (подключись к Wi-Fi/USB-модему телефона)");

        if (!string.IsNullOrWhiteSpace(cfg.Phone.PhoneIp))
            _out.WriteLine($"Заданный IP:   {cfg.Phone.PhoneIp}");

        var probe = await PhoneBridgeService.ProbeAsync(cfg.Phone, st.PhoneIp, timeoutMs: 1200).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(probe.PhoneIp))
        {
            _out.WriteLine($"Порты ({probe.PhoneIp}): SOCKS5 :{probe.SocksPort} {(probe.SocksOpen ? "OPEN" : "closed")}, HTTP :{probe.HttpPort} {(probe.HttpOpen ? "OPEN" : "closed")}");
        }
        if (st.HasBackup)
            _out.WriteLine("Снимок отката: сохранён (вернётся при 'phone off').");
        return 0;
    }

    private async Task<int> PhoneProbeAsync(RoutingConfig cfg, CliArgs a)
    {
        var probe = await PhoneBridgeService.ProbeAsync(cfg.Phone, a.Get("--ip"), timeoutMs: 2000).ConfigureAwait(false);
        if (string.IsNullOrEmpty(probe.PhoneIp))
            return Fail("Не найден активный Wi-Fi или USB-модем с шлюзом телефона. Укажи --ip <IP>.");

        _out.WriteLine($"Телефон:       {probe.PhoneIp}" + (probe.Gateway != null ? $" ({probe.Gateway.Kind}: {probe.Gateway.InterfaceName})" : ""));
        _out.WriteLine($"SOCKS5 :{probe.SocksPort}: {(probe.SocksOpen ? "ДОСТУПЕН" : "закрыт")}");
        _out.WriteLine($"HTTP   :{probe.HttpPort}: {(probe.HttpOpen ? "ДОСТУПЕН" : "закрыт")}");
        if (!probe.SocksOpen && !probe.HttpOpen)
        {
            _out.WriteLine("Подсказка: в Happ на телефоне включи Настройки -> Расширенные -> Allow LAN Connections и подключи VPN.");
            return 2;
        }
        return 0;
    }

    private async Task<int> PhoneOnAsync(RoutingConfig cfg, CliArgs a)
    {
        PhoneBridgeMode? mode = null;
        if (a.Get("--mode") is { } m)
        {
            if (!TryParsePhoneMode(m, out var parsedMode))
                return Fail("--mode: ожидается 'tun' или 'proxy'.");
            mode = parsedMode;
        }

        int? socksPort = null;
        if (a.Get("--socks-port") is { } sp)
        {
            if (!int.TryParse(sp, out var port) || port is < 1 or > 65535) return Fail("Неверный --socks-port.");
            socksPort = port;
        }

        int? httpPort = null;
        if (a.Get("--http-port") is { } hp)
        {
            if (!int.TryParse(hp, out var port) || port is < 1 or > 65535) return Fail("Неверный --http-port.");
            httpPort = port;
        }

        var opts = new PhoneStartOptions(
            Mode: mode,
            PhoneIp: a.Get("--ip"),
            SocksPort: socksPort,
            HttpPort: httpPort,
            DnsServer: a.Get("--dns"),
            PersistAtBoot: a.Has("--persist"),
            Force: a.Has("--force", "-f"),
            SkipHealthCheck: a.Has("--skip-health"),
            StopLocalHapp: a.Has("--no-stop-happ") ? false : null,
            RouteTailscaleViaPhone: a.Has("--no-ts-route") ? false : null);

        var res = await PhoneBridgeService.StartAsync(cfg.Phone, opts, msg => _out.WriteLine(msg)).ConfigureAwait(false);
        if (!res.Ok)
            return Fail(res.Message, res.ExitCode);

        _out.WriteLine(res.Message);
        return 0;
    }

    private async Task<int> PhoneOffAsync(bool clean)
    {
        var res = await PhoneBridgeService.StopAsync(clean, msg => _out.WriteLine(msg)).ConfigureAwait(false);
        if (!res.Ok)
            return Fail(res.Message, res.ExitCode);
        _out.WriteLine(res.Message);
        return 0;
    }

    private int PhoneSet(RoutingConfig cfg, CliArgs a)
    {
        var p = cfg.Phone;
        var changed = false;

        if (a.Get("--mode") is { } m)
        {
            if (!TryParsePhoneMode(m, out var parsedMode))
                return Fail("--mode: ожидается 'tun' или 'proxy'.");
            p.Mode = parsedMode;
            changed = true;
        }

        if (a.Get("--ip") is { } ip)
        {
            if (ip.Equals("auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ip))
            {
                p.PhoneIp = "";
            }
            else
            {
                if (!IPAddress.TryParse(ip.Trim(), out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return Fail($"Некорректный IPv4-адрес '{ip}'.");
                p.PhoneIp = addr.ToString();
            }
            changed = true;
        }

        if (a.Get("--socks-port") is { } sp)
        {
            if (!int.TryParse(sp, out var port) || port is < 1 or > 65535) return Fail("Неверный --socks-port.");
            p.SocksPort = port;
            changed = true;
        }

        if (a.Get("--http-port") is { } hp)
        {
            if (!int.TryParse(hp, out var port) || port is < 1 or > 65535) return Fail("Неверный --http-port.");
            p.HttpPort = port;
            changed = true;
        }

        if (a.Get("--dns") is { } dns)
        {
            if (string.IsNullOrWhiteSpace(dns)) return Fail("Пустой --dns.");
            p.DnsServer = dns.Trim();
            changed = true;
        }

        if (a.Get("--stop-happ") is { } sh)
        {
            if (!bool.TryParse(sh, out var v)) return Fail("--stop-happ: ожидается true или false.");
            p.StopLocalHapp = v;
            changed = true;
        }

        if (a.Get("--ts-route") is { } tr)
        {
            if (!bool.TryParse(tr, out var v)) return Fail("--ts-route: ожидается true или false.");
            p.RouteTailscaleViaPhone = v;
            changed = true;
        }

        if (changed)
            _store.Save(cfg);

        _out.WriteLine($"Настройки моста телефона ({(changed ? "сохранены" : "текущие")}):");
        _out.WriteLine($"  режим:        {p.Mode}");
        _out.WriteLine($"  IP телефона:  {(string.IsNullOrWhiteSpace(p.PhoneIp) ? "авто (шлюз Wi-Fi/USB)" : p.PhoneIp)}");
        _out.WriteLine($"  SOCKS5 / HTTP:{p.SocksPort} / {p.HttpPort}");
        _out.WriteLine($"  DNS:          {p.DnsServer}");
        _out.WriteLine($"  стоп Happ:    {p.StopLocalHapp}");
        _out.WriteLine($"  Tailscale:    {(p.RouteTailscaleViaPhone ? "через телефон" : "напрямую")}");
        return 0;
    }

    private int PhoneConfig(RoutingConfig cfg, CliArgs a)
    {
        var p = cfg.Phone;
        var effective = new PhoneSettings
        {
            PhoneIp = p.PhoneIp,
            SocksPort = int.TryParse(a.Get("--socks-port"), out var sp) && sp is >= 1 and <= 65535 ? sp : p.SocksPort,
            HttpPort = p.HttpPort,
            Mode = p.Mode,
            StopLocalHapp = p.StopLocalHapp,
            RouteTailscaleViaPhone = a.Has("--no-ts-route") ? false : p.RouteTailscaleViaPhone,
            DnsServer = a.Get("--dns") ?? p.DnsServer
        };

        var gateway = PhoneBridgeService.DetectPhoneGateway();
        var target = PhoneBridgeService.ResolvePhoneTarget(effective, a.Get("--ip"), gateway);
        if (!target.Ok) return Fail(target.Error!);

        _out.WriteLine(SingBoxConfigBuilder.BuildPhoneBridge(effective, target.PhoneIp, target.WifiSubnet, PhoneBridgeService.PhoneRunner.LogPath));
        return 0;
    }

    private static bool TryParsePhoneMode(string raw, out PhoneBridgeMode mode)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "tun":
                mode = PhoneBridgeMode.Tun;
                return true;
            case "proxy" or "http":
                mode = PhoneBridgeMode.Proxy;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
