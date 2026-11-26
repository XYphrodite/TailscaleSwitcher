using System.Runtime.InteropServices;
using System.Text;
using TailscaleSwitcher.Proxy;
using TailscaleSwitcher.Routing;
using TailscaleSwitcher.Update;

namespace TailscaleSwitcher.Services;

/// <summary>
/// Обрабатывает CLI-аргументы, сохраняя полную совместимость со сценариями из терминала и скриптов.
/// </summary>
internal static class CliRunner
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static bool ShouldHandleAsCli(string[] args)
    {
        if (args.Length == 0) return false;
        // -Tray запускает GUI свёрнутым в трей
        if (args.Length == 1 && args[0].Equals("-Tray", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        EnsureConsoleAttached();
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var first = args[0].ToLowerInvariant();
        if (first is "--help" or "-h" or "-?" or "help")
        {
            PrintHelp();
            return 0;
        }
        if (first is "--version" or "-v" or "version")
        {
            Console.WriteLine(AppVersion.Informational);
            return 0;
        }
        if (first is "proxy" or "px" or "xeon-proxy")
            return await HandleProxyCliAsync(args).ConfigureAwait(false);

        if (first is "route" or "rt" or "relay" or "phone" or "ph")
            return await new RoutingCli().RunAsync(args).ConfigureAwait(false);

        if (first is "update" or "--update" or "--apply-update")
        {
            var checkOnly = args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
            return await RunUpdateAsync(checkOnly).ConfigureAwait(false);
        }

        Console.Error.WriteLine($"Неизвестная команда '{args[0]}'. См. 'TailscaleSwitcher --help'.");
        return 1;
    }

    private static void EnsureConsoleAttached()
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected) return;
        AttachConsole(AttachParentProcess);
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"TailscaleSwitcher {AppVersion.Informational}");
        Console.WriteLine("Использование:");
        Console.WriteLine("  TailscaleSwitcher                — открыть графическую оболочку");
        Console.WriteLine("  TailscaleSwitcher -Tray          — запустить свёрнутым в системный трей");
        Console.WriteLine("  TailscaleSwitcher proxy [status] — статус системного прокси через Xeon (Happ)");
        Console.WriteLine("  TailscaleSwitcher proxy on       — включить прокси через Xeon (192.168.1.5 / 100.119.48.15)");
        Console.WriteLine("  TailscaleSwitcher proxy off      — выключить системный прокси (прямой выход)");
        Console.WriteLine("  TailscaleSwitcher proxy toggle   — переключить прокси вкл/выкл");
        Console.WriteLine("    Опции proxy: --server <host[:port]>  --force  --winhttp");
        Console.WriteLine("  TailscaleSwitcher route [on|off|list|profile|app|host|config] — маршрутизация приложений/адресов (см. route --help)");
        Console.WriteLine("  TailscaleSwitcher relay [init|start|stop|status|info|firewall] — серверная часть на выходном ПК");
        Console.WriteLine("  TailscaleSwitcher phone [on|off|toggle|status|set|config] — мост через Wi-Fi телефона + Happ (см. phone --help)");
        Console.WriteLine("  TailscaleSwitcher update         — обновить из GitHub Releases");
        Console.WriteLine("  TailscaleSwitcher update --check — проверить наличие обновления");
        Console.WriteLine("  TailscaleSwitcher --version      — показать версию");
    }

    private static async Task<int> HandleProxyCliAsync(string[] cliArgs)
    {
        var opts = XeonProxyService.ParseCli(cliArgs);
        if (opts.Error != null)
        {
            Console.Error.WriteLine($"Ошибка: {opts.Error}");
            Console.Error.WriteLine("Справка: TailscaleSwitcher proxy --help");
            return 1;
        }
        if (opts.Action == ProxyAction.Help)
        {
            PrintHelp();
            return 0;
        }

        var current = XeonProxyService.GetState();
        var action = opts.Action == ProxyAction.Toggle
            ? (current.Enabled ? ProxyAction.Disable : ProxyAction.Enable)
            : opts.Action;

        if (action == ProxyAction.Status)
        {
            Console.WriteLine($"Системный прокси: {(current.Enabled ? $"ВКЛЮЧЕН -> {current.Server}" : "ВЫКЛЮЧЕН (прямой выход)")}");
            var (resolved, ok) = await XeonProxyService.ResolveEndpointAsync(opts.CustomServer).ConfigureAwait(false);
            Console.WriteLine($"Доступность Xeon: {(ok ? $"доступен ({resolved})" : "недоступен (ни 192.168.1.5:10809, ни 100.119.48.15:10809)")}");
            var egress = await XeonProxyService.CheckEgressAsync(current).ConfigureAwait(false);
            Console.WriteLine(FormatEgress("Внешний IP:      ", egress));
            return 0;
        }

        if (action == ProxyAction.Enable)
        {
            var (endpoint, reachable) = await XeonProxyService.ResolveEndpointAsync(opts.CustomServer).ConfigureAwait(false);
            if (!reachable && !opts.Force)
            {
                Console.Error.WriteLine($"Прокси на Xeon не отвечает ({opts.CustomServer ?? "192.168.1.5:10809 / 100.119.48.15:10809"}).");
                Console.Error.WriteLine("Проверь, что xeon включен и на нем запущен Happ (или передай --force).");
                return 2;
            }
            XeonProxyService.Enable(endpoint, alsoWinHttp: opts.AlsoWinHttp, isAdmin: TailscaleService.IsAdmin());
            Console.WriteLine($"Прокси через Xeon включен -> {endpoint}");
            var egress = await XeonProxyService.CheckEgressAsync(XeonProxyService.GetState()).ConfigureAwait(false);
            if (egress != null) Console.WriteLine(FormatEgress("Внешний IP: ", egress));
            return 0;
        }

        if (action == ProxyAction.Disable)
        {
            XeonProxyService.Disable(isAdmin: TailscaleService.IsAdmin());
            Console.WriteLine("Системный прокси выключен (прямой выход).");
            var egress = await XeonProxyService.CheckEgressAsync(XeonProxyService.GetState()).ConfigureAwait(false);
            if (egress != null) Console.WriteLine(FormatEgress("Внешний IP: ", egress));
            return 0;
        }

        return 0;
    }

    private static string FormatEgress(string prefix, EgressInfo? egress)
    {
        if (egress == null) return $"{prefix}не удалось проверить";
        var loc = string.Join(", ", new[] { egress.City, egress.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(loc) ? $"{prefix}{egress.Ip}" : $"{prefix}{egress.Ip} ({loc})";
    }

    private static async Task<int> RunUpdateAsync(bool checkOnly)
    {
        if (!AppVersion.CanSelfUpdate(Environment.ProcessPath))
        {
            Console.Error.WriteLine("Self-update доступен только для установленной копии (не из bin/).");
            return 1;
        }
        try
        {
            if (checkOnly)
            {
                var rep = await AppUpdater.CheckAsync().ConfigureAwait(false);
                if (rep.Status == SelfUpdateStatus.AlreadyCurrent)
                {
                    Console.WriteLine($"TailscaleSwitcher {rep.Installed} уже актуален.");
                    return 0;
                }
                Console.WriteLine($"Доступно обновление: {rep.Installed} -> {rep.Release} ({rep.Tag}).");
                Console.WriteLine("Запусти 'TailscaleSwitcher update'.");
                return 0;
            }

            Console.WriteLine($"TailscaleSwitcher {AppVersion.Informational}. Проверка GitHub Releases...");
            var report = await AppUpdater.ApplyInProcessAsync((recv, total) =>
            {
                var pct = total is > 0 ? (int)(100.0 * recv / total.Value) : 0;
                Console.Write($"\r  {pct,3}%  {recv / 1048576.0:0.0}/{(total ?? recv) / 1048576.0:0.0} MB   ");
            }).ConfigureAwait(false);
            if (report.Status == SelfUpdateStatus.AlreadyCurrent)
                Console.WriteLine($"Уже актуален: {report.Installed}");
            else
                Console.WriteLine($"\nУстановлено {report.Tag}. Перезапусти приложение.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
