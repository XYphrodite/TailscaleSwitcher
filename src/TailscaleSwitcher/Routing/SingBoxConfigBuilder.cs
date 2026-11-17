using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TailscaleSwitcher.Routing;

public enum HostRuleKind
{
    DomainSuffix,
    Domain,
    IpCidr,
    Invalid
}

public readonly record struct HostRule(HostRuleKind Kind, string Value);

public static class HostRuleParser
{
    /// <summary>
    /// "example.com" / "*.example.com" / ".example.com" -> домен с поддоменами;
    /// "full:host" -> точный домен; "1.2.3.4" -> /32; "10.0.0.0/8" -> CIDR.
    /// </summary>
    public static HostRule Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(HostRuleKind.Invalid, "");
        var s = raw.Trim().ToLowerInvariant();

        if (s.StartsWith("http://") || s.StartsWith("https://"))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return new(HostRuleKind.Invalid, s);
            s = uri.Host;
        }

        if (s.StartsWith("full:"))
        {
            var host = s["full:".Length..].Trim();
            return IsValidDomain(host) ? new(HostRuleKind.Domain, host) : new(HostRuleKind.Invalid, s);
        }

        var slash = s.IndexOf('/');
        if (slash > 0)
        {
            var addrPart = s[..slash];
            if (IPAddress.TryParse(addrPart, out var net)
                && int.TryParse(s[(slash + 1)..], out var prefix)
                && prefix >= 0
                && prefix <= (net.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
                return new(HostRuleKind.IpCidr, $"{net}/{prefix}");
            return new(HostRuleKind.Invalid, s);
        }

        if (IPAddress.TryParse(s, out var ip))
        {
            var bits = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            return new(HostRuleKind.IpCidr, $"{ip}/{bits}");
        }

        if (s.StartsWith("*.")) s = s[2..];
        s = s.TrimStart('.');
        return IsValidDomain(s) ? new(HostRuleKind.DomainSuffix, s) : new(HostRuleKind.Invalid, s);
    }

    public static bool IsValidDomain(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 253) return false;
        if (!s.Contains('.') && !string.Equals(s, "localhost", StringComparison.Ordinal)) return false;
        foreach (var label in s.Split('.'))
        {
            if (label.Length is 0 or > 63) return false;
            if (label.StartsWith('-') || label.EndsWith('-')) return false;
            foreach (var c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }
}

public static class SingBoxConfigBuilder
{
    public const string TunInterfaceName = "TSS-route";
    public const string TunAddress = "172.30.0.1/30";
    public const string TailscaleCgnat = "100.64.0.0/10";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static (string Host, int Port)? SplitEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var s = endpoint.Trim();
        var idx = s.LastIndexOf(':');
        if (idx <= 0 || idx == s.Length - 1) return null;
        return int.TryParse(s[(idx + 1)..], out var port) && port is >= 1 and <= 65535
            ? (s[..idx], port)
            : null;
    }

    /// <summary>
    /// "direct"/пусто -> null; "host:port" и "http://host:port" -> HTTP-прокси (Happ :10809);
    /// "socks://host:port" / "socks5://host:port" -> SOCKS5. Некорректное значение -> null.
    /// </summary>
    public static (string Type, string Host, int Port)? ParseUpstream(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Trim().Equals("direct", StringComparison.OrdinalIgnoreCase))
            return null;
        var s = spec.Trim();
        var type = "http";
        foreach (var (prefix, t) in new[] { ("http://", "http"), ("socks5://", "socks"), ("socks://", "socks") })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                type = t;
                s = s[prefix.Length..];
                break;
            }
        }
        var ep = SplitEndpoint(s.TrimEnd('/'));
        return ep == null ? null : (type, ep.Value.Host, ep.Value.Port);
    }

    /// <summary>Приложения и адреса, разобранные в категории sing-box.</summary>
    private sealed class RuleSet
    {
        public List<string> DomainSuffix { get; } = [];
        public List<string> Domain { get; } = [];
        public List<string> Cidr { get; } = [];
        public List<string> ProcessNames { get; } = [];
        public List<string> ProcessPaths { get; } = [];

        public bool HasDomains => DomainSuffix.Count > 0 || Domain.Count > 0;

        public static RuleSet From(IEnumerable<string> hosts, IEnumerable<string> apps)
        {
            var rs = new RuleSet();
            foreach (var h in hosts)
            {
                var r = HostRuleParser.Parse(h);
                switch (r.Kind)
                {
                    case HostRuleKind.DomainSuffix: AddUnique(rs.DomainSuffix, r.Value); break;
                    case HostRuleKind.Domain: AddUnique(rs.Domain, r.Value); break;
                    case HostRuleKind.IpCidr: AddUnique(rs.Cidr, r.Value); break;
                }
            }
            foreach (var app in apps)
            {
                var a = app.Trim();
                if (a.Length == 0) continue;
                AddUnique(a.Contains('\\') || a.Contains('/') ? rs.ProcessPaths : rs.ProcessNames, a);
            }
            return rs;
        }

        public void AddRules(JsonArray rules, string outbound)
        {
            if (ProcessNames.Count > 0)
                rules.Add(new JsonObject { ["process_name"] = ToArray(ProcessNames), ["outbound"] = outbound });
            if (ProcessPaths.Count > 0)
                rules.Add(new JsonObject { ["process_path"] = ToArray(ProcessPaths), ["outbound"] = outbound });
            if (DomainSuffix.Count > 0)
                rules.Add(new JsonObject { ["domain_suffix"] = ToArray(DomainSuffix), ["outbound"] = outbound });
            if (Domain.Count > 0)
                rules.Add(new JsonObject { ["domain"] = ToArray(Domain), ["outbound"] = outbound });
            if (Cidr.Count > 0)
                rules.Add(new JsonObject { ["ip_cidr"] = ToArray(Cidr), ["outbound"] = outbound });
        }

        private static void AddUnique(List<string> list, string v)
        {
            if (!list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v);
        }
    }

    /// <summary>Процессы Tailscale и самого sing-box не заворачиваем: иначе туннель зависит сам от себя.</summary>
    public static readonly string[] AlwaysDirectProcesses =
        ["tailscaled.exe", "tailscale-ipn.exe", "tailscale.exe", "sing-box.exe"];

    public static string BuildClient(RouteProfile profile, string? logFile = null)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };

        var dnsServers = new JsonArray { new JsonObject { ["type"] = "local", ["tag"] = "local" } };
        var dns = new JsonObject { ["servers"] = dnsServers };
        var remoteDns = new JsonObject
        {
            ["type"] = "tcp",
            ["tag"] = "remote",
            ["server"] = string.IsNullOrWhiteSpace(profile.DnsServer) ? "1.1.1.1" : profile.DnsServer,
            ["detour"] = "club"
        };

        string routeFinal;
        if (profile.AllTraffic)
        {
            // Весь трафик -> club; напрямую только Tailscale, локальная сеть и исключения.
            rules.Add(new JsonObject { ["process_name"] = ToArray(AlwaysDirectProcesses), ["outbound"] = "direct" });
            var bypass = RuleSet.From(profile.BypassHosts, profile.BypassApps);
            bypass.AddRules(rules, "direct");
            rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });
            // QUIC через TCP-only relay не заработает: пусть браузер сразу откатится на TCP
            rules.Add(new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });
            routeFinal = "club";

            dnsServers.Add(remoteDns);
            var localDomains = new JsonArray();
            localDomains.Add("ts.net");
            foreach (var d in bypass.DomainSuffix) localDomains.Add(d);
            var localRule = new JsonObject { ["action"] = "route", ["server"] = "local", ["domain_suffix"] = localDomains };
            if (bypass.Domain.Count > 0) localRule["domain"] = ToArray(bypass.Domain);
            dns["rules"] = new JsonArray { localRule };
            dns["final"] = profile.RemoteDns ? "remote" : "local";
        }
        else
        {
            var include = RuleSet.From(profile.Hosts, profile.Apps);
            include.AddRules(rules, "club");
            routeFinal = "direct";

            dns["final"] = "local";
            if (profile.RemoteDns && include.HasDomains)
            {
                dnsServers.Add(remoteDns);
                var dnsRule = new JsonObject { ["action"] = "route", ["server"] = "remote" };
                if (include.DomainSuffix.Count > 0) dnsRule["domain_suffix"] = ToArray(include.DomainSuffix);
                if (include.Domain.Count > 0) dnsRule["domain"] = ToArray(include.Domain);
                dns["rules"] = new JsonArray { dnsRule };
            }
        }

        var club = new JsonObject
        {
            ["type"] = "socks",
            ["tag"] = "club",
            ["server"] = profile.Node,
            ["server_port"] = profile.Port,
            ["version"] = "5",
            // relay лежит в Tailscale: сокет обязан идти через адаптер Tailscale, а не через физический.
            ["bind_interface"] = profile.TailscaleInterface
        };
        if (!string.IsNullOrEmpty(profile.Username))
        {
            club["username"] = profile.Username;
            club["password"] = profile.Password;
        }

        var root = new JsonObject
        {
            ["log"] = LogSection(logFile),
            ["dns"] = dns,
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = TunInterfaceName,
                    ["address"] = new JsonArray(TunAddress),
                    ["auto_route"] = true,
                    ["strict_route"] = false,
                    ["stack"] = "mixed",
                    // трафик к узлам Tailscale (в т.ч. к самому relay) в TUN не заворачиваем
                    ["route_exclude_address"] = new JsonArray(TailscaleCgnat)
                }
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                club
            },
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = routeFinal,
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local"
            }
        };
        return root.ToJsonString(Pretty);
    }

    public static string BuildRelay(RelaySettings relay, string listenAddress, string? logFile = null)
    {
        var inbound = new JsonObject
        {
            ["type"] = "socks",
            ["tag"] = "relay-in",
            ["listen"] = listenAddress,
            ["listen_port"] = relay.Port
        };
        if (!string.IsNullOrEmpty(relay.Username))
        {
            inbound["users"] = new JsonArray(new JsonObject
            {
                ["username"] = relay.Username,
                ["password"] = relay.Password
            });
        }

        var outbounds = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } };
        var final = "direct";
        var upstream = ParseUpstream(relay.Upstream);
        if (upstream != null)
        {
            var up = new JsonObject
            {
                ["type"] = upstream.Value.Type,
                ["tag"] = "upstream",
                ["server"] = upstream.Value.Host,
                ["server_port"] = upstream.Value.Port
            };
            if (upstream.Value.Type == "socks") up["version"] = "5";
            outbounds.Add(up);
            final = "upstream";
        }

        var root = new JsonObject
        {
            ["log"] = LogSection(logFile),
            ["inbounds"] = new JsonArray(inbound),
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["final"] = final,
                ["default_domain_resolver"] = new JsonObject { ["server"] = "local" }
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" })
            }
        };
        return root.ToJsonString(Pretty);
    }

    private static JsonObject LogSection(string? logFile)
    {
        var log = new JsonObject { ["level"] = "warn", ["timestamp"] = true };
        if (!string.IsNullOrWhiteSpace(logFile)) log["output"] = logFile;
        return log;
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }
}
