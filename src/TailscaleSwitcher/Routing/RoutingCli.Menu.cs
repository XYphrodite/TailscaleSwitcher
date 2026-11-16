using System.Diagnostics;
using System.Text.Json;

namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    /// <summary>Подменю «Маршрутизация через другой ПК».</summary>
    public async Task RunMenuAsync()
    {
        while (true)
        {
            Ui.Clear();
            var cfg = _store.Load();
            var p = cfg.FindActiveOrOnly();
            var running = ClientRunner.GetStatus().Running;

            Ui.Title("Маршрутизация через другой ПК");
            if (running) Ui.Status("Состояние:", "ВКЛЮЧЕНА", ConsoleColor.Green);
            else Ui.Status("Состояние:", "выключена", ConsoleColor.DarkGray);
            if (p == null)
            {
                Ui.Status("Профиль:", "не создан — начни с пункта 4", ConsoleColor.Yellow, dot: false);
            }
            else
            {
                Ui.Status("Выходной узел:", $"{p.Name}  ({p.Node}:{p.Port})", ConsoleColor.Cyan, dot: false);
                Ui.Status("Режим:", ModeName(p), ConsoleColor.Cyan, dot: false);
                Ui.Status(p.AllTraffic ? "Исключений:" : "Правил:",
                    p.AllTraffic
                        ? $"{p.BypassApps.Count + p.BypassHosts.Count}"
                        : $"{p.Apps.Count} прил., {p.Hosts.Count} адр.",
                    ConsoleColor.Cyan, dot: false);
            }
            Ui.Rule();
            Ui.Item("1", running ? "Выключить маршрутизацию" : "Включить маршрутизацию");
            Ui.Item("2", "Режим: весь трафик / только выбранное", p == null ? null : $"сейчас: {(p.AllTraffic ? "весь трафик" : "выбранное")}");
            Ui.Item("3", p is { AllTraffic: true } ? "Исключения (идут напрямую)" : "Приложения и адреса");
            Ui.Item("4", "Профили выходных узлов");
            Ui.Item("5", "Подробный статус");
            Ui.Item("0", "Назад");
            Ui.Rule();

            switch (Ui.Prompt())
            {
                case "1":
                    if (running) await RunAsync(["route", "off"]);
                    else if (p == null) await ProfileWizardAsync();
                    else await RunAsync(["route", "on"]);
                    Ui.Pause();
                    break;
                case "2":
                    if (p == null) { Ui.Warn("  Сначала создай профиль (пункт 4)."); Ui.Pause(); break; }
                    await ChooseModeAsync(p, running);
                    break;
                case "3":
                    if (p == null) { Ui.Warn("  Сначала создай профиль (пункт 4)."); Ui.Pause(); break; }
                    await ListsMenuAsync(p.Name);
                    break;
                case "4":
                    await ProfilesMenuAsync();
                    break;
                case "5":
                    Console.WriteLine();
                    await RunAsync(["route", "status"]);
                    Console.WriteLine();
                    await RunAsync(["route", "list"]);
                    Ui.Pause();
                    break;
                case "0":
                case "":
                    return;
            }
        }
    }

    private async Task ChooseModeAsync(RouteProfile p, bool running)
    {
        Console.WriteLine();
        var pick = Ui.Pick("Что пускать через выходной узел?",
        [
            "ВЕСЬ интернет-трафик (кроме Tailscale, локальной сети и исключений)",
            "Только выбранные приложения и адреса"
        ]);
        if (pick == null) return;
        await RunAsync(["route", "mode", pick == 0 ? "all" : "selective", "--profile", p.Name]);
        if (pick == 0 && OperatingSystem.IsWindows() && Proxy.XeonProxyService.GetState().Enabled)
            Ui.Warn("  Внимание: системный прокси Windows будет временно отключён, пока маршрутизация включена.");
        await OfferApplyAsync(running);
    }

    /// <summary>Если туннель уже работает — предлагает перезапустить его с новыми настройками.</summary>
    private async Task OfferApplyAsync(bool running)
    {
        if (running && Ui.Confirm("Применить изменения сейчас (перезапуск туннеля)?"))
            await RunAsync(["route", "on"]);
        Ui.Pause();
    }

    // ───────────── списки приложений / адресов ─────────────

    private async Task ListsMenuAsync(string profileName)
    {
        var dirty = false;
        while (true)
        {
            Ui.Clear();
            var cfg = _store.Load();
            var p = cfg.Find(profileName);
            if (p == null) return;
            var bypass = p.AllTraffic;
            var apps = bypass ? p.BypassApps : p.Apps;
            var hosts = bypass ? p.BypassHosts : p.Hosts;

            Ui.Title(bypass ? "Исключения (идут напрямую, мимо узла)" : "Приложения и адреса (идут через узел)");
            Ui.Dim($"  профиль {p.Name}, режим: {ModeName(p)}");
            Console.WriteLine();
            var entries = new List<(bool isApp, string value)>();
            apps.ForEach(a => entries.Add((true, a)));
            hosts.ForEach(h => entries.Add((false, h)));
            if (entries.Count == 0) Ui.Dim("  (пусто)");
            for (var i = 0; i < entries.Count; i++)
                Ui.Item((i + 1).ToString(), entries[i].value, entries[i].isApp ? "приложение" : "адрес");
            Ui.Rule();
            Ui.Item("a", "Добавить приложение");
            Ui.Item("h", "Добавить адрес / домен / подсеть");
            Ui.Item("d", "Удалить по номеру");
            Ui.Item("0", "Назад");
            Ui.Rule();

            var choice = Ui.Prompt().ToLowerInvariant();
            if (choice is "0" or "") break;

            string[]? cmd = null;
            var prefix = bypass ? new[] { "route", "bypass" } : new[] { "route" };
            if (choice == "a")
            {
                var app = PickApp();
                if (!string.IsNullOrWhiteSpace(app)) cmd = [.. prefix, "app", "add", app];
            }
            else if (choice == "h")
            {
                Ui.Dim("  Примеры: example.com (с поддоменами), full:api.example.com, 1.2.3.4, 10.0.0.0/8");
                var host = Ui.Ask("Адрес");
                if (!string.IsNullOrWhiteSpace(host)) cmd = [.. prefix, "host", "add", host];
            }
            else if (choice == "d" || int.TryParse(choice, out _))
            {
                var numText = choice == "d" ? Ui.Ask("Номер для удаления") : choice;
                if (int.TryParse(numText, out var n) && n >= 1 && n <= entries.Count)
                {
                    var e = entries[n - 1];
                    cmd = [.. prefix, e.isApp ? "app" : "host", "remove", e.value];
                }
            }

            if (cmd != null)
            {
                Console.WriteLine();
                if (await RunAsync(cmd) == 0) dirty = true;
                Ui.Pause();
            }
        }

        if (dirty) await OfferApplyAsync(ClientRunner.GetStatus().Running);
    }

    /// <summary>Выбор приложения: из запущенных (с окном) или вручную.</summary>
    private static string? PickApp()
    {
        Console.WriteLine();
        var pick = Ui.Pick("Как указать приложение?",
            ["Выбрать из запущенных", "Ввести имя процесса или полный путь вручную"]);
        if (pick == null) return null;
        if (pick == 1)
        {
            Ui.Dim(@"  Например: chrome.exe  или  C:\Program Files\App\app.exe");
            return Ui.Ask("Имя или путь");
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(proc.MainWindowTitle)) names.Add(proc.ProcessName + ".exe");
            }
            catch { }
            finally { proc.Dispose(); }
        }
        var list = names.ToList();
        if (list.Count == 0) { Ui.Warn("  Не нашёл запущенных приложений с окном."); return null; }
        var idx = Ui.Pick("Запущенные приложения:", list);
        return idx == null ? null : list[idx.Value];
    }
}
