using System.Security.Principal;

namespace TailscaleSwitcher.Routing;

/// <summary>Команды `route ...` (клиент на moscow_enjoyer) и `relay ...` (сервер на клубном ПК).</summary>
public sealed partial class RoutingCli
{
    private readonly RoutingConfigStore _store;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public RoutingCli(RoutingConfigStore? store = null, TextWriter? output = null, TextWriter? error = null)
    {
        _store = store ?? new RoutingConfigStore();
        _out = output ?? Console.Out;
        _err = error ?? Console.Error;
    }

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>args[0] — "route" или "relay".</summary>
    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) return Fail("Пустая команда.");
        var parsed = CliArgs.Parse(args.Skip(1));
        if (parsed.Error != null) return Fail(parsed.Error);

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "route" or "rt" => parsed.Has("--help", "-h") ? PrintHelp() : await RouteAsync(parsed),
                "relay" => parsed.Has("--help", "-h") ? PrintHelp() : await RelayAsync(parsed),
                _ => Fail($"Неизвестная команда '{args[0]}'.")
            };
        }
        catch (InvalidDataException ex) { return Fail(ex.Message); }
        catch (HttpRequestException ex) { return Fail($"Сеть: {ex.Message}"); }
    }

    private int Fail(string message, int code = 1)
    {
        _err.WriteLine($"Ошибка: {message}");
        return code;
    }

    private int PrintHelp()
    {
        _out.WriteLine("""
            Маршрутизация выбранных приложений и адресов через другой ПК (sing-box поверх Tailscale).

            Клиент (moscow_enjoyer, нужен запуск от Администратора для on/off):
              route                                 статус
              route on [профиль]                    включить (TUN), перезапускает при повторном вызове
              route off                             выключить
              route list                            профили, приложения, адреса
              route profile add <имя> --node <tailscale-ip> [--port 1081] [--user U] [--pass P]
                                      [--iface Tailscale] [--dns 1.1.1.1] [--no-remote-dns]
              route profile remove|use <имя>
              route app  add|remove <chrome.exe | C:\путь\app.exe> [--profile имя]
              route host add|remove <example.com | full:host | 1.2.3.4 | 10.0.0.0/8> [--profile имя]
              route config [--profile имя]          показать сгенерированный config sing-box

            Сервер (клубный ПК):
              relay init [--listen ip] [--port 1081] [--user U] [--pass P] [--upstream 127.0.0.1:10809|socks://h:p|direct]
              relay start [--persist] | stop | uninstall | status   (start/uninstall — от Администратора; --persist — автозапуск при загрузке)
              relay info                            команда для настройки клиента
              relay firewall                        правило Windows Firewall для сети Tailscale (Администратор)
            """);
        return 0;
    }
}
