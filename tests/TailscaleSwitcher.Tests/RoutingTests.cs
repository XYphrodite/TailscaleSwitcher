using System.Text.Json.Nodes;
using TailscaleSwitcher.Routing;
using Xunit;

namespace TailscaleSwitcher.Tests;

public class RoutingTests
{
    [Theory]
    [InlineData("example.com", HostRuleKind.DomainSuffix, "example.com")]
    [InlineData("*.Example.com", HostRuleKind.DomainSuffix, "example.com")]
    [InlineData(".example.com", HostRuleKind.DomainSuffix, "example.com")]
    [InlineData("https://sub.example.com/path", HostRuleKind.DomainSuffix, "sub.example.com")]
    [InlineData("full:api.example.com", HostRuleKind.Domain, "api.example.com")]
    [InlineData("1.2.3.4", HostRuleKind.IpCidr, "1.2.3.4/32")]
    [InlineData("10.0.0.0/8", HostRuleKind.IpCidr, "10.0.0.0/8")]
    [InlineData("::1", HostRuleKind.IpCidr, "::1/128")]
    [InlineData("10.0.0.0/33", HostRuleKind.Invalid, "10.0.0.0/33")]
    [InlineData("nodots", HostRuleKind.Invalid, "nodots")]
    [InlineData("bad_host!.com", HostRuleKind.Invalid, "bad_host!.com")]
    public void HostRuleParser_Classifies(string raw, HostRuleKind kind, string value)
    {
        var r = HostRuleParser.Parse(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(value, r.Value);
    }

    private static RouteProfile Profile() => new()
    {
        Name = "club",
        Node = "100.89.154.125",
        Port = 1081,
        Username = "u",
        Password = "p",
        Apps = ["chrome.exe", @"C:\Tools\app.exe"],
        Hosts = ["example.com", "full:api.test.org", "8.8.8.8", "10.0.0.0/8"]
    };

    [Fact]
    public void BuildClient_ProducesExpectedRules()
    {
        var root = JsonNode.Parse(SingBoxConfigBuilder.BuildClient(Profile()))!;
        var rules = root["route"]!["rules"]!.AsArray();

        var proc = rules.First(r => r!["process_name"] != null)!;
        Assert.Equal("chrome.exe", proc["process_name"]![0]!.GetValue<string>());
        Assert.Equal("club", proc["outbound"]!.GetValue<string>());

        Assert.Equal(@"C:\Tools\app.exe", rules.First(r => r!["process_path"] != null)!["process_path"]![0]!.GetValue<string>());
        Assert.Equal("example.com", rules.First(r => r!["domain_suffix"] != null)!["domain_suffix"]![0]!.GetValue<string>());
        Assert.Equal("api.test.org", rules.First(r => r!["domain"] != null)!["domain"]![0]!.GetValue<string>());
        var cidr = rules.First(r => r!["ip_cidr"] != null)!["ip_cidr"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("8.8.8.8/32", cidr);
        Assert.Contains("10.0.0.0/8", cidr);

        Assert.Equal("direct", root["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void BuildClient_ExcludesTailscaleAndBindsRelayToTailscaleInterface()
    {
        var root = JsonNode.Parse(SingBoxConfigBuilder.BuildClient(Profile()))!;
        var tun = root["inbounds"]![0]!;
        Assert.Equal("tun", tun["type"]!.GetValue<string>());
        Assert.Equal(SingBoxConfigBuilder.TailscaleCgnat, tun["route_exclude_address"]![0]!.GetValue<string>());

        var club = root["outbounds"]!.AsArray().First(o => o!["tag"]!.GetValue<string>() == "club")!;
        Assert.Equal("100.89.154.125", club["server"]!.GetValue<string>());
        Assert.Equal("Tailscale", club["bind_interface"]!.GetValue<string>());
        Assert.Equal("u", club["username"]!.GetValue<string>());
    }

    [Fact]
    public void BuildClient_RemoteDnsOnlyForDomainRules()
    {
        var p = Profile();
        var withDns = JsonNode.Parse(SingBoxConfigBuilder.BuildClient(p))!;
        Assert.NotNull(withDns["dns"]!["rules"]);

        p.Hosts = ["8.8.8.8"];
        var ipOnly = JsonNode.Parse(SingBoxConfigBuilder.BuildClient(p))!;
        Assert.Null(ipOnly["dns"]!["rules"]);

        p.Hosts = ["example.com"];
        p.RemoteDns = false;
        var local = JsonNode.Parse(SingBoxConfigBuilder.BuildClient(p))!;
        Assert.Null(local["dns"]!["rules"]);
    }

    [Theory]
    [InlineData("127.0.0.1:10809", "http", "127.0.0.1", 10809)]
    [InlineData("http://127.0.0.1:10809/", "http", "127.0.0.1", 10809)]
    [InlineData("socks://127.0.0.1:10808", "socks", "127.0.0.1", 10808)]
    [InlineData("SOCKS5://h:1", "socks", "h", 1)]
    public void ParseUpstream_ParsesTypeAndEndpoint(string spec, string type, string host, int port)
    {
        var u = SingBoxConfigBuilder.ParseUpstream(spec);
        Assert.NotNull(u);
        Assert.Equal((type, host, port), u!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("direct")]
    [InlineData("garbage")]
    public void ParseUpstream_NullForDirectOrInvalid(string spec) =>
        Assert.Null(SingBoxConfigBuilder.ParseUpstream(spec));

    [Fact]
    public void BuildRelay_ChainsToHappUpstream()
    {
        var relay = new RelaySettings { Username = "club", Password = "secret", Port = 1081, Upstream = "127.0.0.1:10809" };
        var root = JsonNode.Parse(SingBoxConfigBuilder.BuildRelay(relay, "100.89.154.125"))!;
        var inbound = root["inbounds"]![0]!;
        Assert.Equal("100.89.154.125", inbound["listen"]!.GetValue<string>());
        Assert.Equal(1081, inbound["listen_port"]!.GetValue<int>());
        Assert.Equal("secret", inbound["users"]![0]!["password"]!.GetValue<string>());
        Assert.Equal("upstream", root["route"]!["final"]!.GetValue<string>());
        var up = root["outbounds"]!.AsArray().First(o => o!["tag"]!.GetValue<string>() == "upstream")!;
        Assert.Equal("http", up["type"]!.GetValue<string>());
        Assert.Equal(10809, up["server_port"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildTaskXml_IsValidXmlAndEscapesPaths(bool persist)
    {
        var xml = SingBoxRunner.BuildTaskXml(@"C:\Tools & Co\sing-box.exe", @"C:\Users\a&b\run\relay.config.json", persist);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        XNamespaceCheck(doc, persist);
        Assert.Equal(@"C:\Tools & Co\sing-box.exe", doc.Descendants(doc.Root!.Name.Namespace + "Command").Single().Value);
    }

    private static void XNamespaceCheck(System.Xml.Linq.XDocument doc, bool persist)
    {
        var ns = doc.Root!.Name.Namespace;
        Assert.Equal(persist, doc.Descendants(ns + "BootTrigger").Any());
        Assert.Equal(persist, doc.Descendants(ns + "RestartOnFailure").Any());
        Assert.Equal("S-1-5-18", doc.Descendants(ns + "UserId").Single().Value);
    }

    [Fact]
    public void BuildRelay_DirectWhenNoUpstream()
    {
        var relay = new RelaySettings { Upstream = "direct", Password = "x" };
        var root = JsonNode.Parse(SingBoxConfigBuilder.BuildRelay(relay, "100.1.1.1"))!;
        Assert.Equal("direct", root["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cli_ProfileAppHostLifecycle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tss-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RoutingConfigStore(Path.Combine(dir, "routing.json"));
            var o = new StringWriter();
            var e = new StringWriter();
            var cli = new RoutingCli(store, o, e);

            Assert.Equal(0, await cli.RunAsync(["route", "profile", "add", "club", "--node", "100.89.154.125", "--user", "u", "--pass", "p"]));
            Assert.Equal(0, await cli.RunAsync(["route", "app", "add", "chrome.exe"]));
            Assert.Equal(0, await cli.RunAsync(["route", "host", "add", "example.com"]));
            Assert.Equal(1, await cli.RunAsync(["route", "host", "add", "nodots"]));

            var cfg = store.Load();
            Assert.Equal("club", cfg.Active);
            Assert.Equal(["chrome.exe"], cfg.Profiles[0].Apps);
            Assert.Equal(["example.com"], cfg.Profiles[0].Hosts);

            Assert.Equal(0, await cli.RunAsync(["route", "app", "remove", "CHROME.EXE"]));
            Assert.Empty(store.Load().Profiles[0].Apps);

            Assert.Equal(1, await cli.RunAsync(["route", "profile", "add", "club", "--node", "1.1.1.1"]));
            Assert.Equal(0, await cli.RunAsync(["route", "profile", "remove", "club"]));
            Assert.Null(store.Load().Active);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Cli_RelayInitGeneratesPassword()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tss-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RoutingConfigStore(Path.Combine(dir, "routing.json"));
            var cli = new RoutingCli(store, new StringWriter(), new StringWriter());
            Assert.Equal(0, await cli.RunAsync(["relay", "init", "--listen", "100.89.154.125", "--port", "1090"]));
            var r = store.Load().Relay;
            Assert.Equal(1090, r.Port);
            Assert.False(string.IsNullOrEmpty(r.Password));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
