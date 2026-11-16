using System.Text.Json;

namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    // ───────────── профили выходных узлов ─────────────

    private async Task ProfilesMenuAsync()
    {
        while (true)
        {
            Ui.Clear();
            var cfg = _store.Load();
            Ui.Title("Профили выходных узлов");
            if (cfg.Profiles.Count == 0) Ui.Dim("  (профилей нет)");
            for (var i = 0; i < cfg.Profiles.Count; i++)
            {
                var p = cfg.Profiles[i];
                var active = string.Equals(p.Name, cfg.Active, StringComparison.OrdinalIgnoreCase);
                Ui.Item((i + 1).ToString(), $"{p.Name}  {p.Node}:{p.Port}",
                    $"{(p.AllTraffic ? "весь трафик" : "выбранное")}{(active ? "  [активный]" : "")}");
            }
            Ui.Rule();
            Ui.Item("a", "Добавить профиль (мастер)");
            Ui.Item("u", "Сделать активным");
            Ui.Item("r", "Удалить профиль");
            Ui.Item("0", "Назад");
            Ui.Rule();

            var choice = Ui.Prompt().ToLowerInvariant();
            if (choice is "0" or "") return;

            if (choice == "a")
            {
                await ProfileWizardAsync();
                Ui.Pause();
            }
            else if (choice is "u" or "r" && cfg.Profiles.Count > 0)
            {
                var idx = Ui.Pick(choice == "u" ? "Какой профиль сделать активным?" : "Какой профиль удалить?",
                    cfg.Profiles.Select(x => $"{x.Name}  {x.Node}:{x.Port}").ToList());
                if (idx != null)
                {
                    var name = cfg.Profiles[idx.Value].Name;
                    if (choice == "r" && !Ui.Confirm($"Удалить профиль '{name}'?", defaultYes: false)) continue;
                    await RunAsync(["route", "profile", choice == "u" ? "use" : "remove", name]);
                    Ui.Pause();
                }
            }
        }
    }

    /// <summary>Пошаговое создание профиля: узел из списка Tailscale, порт, логин/пароль, режим.</summary>
    private async Task ProfileWizardAsync()
    {
        Console.WriteLine();
        Ui.Section("Новый профиль выходного узла");
        Ui.Dim("  На выходном ПК выполни 'relay init' — он напечатает готовые значения.");
        Console.WriteLine();

        var name = Ui.Ask("Имя профиля", "club");

        string? node = null;
        var peers = ListTailscalePeers();
        if (peers.Count > 0)
        {
            var idx = Ui.Pick("Выбери выходной ПК из tailnet:",
                peers.Select(p => $"{p.Host,-24} {p.Ip,-16} {(p.Online ? "online" : "offline")}").ToList(),
                "0 — ввести IP вручную");
            if (idx != null) node = peers[idx.Value].Ip;
        }
        node ??= Ui.Ask("Tailscale IP выходного ПК");
        if (string.IsNullOrWhiteSpace(node)) { Ui.Warn("  Отменено."); return; }

        var port = Ui.Ask("Порт relay", RelaySettings.DefaultPort.ToString());
        var user = Ui.Ask("Логин", "club");
        var pass = Ui.Ask("Пароль (из 'relay init')");

        var mode = Ui.Pick("Что пускать через этот узел?",
            ["ВЕСЬ интернет-трафик", "Только выбранные приложения и адреса"]);

        var args = new List<string> { "route", "profile", "add", name, "--node", node, "--port", port, "--user", user, "--pass", pass };
        if (mode == 0) args.Add("--all");
        Console.WriteLine();
        if (await RunAsync([.. args]) == 0)
            Ui.Ok(mode == 0
                ? "  Готово. Включить: «Включить маршрутизацию» в этом меню."
                : "  Готово. Добавь приложения/адреса (пункт 3), затем включи маршрутизацию.");
    }

    private static List<(string Host, string Ip, bool Online)> ListTailscalePeers()
    {
        var result = new List<(string, string, bool)>();
        try
        {
            var exe = FindTailscalePath();
            var (ok, output) = RunProcess(exe, "status --json");
            if (!ok) return result;
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("Peer", out var peers)) return result;
            foreach (var peer in peers.EnumerateObject())
            {
                var v = peer.Value;
                var host = v.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : "";
                var ip = v.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0 ? ips[0].GetString() ?? "" : "";
                var online = v.TryGetProperty("Online", out var o) && o.GetBoolean();
                var os = v.TryGetProperty("OS", out var osEl) ? osEl.GetString() : null;
                if (ip.Length > 0 && string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase))
                    result.Add((host, ip, online));
            }
        }
        catch { }
        return result.OrderByDescending(p => p.Item3).ThenBy(p => p.Item1).ToList();
    }

    // ───────────── выходной ПК (relay) ─────────────

    public async Task RunRelayMenuAsync()
    {
        while (true)
        {
            Ui.Clear();
            var cfg = _store.Load();
            var r = cfg.Relay;
            var st = RelayRunner.GetStatus();

            Ui.Title("Этот компьютер как выходной ПК (relay)");
            if (st.Running) Ui.Status("Состояние:", "РАБОТАЕТ", ConsoleColor.Green);
            else Ui.Status("Состояние:", "остановлен", ConsoleColor.DarkGray);
            if (string.IsNullOrEmpty(r.Password))
            {
                Ui.Status("Настройка:", "не выполнена — пункт 1", ConsoleColor.Yellow, dot: false);
            }
            else
            {
                Ui.Status("Адрес:", $"{ResolveListenAddress(r) ?? "?"}:{r.Port}", ConsoleColor.Cyan, dot: false);
                Ui.Status("Дальше в интернет:", string.IsNullOrWhiteSpace(r.Upstream) || r.Upstream == "direct" ? "напрямую" : $"через {r.Upstream} (Happ)", ConsoleColor.Cyan, dot: false);
            }
            Ui.Rule();
            Ui.Item("1", string.IsNullOrEmpty(r.Password) ? "Настроить relay" : "Показать данные для подключения клиента");
            Ui.Item("2", st.Running ? "Перезапустить relay" : "Запустить relay");
            Ui.Item("3", "Запустить с автозапуском при загрузке Windows");
            Ui.Item("4", "Остановить relay");
            Ui.Item("5", "Открыть порт в Windows Firewall (только для Tailscale)");
            Ui.Item("6", "Изменить апстрим (Happ / напрямую)");
            Ui.Item("7", "Остановить и убрать автозапуск");
            Ui.Item("0", "Назад");
            Ui.Rule();

            string[]? cmd = null;
            switch (Ui.Prompt())
            {
                case "1": cmd = string.IsNullOrEmpty(r.Password) ? ["relay", "init"] : ["relay", "info"]; break;
                case "2":
                    if (st.Running) await RunAsync(["relay", "stop"]);
                    cmd = ["relay", "start"];
                    break;
                case "3":
                    if (st.Running) await RunAsync(["relay", "stop"]);
                    cmd = ["relay", "start", "--persist"];
                    break;
                case "4": cmd = ["relay", "stop"]; break;
                case "5": cmd = ["relay", "firewall"]; break;
                case "6":
                    var pick = Ui.Pick("Куда relay отправляет трафик дальше?",
                        [$"В Happ ({RelaySettings.DefaultUpstream}, HTTP)", "Напрямую (без Happ)", "Свой адрес host:port"]);
                    if (pick == null) break;
                    var up = pick == 0 ? RelaySettings.DefaultUpstream : pick == 1 ? "direct" : Ui.Ask("Адрес апстрима (host:port или socks://host:port)");
                    if (!string.IsNullOrWhiteSpace(up)) cmd = ["relay", "init", "--upstream", up];
                    break;
                case "7": cmd = ["relay", "uninstall"]; break;
                case "0":
                case "":
                    return;
            }

            if (cmd != null)
            {
                Console.WriteLine();
                await RunAsync(cmd);
                Ui.Pause();
            }
        }
    }
}
