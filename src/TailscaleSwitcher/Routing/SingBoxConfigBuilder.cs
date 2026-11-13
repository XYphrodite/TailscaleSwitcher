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

    public static string BuildClient(RouteProfile profile, string? logFile = null)
    {
        var domainSuffix = new List<string>();
        var domains = new List<string>();
        var cidrs = new List<string>();
        foreach (var h in profile.Hosts)
        {
            var r = HostRuleParser.Parse(h);
            switch (r.Kind)
            {
                case HostRuleKind.DomainSuffix: if (!domainSuffix.Contains(r.Value)) domainSuffix.Add(r.Value); break;
                case HostRuleKind.Domain: if (!domains.Contains(r.Value)) domains.Add(r.Value); break;
                case HostRuleKind.IpCidr: if (!cidrs.Contains(r.Value)) cidrs.Add(r.Value); break;
            }
        }

        var processNames = new List<string>();
        var processPaths = new List<string>();
        foreach (var app in profile.Apps)
        {
            var a = app.Trim();
            if (a.Length == 0) continue;
            if (a.Contains('\\') || a.Contains('/'))
            {
                if (!processPaths.Contains(a, StringComparer.OrdinalIgnoreCase)) processPaths.Add(a);
            }
            else
            {
                if (!processNames.Contains(a, StringComparer.OrdinalIgnoreCase)) processNames.Add(a);
            }
        }

        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };
        if (processNames.Count > 0)
            rules.Add(new JsonObject { ["process_name"] = ToArray(processNames), ["outbound"] = "club" });
        if (processPaths.Count > 0)
            rules.Add(new JsonObject { ["process_path"] = ToArray(processPaths), ["outbound"] = "club" });
        if (domainSuffix.Count > 0)
            rules.Add(new JsonObject { ["domain_suffix"] = ToArray(domainSuffix), ["outbound"] = "club" });
        if (domains.Count > 0)
            rules.Add(new JsonObject { ["domain"] = ToArray(domains), ["outbound"] = "club" });
        if (cidrs.Count > 0)
            rules.Add(new JsonObject { ["ip_cidr"] = ToArray(cidrs), ["outbound"] = "club" });

        var dnsServers = new JsonArray { new JsonObject { ["type"] = "local", ["tag"] = "local" } };
        var dns = new JsonObject { ["servers"] = dnsServers, ["final"] = "local" };
        if (profile.RemoteDns && (domainSuffix.Count > 0 || domains.Count > 0))
        {
            dnsServers.Add(new JsonObject
            {
                ["type"] = "tcp",
                ["tag"] = "remote",
                ["server"] = string.IsNullOrWhiteSpace(profile.DnsServer) ? "1.1.1.1" : profile.DnsServer,
                ["detour"] = "club"
            });
            var dnsRule = new JsonObject { ["action"] = "route", ["server"] = "remote" };
            if (domainSuffix.Count > 0) dnsRule["domain_suffix"] = ToArray(domainSuffix);
            if (domains.Count > 0) dnsRule["domain"] = ToArray(domains);
            dns["rules"] = new JsonArray { dnsRule };
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
                ["final"] = "direct",
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
