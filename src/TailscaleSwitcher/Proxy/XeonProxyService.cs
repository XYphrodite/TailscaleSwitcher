using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace TailscaleSwitcher.Proxy;

public enum ProxyAction
{
    Status,
    Enable,
    Disable,
    Toggle,
    Help
}

public sealed record ProxyCliOptions(
    ProxyAction Action,
    string? CustomServer = null,
    bool Force = false,
    bool AlsoWinHttp = false,
    string? Error = null);

public sealed record ProxyState(
    bool Enabled,
    string Server,
    string Override);

public sealed record EgressInfo(
    string Ip,
    string? Country = null,
    string? City = null,
    string? Org = null);

public static class XeonProxyService
{
    public const string XeonLanEndpoint = "192.168.1.5:10809";
    public const string XeonTailscaleEndpoint = "100.119.48.15:10809";

    public static readonly string[] DefaultEndpoints =
    [
        XeonLanEndpoint,
        XeonTailscaleEndpoint
    ];

    private static readonly string[] RequiredBypassEntries =
    [
        "localhost",
        "127.*",
        "10.*",
        "192.168.*",
        "100.*",
        "*.ts.net"
    ];

    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static ProxyCliOptions ParseCli(string[] args)
    {
        // args[0] is "proxy" / "px" / "xeon-proxy"
        var action = ProxyAction.Status;
        string? customServer = null;
        bool force = false;
        bool winHttp = false;

        int i = 1;
        if (i < args.Length && !args[i].StartsWith('-'))
        {
            var sub = args[i].Trim().ToLowerInvariant();
            switch (sub)
            {
                case "on" or "enable" or "start" or "1":
                    action = ProxyAction.Enable;
                    i++;
                    break;
                case "off" or "disable" or "stop" or "0":
                    action = ProxyAction.Disable;
                    i++;
                    break;
                case "toggle" or "switch":
                    action = ProxyAction.Toggle;
                    i++;
                    break;
                case "status" or "state" or "info":
                    action = ProxyAction.Status;
                    i++;
                    break;
                case "help":
                    return new ProxyCliOptions(ProxyAction.Help);
                default:
                    return new ProxyCliOptions(ProxyAction.Help, Error: $"Неизвестная подкоманда '{args[i]}'. Ожидается: on | off | toggle | status.");
            }
        }

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            var lower = arg.ToLowerInvariant();
            if (lower is "--help" or "-h" or "-?")
            {
                return new ProxyCliOptions(ProxyAction.Help);
            }
            if (lower is "--force" or "-f")
            {
                force = true;
                continue;
            }
            if (lower is "--winhttp")
            {
                winHttp = true;
                continue;
            }
            if (lower is "--server" or "-s")
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    return new ProxyCliOptions(action, Error: "Флаг --server требует адрес host[:port].");
                customServer = NormalizeEndpoint(args[++i]);
                continue;
            }
            if (lower.StartsWith("--server=", StringComparison.Ordinal))
            {
                var val = arg["--server=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(val))
                    return new ProxyCliOptions(action, Error: "Флаг --server требует адрес host[:port].");
                customServer = NormalizeEndpoint(val);
                continue;
            }

            return new ProxyCliOptions(ProxyAction.Help, Error: $"Неизвестный параметр '{arg}'.");
        }

        return new ProxyCliOptions(action, customServer, force, winHttp);
    }

    public static string NormalizeEndpoint(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = s["http://".Length..];
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = s["https://".Length..];
        s = s.TrimEnd('/');
        if (!s.Contains(':'))
            s += ":10809";
        return s;
    }

    public static string BuildProxyOverride(string? existing)
    {
        var list = new List<string>();
        bool hadLocalTag = false;

        if (!string.IsNullOrWhiteSpace(existing))
        {
            foreach (var part in existing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(part, "<local>", StringComparison.OrdinalIgnoreCase))
                {
                    hadLocalTag = true;
                    continue;
                }
                if (!list.Contains(part, StringComparer.OrdinalIgnoreCase))
                    list.Add(part);
            }
        }
        else
        {
            hadLocalTag = true;
        }

        foreach (var req in RequiredBypassEntries)
        {
            if (!list.Contains(req, StringComparer.OrdinalIgnoreCase))
                list.Add(req);
        }

        if (hadLocalTag || !list.Contains("<local>", StringComparer.OrdinalIgnoreCase))
            list.Add("<local>");

        return string.Join(';', list);
    }

    public static async Task<(string Endpoint, bool Reachable)> ResolveEndpointAsync(
        string? customServer = null,
        Func<string, int, Task<bool>>? probeFunc = null,
        int timeoutMs = 1200)
    {
        var probe = probeFunc ?? ProbeTcpAsync;

        if (!string.IsNullOrWhiteSpace(customServer))
        {
            var normalized = NormalizeEndpoint(customServer);
            var ok = await probe(normalized, timeoutMs);
            return (normalized, ok);
        }

        foreach (var candidate in DefaultEndpoints)
        {
            if (await probe(candidate, timeoutMs))
                return (candidate, true);
        }

        return (DefaultEndpoints[0], false);
    }

    public static async Task<bool> ProbeTcpAsync(string endpoint, int timeoutMs = 1200)
    {
        if (!TrySplitHostPort(endpoint, out var host, out var port))
            return false;

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySplitHostPort(string endpoint, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var clean = ExtractHttpProxyEndpoint(endpoint);
        var idx = clean.LastIndexOf(':');
        if (idx <= 0 || idx >= clean.Length - 1)
            return false;

        host = clean[..idx].Trim();
        return !string.IsNullOrWhiteSpace(host)
               && int.TryParse(clean[(idx + 1)..], out port)
               && port is >= 1 and <= 65535;
    }

    public static string ExtractHttpProxyEndpoint(string rawServer)
    {
        if (string.IsNullOrWhiteSpace(rawServer))
            return "";

        // Windows ProxyServer can be "host:port" or "http=host:port;https=host:port"
        var s = rawServer.Trim();
        if (s.Contains('='))
        {
            foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                    return NormalizeEndpoint(part["https=".Length..]);
                if (part.StartsWith("http=", StringComparison.OrdinalIgnoreCase))
                    return NormalizeEndpoint(part["http=".Length..]);
            }
        }
        return NormalizeEndpoint(s);
    }

    [SupportedOSPlatform("windows")]
    public static ProxyState GetState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: false);
        if (key == null)
            return new ProxyState(false, "", "");

        var enableObj = key.GetValue("ProxyEnable");
        bool enabled = enableObj switch
        {
            int i => i != 0,
            long l => l != 0,
            string str when int.TryParse(str, out var parsed) => parsed != 0,
            _ => false
        };
        var server = key.GetValue("ProxyServer")?.ToString() ?? "";
        var over = key.GetValue("ProxyOverride")?.ToString() ?? "";
        return new ProxyState(enabled, server, over);
    }

    [SupportedOSPlatform("windows")]
    public static void Enable(string endpoint, bool alsoWinHttp, bool isAdmin)
    {
        var normalized = NormalizeEndpoint(endpoint);
        using (var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKeyPath, writable: true))
        {
            var existingOverride = key.GetValue("ProxyOverride")?.ToString();
            var mergedOverride = BuildProxyOverride(existingOverride);
            key.SetValue("ProxyServer", normalized, RegistryValueKind.String);
            key.SetValue("ProxyOverride", mergedOverride, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        }

        NotifyWinInet();

        if (isAdmin)
        {
            if (alsoWinHttp)
                RunNetshWinHttp($"set proxy proxy-server=\"{normalized}\" bypass-list=\"localhost;127.*;10.*;192.168.*;100.*;*.ts.net;<local>\"");
            else
                RunNetshWinHttp("reset proxy");
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Disable(bool isAdmin)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKeyPath, writable: true))
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }

        NotifyWinInet();

        if (isAdmin)
        {
            RunNetshWinHttp("reset proxy");
        }
    }

    public static async Task<EgressInfo?> CheckEgressAsync(ProxyState currentState, int timeoutSeconds = 6)
    {
        using var handler = new HttpClientHandler();
        if (currentState.Enabled && !string.IsNullOrWhiteSpace(currentState.Server))
        {
            var ep = ExtractHttpProxyEndpoint(currentState.Server);
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"http://{ep}") { BypassProxyOnLocal = true };
        }
        else
        {
            handler.UseProxy = false;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

        try
        {
            var json = await http.GetStringAsync("https://ipinfo.io/json");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ip = root.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(ip))
            {
                var country = root.TryGetProperty("country", out var cProp) ? cProp.GetString() : null;
                var city = root.TryGetProperty("city", out var cityProp) ? cityProp.GetString() : null;
                var org = root.TryGetProperty("org", out var orgProp) ? orgProp.GetString() : null;
                return new EgressInfo(ip.Trim(), country, city, org);
            }
        }
        catch
        {
            // fallback to plain-text IP endpoints below
        }

        string[] fallbacks = ["https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com"];
        foreach (var url in fallbacks)
        {
            try
            {
                var ip = (await http.GetStringAsync(url)).Trim();
                if (!string.IsNullOrWhiteSpace(ip) && ip.Contains('.'))
                    return new EgressInfo(ip);
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static void NotifyWinInet()
    {
        try
        {
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch
        {
            // ignore on non-desktop environments
        }
    }

    private static void RunNetshWinHttp(string netshArgs)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"winhttp {netshArgs}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(4000);
        }
        catch
        {
            // ignore
        }
    }

    private static class NativeMethods
    {
        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    }
}
