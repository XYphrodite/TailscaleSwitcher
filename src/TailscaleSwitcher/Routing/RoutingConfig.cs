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

public sealed class RoutingConfig
{
    public List<RouteProfile> Profiles { get; set; } = [];
    public string? Active { get; set; }
    public RelaySettings Relay { get; set; } = new();

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
            return JsonSerializer.Deserialize<RoutingConfig>(File.ReadAllText(Path), JsonOptions) ?? new RoutingConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Не удалось прочитать {Path}: {ex.Message}", ex);
        }
    }

    public void Save(RoutingConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
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
