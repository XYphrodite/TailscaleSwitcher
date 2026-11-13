namespace TailscaleSwitcher.Routing;

public sealed partial class RoutingCli
{
    /// <summary>Подменю «Маршрутизация приложений/адресов» в интерактивном режиме.</summary>
    public async Task RunMenuAsync()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine(new string('─', 55));
            Console.WriteLine("  Маршрутизация приложений/адресов через другой ПК");
            Console.WriteLine(new string('─', 55));
            Console.WriteLine("   1) Статус");
            Console.WriteLine("   2) Включить (route on)");
            Console.WriteLine("   3) Выключить (route off)");
            Console.WriteLine("   4) Профили, приложения, адреса");
            Console.WriteLine("   5) Добавить приложение");
            Console.WriteLine("   6) Добавить адрес/домен/подсеть");
            Console.WriteLine("   7) Удалить приложение или адрес");
            Console.WriteLine("   8) Добавить профиль узла");
            Console.WriteLine("   9) Выбрать активный профиль");
            Console.WriteLine("  10) Relay (клубный ПК): статус");
            Console.WriteLine("  11) Relay: init + info");
            Console.WriteLine("  12) Relay: запустить");
            Console.WriteLine("  13) Relay: остановить");
            Console.WriteLine("   0) Назад");
            Console.Write("  Выбери пункт > ");
            var choice = Console.ReadLine()?.Trim();

            string[]? cmd = choice switch
            {
                "1" => ["route", "status"],
                "2" => ["route", "on"],
                "3" => ["route", "off"],
                "4" => ["route", "list"],
                "5" => Ask("Приложение (chrome.exe или полный путь)", v => new[] { "route", "app", "add", v }),
                "6" => Ask("Адрес (example.com, full:host, 1.2.3.4, 10.0.0.0/8)", v => new[] { "route", "host", "add", v }),
                "7" => AskRemove(),
                "8" => AskProfile(),
                "9" => Ask("Имя профиля", v => new[] { "route", "profile", "use", v }),
                "10" => ["relay", "status"],
                "11" => ["relay", "init"],
                "12" => ["relay", "start"],
                "13" => ["relay", "stop"],
                "0" => null,
                _ => []
            };
            if (choice == "0") return;
            if (cmd is { Length: > 0 })
            {
                Console.WriteLine();
                await RunAsync(cmd);
            }
            Console.Write("\nНажми Enter...");
            Console.ReadLine();
        }
    }

    private static string[]? Ask(string prompt, Func<string, string[]> build)
    {
        Console.Write($"  {prompt}: ");
        var v = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(v) ? [] : build(v);
    }

    private static string[]? AskRemove()
    {
        Console.Write("  Удалить: 1) приложение  2) адрес > ");
        var kind = Console.ReadLine()?.Trim();
        if (kind is not ("1" or "2")) return [];
        return Ask("Значение", v => new[] { "route", kind == "1" ? "app" : "host", "remove", v });
    }

    private static string[]? AskProfile()
    {
        Console.Write("  Имя профиля (например club): ");
        var name = Console.ReadLine()?.Trim();
        Console.Write("  Tailscale IP relay-узла: ");
        var node = Console.ReadLine()?.Trim();
        Console.Write($"  Порт [{RelaySettings.DefaultPort}]: ");
        var port = Console.ReadLine()?.Trim();
        Console.Write("  Логин: ");
        var user = Console.ReadLine()?.Trim();
        Console.Write("  Пароль: ");
        var pass = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(node)) return [];
        var args = new List<string> { "route", "profile", "add", name, "--node", node };
        if (!string.IsNullOrWhiteSpace(port)) { args.Add("--port"); args.Add(port); }
        if (!string.IsNullOrWhiteSpace(user)) { args.Add("--user"); args.Add(user); }
        if (!string.IsNullOrWhiteSpace(pass)) { args.Add("--pass"); args.Add(pass); }
        return args.ToArray();
    }
}
