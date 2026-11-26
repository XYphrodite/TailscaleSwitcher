using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailscaleSwitcher.Routing;

/// <summary>Профиль узла: куда (relay на клубном ПК) и что именно туда заворачивать.</summary>
public sealed class RouteProfile
{
    public string Name { get; set; } = "";
    /// <summary>Tailscale IP relay-узла (например 100.89.154.125).</summary>
    public string Node { get; set; } = "";
    public int Port { get; set; } = RelaySettings.DefaultPort;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Имя сетевого адаптера Tailscale на этом ПК (для bind_interface).</summary>
    public string TailscaleInterface { get; set; } = "Tailscale";
    /// <summary>Приложения: "chrome.exe" (по имени процесса) или полный путь.</summary>
    public List<string> Apps { get; set; } = [];
    /// <summary>Адреса: домен (с поддоменами), "full:host", IP, CIDR.</summary>
    public List<string> Hosts { get; set; } = [];
    /// <summary>
    /// true — через узел идёт ВЕСЬ трафик (кроме Tailscale, локальной сети и исключений Bypass*).
    /// false — только приложения и адреса из Apps/Hosts.
    /// </summary>
    public bool AllTraffic { get; set; }
    /// <summary>Исключения для режима AllTraffic: эти приложения/адреса идут напрямую.</summary>
    public List<string> BypassApps { get; set; } = [];
    public List<string> BypassHosts { get; set; } = [];
    /// <summary>Резолвить домены из списка Hosts через relay (без DNS-утечки).</summary>
    public bool RemoteDns { get; set; } = true;
    public string DnsServer { get; set; } = "1.1.1.1";
}

/// <summary>Настройки серверной части (relay) на клубном ПК.</summary>
public sealed class RelaySettings
{
    public const int DefaultPort = 1081;
    public const string DefaultUpstream = "127.0.0.1:10809"; // Happ (xray) HTTP-прокси; SOCKS :10808 у Happ с авторизацией

    /// <summary>Адрес для прослушивания. Пусто — определить через `tailscale ip -4`.</summary>
    public string Listen { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public string Username { get; set; } = "club";
    public string Password { get; set; } = "";
    /// <summary>Апстрим (Happ): "host:port" / "http://host:port" / "socks://host:port". Пусто или "direct" — напрямую.</summary>
    public string Upstream { get; set; } = DefaultUpstream;
}

/// <summary>Режим подключения через телефон (Wi-Fi точка доступа + Happ).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PhoneBridgeMode
{
    /// <summary>Весь трафик через sing-box TUN (игры, Steam, браузеры, Tailscale; нужен Администратор).</summary>
    Tun,
    /// <summary>Системный HTTP-прокси Windows (браузеры и приложения с поддержкой прокси; без прав Администратора).</summary>
    Proxy
}

/// <summary>Настройки моста через телефон (точка доступа Wi-Fi + Happ с Allow LAN Connections).</summary>
public sealed class PhoneSettings
{
    public const int DefaultSocksPort = 10808;
    public const int DefaultHttpPort = 10809;

    /// <summary>IP телефона вручную. Пусто — определять автоматически по шлюзу Wi-Fi.</summary>
    public string PhoneIp { get; set; } = "";
    /// <summary>Порт SOCKS5 на телефоне (используется в режиме TUN).</summary>
    public int SocksPort { get; set; } = DefaultSocksPort;
    /// <summary>Порт HTTP-прокси на телефоне (используется в режиме системного прокси Windows).</summary>
    public int HttpPort { get; set; } = DefaultHttpPort;
    /// <summary>Режим по умолчанию: Tun или Proxy.</summary>
    public PhoneBridgeMode Mode { get; set; } = PhoneBridgeMode.Tun;
    /// <summary>Останавливать локальный Happ/HappService на время работы TUN (избегает конфликта TUN).</summary>
    public bool StopLocalHapp { get; set; } = true;
    /// <summary>Добавлять маршрут 0.0.0.0/0 на TUN, чтобы Tailscale (DERP) тоже ходил через VPN телефона.</summary>
    public bool RouteTailscaleViaPhone { get; set; } = true;
    /// <summary>DNS-сервер через прокси телефона.</summary>
    public string DnsServer { get; set; } = "1.1.1.1";
}

/// <summary>Набор программ и портов, которые идут напрямую, мимо узла.</summary>
public sealed class ExclusionProfile
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool BuiltIn { get; set; }
    public List<string> Apps { get; set; } = [];
    public List<string> Hosts { get; set; } = [];
    /// <summary>Диапазоны UDP, например 19294:19344. Голос Discord — чтобы zapret видел пакеты.</summary>
    public List<string> UdpPorts { get; set; } = [];
}

public static class ExclusionPresets
{
    public const string Discord = "Discord";

    public static ExclusionProfile DiscordPreset(bool enabled) => new()
    {
        Name = Discord,
        Enabled = enabled,
        BuiltIn = true,
        Apps = ["Discord.exe", "Update.exe"],
        UdpPorts = ["19294:19344", "50000:50100"]
    };
}

public sealed class RoutingConfig
{
    public List<RouteProfile> Profiles { get; set; } = [];
    public string? Active { get; set; }
    public RelaySettings Relay { get; set; } = new();
    public PhoneSettings Phone { get; set; } = new();
    /// <summary>Наборы, которые идут мимо узла (zapret и похожие). Не зависят от профиля маршрута.</summary>
    public List<ExclusionProfile> Exclusions { get; set; } = [];

    public RouteProfile? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public RouteProfile? FindActiveOrOnly()
    {
        var active = Find(Active);
        if (active != null) return active;
        return Profiles.Count == 1 ? Profiles[0] : null;
    }

    public ExclusionProfile? FindExclusion(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Exclusions.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Добавляет встроенный набор Discord, если его ещё нет. Включает, если Discord.exe уже в обходе.</summary>
    public bool EnsurePresets()
    {
        if (FindExclusion(ExclusionPresets.Discord) != null) return false;
        var already = Profiles.Any(p => p.BypassApps.Any(a =>
            a.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase)));
        Exclusions.Add(ExclusionPresets.DiscordPreset(already));
        return true;
    }
}

public sealed class RoutingConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string Path { get; }

    public RoutingConfigStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(RoutingPaths.DataDir, "routing.json");
    }

    public RoutingConfig Load()
    {
        if (!File.Exists(Path)) return new RoutingConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<RoutingConfig>(File.ReadAllText(Path), JsonOptions) ?? new RoutingConfig();
            if (cfg.EnsurePresets()) Save(cfg);
            return cfg;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Не удалось прочитать {Path}: {ex.Message}", ex);
        }
    }

    public void Save(RoutingConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tmp = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, json);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Move(tmp, Path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(25);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

public static class RoutingPaths
{
    public static string DataDir { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailscaleSwitcher");

    public static string ToolsDir { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailscaleSwitcher", "tools");

    public static string RunDir => System.IO.Path.Combine(DataDir, "run");
}

public static class SecretGenerator
{
    public static string NewPassword(int bytes = 18) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', 'A').Replace('/', 'B').TrimEnd('=');
}
