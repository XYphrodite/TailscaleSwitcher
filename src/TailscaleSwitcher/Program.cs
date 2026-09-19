using System.Diagnostics;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using TailscaleSwitcher.Update;

// Tailscale Exit Node Switcher — Windows, интерактивное меню
// Позволяет пустить весь интернет-трафик через другой комп в Tailnet (один внешний IP)

// CLI: --help / --version / update [--check]  (как в TerminalV)
if (args.Length > 0)
{
    var first = args[0].ToLowerInvariant();
    if (first is "--help" or "-h" or "-?" or "help")
    {
        Console.WriteLine($"TailscaleSwitcher {AppVersion.Informational}");
        Console.WriteLine("Использование:");
        Console.WriteLine("  TailscaleSwitcher                — интерактивное меню (Windows)");
        Console.WriteLine("  TailscaleSwitcher update         — обновить из GitHub Releases");
        Console.WriteLine("  TailscaleSwitcher update --check — проверить наличие обновления");
        Console.WriteLine("  TailscaleSwitcher --version      — показать версию");
        return;
    }
    if (first is "--version" or "-v" or "version")
    {
        Console.WriteLine(AppVersion.Informational);
        return;
    }
    if (first == "update")
    {
        var checkOnly = args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
        if (!AppVersion.CanSelfUpdate(Environment.ProcessPath))
        {
            Console.Error.WriteLine("Self-update доступен только для установленной копии (не из bin/).");
            Environment.Exit(1);
        }
        try
        {
            using var source = new GitHubReleaseSource();
            var svc = new SelfUpdateService(Environment.ProcessPath!, AppVersion.Current, source);
            if (checkOnly)
            {
                var rep = svc.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (rep.Status == SelfUpdateStatus.AlreadyCurrent) Console.WriteLine($"TailscaleSwitcher {rep.Installed} уже актуален.");
                else Console.WriteLine($"Доступно обновление: {rep.Installed} -> {rep.Release} ({rep.Tag}). Запусти 'TailscaleSwitcher update'.");
                return;
            }
            Console.WriteLine($"TailscaleSwitcher {AppVersion.Informational}. Проверка GitHub Releases...");
            var report = svc.ApplyAsync(CancellationToken.None, (recv, total) =>
            {
                var pct = total is > 0 ? (int)(100.0 * recv / total.Value) : 0;
                Console.Write($"\r  {pct,3}%  {recv / 1048576.0:0.0}/{ (total ?? recv) / 1048576.0:0.0} MB   ");
            }).GetAwaiter().GetResult();
            if (report.Status == SelfUpdateStatus.AlreadyCurrent) Console.WriteLine($"Уже актуален: {report.Installed}");
            else Console.WriteLine($"\nУстановлено {report.Tag}. Перезапусти приложение.");
            return;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.Exit(1); }
    }
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "Tailscale Exit Node Switcher";

if (!IsAdmin())
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("⚠  Запусти от имени Администратора, иначе переключение exit-node не сработает.");
    Console.WriteLine("   Правый клик по .exe -> Запуск от имени администратора");
    Console.ResetColor();
    Console.WriteLine();
}

var tailscaleExe = FindTailscaleExe();
if (tailscaleExe == null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("❌ Не найден tailscale.exe");
    Console.WriteLine("   Проверь что Tailscale установлен и в PATH");
    Console.WriteLine(@"   Ожидается: C:\Program Files\Tailscale\tailscale.exe");
    Console.ResetColor();
    Console.WriteLine("Нажми Enter...");
    Console.ReadLine();
    return;
}

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"✓ Найден Tailscale: {tailscaleExe}");
Console.ResetColor();

while (true)
{
    Console.WriteLine();
    Console.WriteLine(new string('═', 55));
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine("  Tailscale Exit Node Switcher (Windows)");
    Console.ResetColor();
    Console.WriteLine(new string('═', 55));

    // быстрый статус в шапке
    var prefs = GetPrefs();
    var currentExit = prefs?.ExitNodeIP;
    var hasExit = !string.IsNullOrWhiteSpace(currentExit);
    Console.Write("  Текущий exit-node: ");
    if (hasExit)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{currentExit} (ID: {prefs!.ExitNodeID})");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("не используется (прямой выход в инет)");
    }
    Console.ResetColor();
    if (prefs != null)
        Console.WriteLine($"  LAN доступ: {(prefs.ExitNodeAllowLANAccess ? "разрешен" : "запрещен")}   |   RouteAll: {prefs.RouteAll}");
    Console.WriteLine(new string('─', 55));
    Console.WriteLine("  1) Показать статус (tailscale status)");
    Console.WriteLine("  2) Показать доступные exit-node");
    Console.WriteLine("  3) Подключиться через exit-node");
    Console.WriteLine("  4) Отключиться от exit-node (вернуть прямой выход)");
    Console.WriteLine("  5) Проверить внешний IP (мой IP в инете)");
    Console.WriteLine("  6) Показать мой Tailscale IP");
    Console.WriteLine("  7) Открыть админку Tailnet в браузере");
    Console.WriteLine("  8) Авто-фикс Happ + exit-node (r1600 -> Happ -> xeon)");
    Console.WriteLine("  0) Выход");
    Console.WriteLine(new string('─', 55));
    Console.Write("  Выбери пункт > ");
    var choice = Console.ReadLine()?.Trim();

    switch (choice)
    {
        case "1": ShowStatus(); break;
        case "2": ShowExitNodes(); break;
        case "3": ConnectExitNode(); break;
        case "4": DisconnectExitNode(); break;
        case "5": await CheckPublicIpAsync(); break;
        case "6": ShowTailscaleIp(); break;
        case "7": OpenAdmin(); break;
        case "8": await AutoFixHappExitNodeAsync(); break;
        case "0": return;
        default:
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Неизвестный пункт.");
            Console.ResetColor();
            break;
    }
    Console.WriteLine();
    Console.Write("Нажми Enter для меню...");
    Console.ReadLine();
    try { if (!Console.IsOutputRedirected) Console.Clear(); } catch { }
}

// ───────────────── helpers ─────────────────

bool IsAdmin()
{
    try
    {
        using var id = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
    catch { return false; }
}

string? FindTailscaleExe()
{
    var candidates = new[]
    {
        @"C:\Program Files\Tailscale\tailscale.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
        "tailscale.exe",
        "tailscale"
    };
    foreach (var c in candidates)
    {
        if (c.Contains(":\\") && File.Exists(c)) return c;
        // try where
        var (ok, outp, _) = RunRaw(c, "--version", 3000);
        if (ok && outp.Contains("tailscale", StringComparison.OrdinalIgnoreCase)) return c;
    }
    // last try via where
    var (ok2, out2, _) = RunRaw("where", "tailscale.exe", 3000);
    if (ok2 && !string.IsNullOrWhiteSpace(out2))
    {
        var first = out2.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (File.Exists(first)) return first;
    }
    return null;
}

(string okOutput, string error, int code) RunTailscale(string args, int timeoutMs = 10000)
{
    var (ok, outp, err, code) = RunRawFull(tailscaleExe!, args, timeoutMs);
    return (outp, err, code);
}

(bool ok, string output, string error, int exitCode) RunRawFull(string exe, string args, int timeoutMs)
{
    try
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            return (false, "", "timeout", -1);
        }
        var outStr = stdout.Result;
        var errStr = stderr.Result;
        return (p.ExitCode == 0, outStr, errStr, p.ExitCode);
    }
    catch (Exception ex) { return (false, "", ex.Message, -1); }
}

(bool ok, string output, string error) RunRaw(string exe, string args, int timeoutMs)
{
    var (ok, o, e, _) = RunRawFull(exe, args, timeoutMs);
    return (ok, o, e);
}

void ShowStatus()
{
    Console.WriteLine();
    Console.WriteLine("── tailscale status ──");
    var (outp, err, code) = RunTailscale("status");
    if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine(outp);
    if (!string.IsNullOrWhiteSpace(err)) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(err); Console.ResetColor(); }
    if (code != 0) Console.WriteLine($"(код {code})");

    Console.WriteLine();
    Console.WriteLine("── debug prefs (текущие настройки) ──");
    var prefs = GetPrefsRaw();
    if (prefs != null) Console.WriteLine(prefs);
}

void ShowTailscaleIp()
{
    var (outp, err, _) = RunTailscale("ip");
    Console.WriteLine();
    Console.WriteLine("Твои Tailscale IP:");
    if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine(outp.Trim());
    if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);
}

void ShowExitNodes()
{
    Console.WriteLine();
    Console.WriteLine("── Доступные exit-node ──");
    var nodes = GetExitNodes();
    if (nodes.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Нет доступных exit-node в этом tailnet.");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Чтобы появился список:");
        Console.WriteLine("  1) На компе-шлюзе выполни:  tailscale up --advertise-exit-node");
        Console.WriteLine("  2) Подтверди в админке: https://login.tailscale.com/admin/machines");
        Console.WriteLine("     -> выбери машину -> Edit route settings -> Use as exit node");
        return;
    }
    Console.WriteLine($"{"#",2}  {"HOSTNAME",-20} {"TAILSCALE IP",-16} {"ONLINE",-7} {"COUNTRY",-10} EXIT?");
    Console.WriteLine(new string('─', 70));
    for (int i = 0; i < nodes.Count; i++)
    {
        var n = nodes[i];
        Console.WriteLine($"{i + 1,2}  {n.HostName,-20} {n.TailscaleIP,-16} {(n.Online ? "online" : "offline"),-7} {n.Location,-10} {(n.IsExitNode ? "★" : "")}");
    }
}

void ConnectExitNode()
{
    var nodes = GetExitNodes();
    if (nodes.Count == 0)
    {
        ShowExitNodes();
        return;
    }
    ShowExitNodes();
    Console.WriteLine();
    Console.Write($"Выбери номер (1-{nodes.Count}) или введи IP/имя вручную: ");
    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(input)) return;

    string target;
    if (int.TryParse(input, out var idx) && idx >= 1 && idx <= nodes.Count)
        target = nodes[idx - 1].TailscaleIP;
    else
        target = input;

    Console.Write("Разрешить доступ к локалке (LAN) через exit-node? (y/N): ");
    var lan = Console.ReadLine()?.Trim().ToLowerInvariant() == "y";
    var lanFlag = lan ? " --exit-node-allow-lan-access=true" : " --exit-node-allow-lan-access=false";

    Console.WriteLine();
    Console.WriteLine($"Подключаюсь через {target} ...");
    // tailscale set --exit-node=<ip>  — предпочтительнее чем up
    var (outp, err, code) = RunTailscale($"set --exit-node={target}{lanFlag}");
    if (code == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✓ Готово. Весь трафик теперь идет через выбранный exit-node.");
        Console.ResetColor();
        if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine(outp);
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ Ошибка (код {code})");
        Console.ResetColor();
        if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);
        if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine(outp);
        Console.WriteLine("Подсказка: убедись что exit-node подтвержден в админке и ты запустил от Администратора.");
    }
}

void DisconnectExitNode()
{
    Console.WriteLine();
    Console.WriteLine("Отключаю exit-node (возврат к прямому выходу)...");
    var (outp, err, code) = RunTailscale("set --exit-node=");
    if (code == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✓ Отключено. Трафик снова идет напрямую.");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ Ошибка (код {code})");
        Console.ResetColor();
        if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);
        if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine(outp);
    }
}

async Task CheckPublicIpAsync()
{
    Console.WriteLine();
    Console.WriteLine("Проверяю внешний IP (как тебя видит интернет)...");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    string? ip = null;
    string[] urls = ["https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com"];
    foreach (var url in urls)
    {
        try
        {
            ip = (await http.GetStringAsync(url)).Trim();
            if (!string.IsNullOrWhiteSpace(ip) && ip.Contains('.')) break;
        }
        catch { }
    }
    if (!string.IsNullOrWhiteSpace(ip))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Внешний IP: {ip}");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine("  Не удалось узнать IP (нет интернета?)");
    }
    // показать prefs для понимания через кого идем
    var prefs = GetPrefs();
    if (prefs != null && !string.IsNullOrWhiteSpace(prefs.ExitNodeIP))
        Console.WriteLine($"  Сейчас выход через exit-node: {prefs.ExitNodeIP}");
    else
        Console.WriteLine("  Сейчас прямой выход (без exit-node)");
}

void OpenAdmin()
{
    try
    {
        Process.Start(new ProcessStartInfo("https://login.tailscale.com/admin/machines") { UseShellExecute = true });
        Console.WriteLine("Открываю админку в браузере...");
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); }
}

async Task AutoFixHappExitNodeAsync()
{
    Console.WriteLine();
    Console.WriteLine("── Авто-фикс Happ + exit-node ──");
    if (!IsAdmin())
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("❌ Нужны права Администратора. Перезапусти от Админа.");
        Console.ResetColor();
        return;
    }
    var xeonIp = "100.119.48.15";
    var nodes = GetExitNodes();
    var xeon = nodes.FirstOrDefault(n => n.TailscaleIP == xeonIp);
    if (xeon == null)
    {
        Console.WriteLine($"Ищу xeon {xeonIp} среди exit-node...");
        foreach (var n in nodes) Console.WriteLine($"  - {n.HostName} {n.TailscaleIP}");
        if (nodes.Count == 0) Console.WriteLine("xeon пока не offers exit-node — включи на xeon: tailscale set --advertise-exit-node=true");
    }
    else
    {
        Console.WriteLine($"✓ Найден xeon: {xeon.HostName} {xeon.TailscaleIP} {(xeon.Online ? "online" : "offline")}");
    }

    // 1) найти физ. шлюз (не Tailscale, не happ-xray)
    Console.WriteLine("Ищу физический шлюз...");
    var (_, routeOut, _) = RunRaw("route", "print 0.0.0.0", 5000);
    // fallback: ipconfig
    var gateway = "";
    string? happGw = null;
    // парсим route print: ищем строку 0.0.0.0 с шлюзом != On-link и интерфейсом != Tailscale/happ
    foreach (var line in routeOut.Split('\n'))
    {
        var t = line.Trim();
        if (!t.StartsWith("0.0.0.0")) continue;
        var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            var g = parts[2];
            var iface = parts[3];
            if (g == "On-link") continue;
            // 10.47.226.95 — физ, 172.19.0.1 — happ, 100.x — tailscale
            if (g.StartsWith("172.19.") || g.StartsWith("100.")) { happGw = g; continue; }
            if (System.Net.IPAddress.TryParse(g, out _)) { gateway = g; break; }
        }
    }
    if (string.IsNullOrEmpty(gateway))
    {
        // fallback: из ipconfig Ethernet 2
        var (_, ipOut, _) = RunRaw("ipconfig", "", 5000);
        foreach (var line in ipOut.Split('\n'))
        {
            if (line.Contains("Основной шлюз") && line.Contains("10."))
            {
                var p = line.Split(':');
                if (p.Length == 2) gateway = p[1].Trim();
            }
        }
    }
    Console.WriteLine($"  физ. шлюз: {(string.IsNullOrEmpty(gateway) ? "не найден" : gateway)}  happ gw: {happGw ?? "-"}");
    if (string.IsNullOrEmpty(gateway))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Не нашел физ. шлюз (10.47.226.95). Проверь route print вручную.");
        Console.ResetColor();
        gateway = "10.47.226.95";
        Console.WriteLine($"  использую {gateway} по умолчанию");
    }

    // 2) найти IP сервера Happ (ESTABLISHED через happ)
    Console.WriteLine("Ищу сервер Happ...");
    var happIps = new HashSet<string>();
    // netstat
    var (_, nsOut, _) = RunRaw("netstat", "-an", 5000);
    foreach (var line in nsOut.Split('\n'))
    {
        if (!line.Contains("ESTABLISHED")) continue;
        if (line.Contains("100.108.") || line.Contains("100.119.") || line.Contains("127.0.0.1") || line.Contains("172.19.224.")) continue;
        // ищем внешний 443 через happ (172.19.0.1:) или 10.217.*
        if (line.Contains("172.19.0.1:") || line.Contains("10.217."))
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var remote = parts[2]; // 185.84.98.5:443
                var ip = remote.Split(':')[0];
                if (System.Net.IPAddress.TryParse(ip, out var a) && !a.ToString().StartsWith("172.") && !a.ToString().StartsWith("192.168.") && !a.ToString().StartsWith("100."))
                    happIps.Add(ip);
            }
        }
    }
    // также из Get-NetTCPConnection через Happ pid
    var (_, psOut, _) = RunRaw("powershell", "-NoProfile -Command \"Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -like '172.19.*' } | Select-Object -ExpandProperty RemoteAddress | Sort-Object -Unique\"", 5000);
    foreach (var ip in psOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
    {
        var t = ip.Trim();
        if (System.Net.IPAddress.TryParse(t, out _)) happIps.Add(t);
    }
    Console.WriteLine($"  найдено Happ IP: {(happIps.Count == 0 ? "нет" : string.Join(", ", happIps.Take(5)))}");

    if (happIps.Count == 0)
    {
        Console.WriteLine("  Happ IP не нашел — пропускаю добавление маршрута (попробуй вручную: netstat -an | findstr 172.19.0.1)");
    }
    else
    {
        foreach (var hip in happIps.Take(5))
        {
            Console.Write($"  route add {hip} mask 255.255.255.255 {gateway} metric 1 ... ");
            var (_, _, err, code) = RunRawFull("route", $"add {hip} mask 255.255.255.255 {gateway} metric 1", 5000);
            if (code == 0) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("OK"); Console.ResetColor(); }
            else
            {
                // уже есть?
                if (err.Contains("already") || err.Contains("уже")) { Console.WriteLine("уже есть"); }
                else { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"код {code} {err}"); Console.ResetColor(); }
            }
        }
    }

    // 3) включить exit-node
    Console.WriteLine($"Включаю exit-node {xeonIp} --allow-lan=true ...");
    var (outp, err2, code2) = RunTailscale($"set --exit-node={xeonIp} --exit-node-allow-lan-access=true");
    if (code2 == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✓ Exit-node включен");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ tailscale set код {code2}: {err2} {outp}");
        Console.ResetColor();
    }
    Console.WriteLine("Проверяю...");
    await CheckPublicIpAsync();
    var (pingOut, _, _) = RunTailscale("ping -c 3 100.119.48.15", 8000);
    if (!string.IsNullOrWhiteSpace(pingOut)) Console.WriteLine(pingOut.Trim());
}

List<ExitNodeInfo> GetExitNodes()
{
    // 1) пробуем tailscale exit-node list (текстовый вывод)
    var (outList, _, codeList) = RunTailscale("exit-node list");
    var parsed = new List<ExitNodeInfo>();
    if (codeList == 0 && !string.IsNullOrWhiteSpace(outList) && !outList.Contains("no exit nodes", StringComparison.OrdinalIgnoreCase))
    {
        // формат обычно колонками: IP  Hostname  Location  ...  — парсим строки кроме заголовка
        var lines = outList.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("#") || t.StartsWith("IP", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(t)) continue;
            // пробуем вытащить IP и имя
            var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                // чаще первая колонка IP, вторая hostname
                var ip = parts[0].Contains('.') ? parts[0] : parts[1];
                var host = parts[0].Contains('.') ? parts[1] : parts[0];
                var loc = parts.Length >= 3 ? parts[parts.Length - 1] : "";
                parsed.Add(new ExitNodeInfo(host, ip, loc, true, true));
            }
        }
        if (parsed.Count > 0) return parsed;
    }

    // 2) fallback: tailscale status --json -> peers где ExitNodeOption == true
    var (outJson, _, codeJson) = RunTailscale("status --json");
    if (codeJson == 0 && !string.IsNullOrWhiteSpace(outJson))
    {
        try
        {
            using var doc = JsonDocument.Parse(outJson);
            if (doc.RootElement.TryGetProperty("Peer", out var peers))
            {
                foreach (var peer in peers.EnumerateObject())
                {
                    var v = peer.Value;
                    bool isOption = v.TryGetProperty("ExitNodeOption", out var e1) && e1.GetBoolean();
                    bool isExit = v.TryGetProperty("ExitNode", out var e2) && e2.GetBoolean();
                    if (!isOption && !isExit) continue;
                    string host = v.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : "";
                    string ip = "";
                    if (v.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0)
                        ip = ips[0].GetString() ?? "";
                    bool online = v.TryGetProperty("Online", out var on) && on.GetBoolean();
                    // локацию из exit-node list нет, ставим —
                    parsed.Add(new ExitNodeInfo(host, ip, "", online, isExit || isOption));
                }
            }
        }
        catch { }
    }
    // отфильтровать оффлайн если есть онлайн
    return parsed;
}

Prefs? GetPrefs()
{
    var json = GetPrefsRaw();
    if (json == null) return null;
    try { return JsonSerializer.Deserialize<Prefs>(json); }
    catch { return null; }
}

string? GetPrefsRaw()
{
    var (outp, _, code) = RunTailscale("debug prefs");
    if (code == 0 && !string.IsNullOrWhiteSpace(outp)) return outp;
    return null;
}

record ExitNodeInfo(string HostName, string TailscaleIP, string Location, bool Online, bool IsExitNode);
class Prefs
{
    public bool RouteAll { get; set; }
    public string ExitNodeID { get; set; } = "";
    public string ExitNodeIP { get; set; } = "";
    public bool ExitNodeAllowLANAccess { get; set; }
}
