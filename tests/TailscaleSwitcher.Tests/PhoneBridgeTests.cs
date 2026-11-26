using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using TailscaleSwitcher.Routing;
using Xunit;

namespace TailscaleSwitcher.Tests;

public class PhoneBridgeTests
{
    [Fact]
    public void BuildPhoneBridge_SocksTunAndDohDnsAndRoutes()
    {
        var phone = new PhoneSettings { SocksPort = 10808, DnsServer = "1.1.1.1", RouteTailscaleViaPhone = true };
        var root = JsonNode.Parse(SingBoxConfigBuilder.BuildPhoneBridge(phone, "192.168.43.1", "192.168.43.0/24"))!;

        Assert.Equal("https", root["dns"]!["servers"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(443, root["dns"]!["servers"]![0]!["server_port"]!.GetValue<int>());
        Assert.Equal("phone-proxy", root["dns"]!["servers"]![0]!["detour"]!.GetValue<string>());
        Assert.Equal("dns-remote", root["dns"]!["final"]!.GetValue<string>());

        var tun = root["inbounds"]![0]!;
        Assert.Equal(SingBoxConfigBuilder.PhoneTunInterfaceName, tun["interface_name"]!.GetValue<string>());
        var excludes = tun["route_exclude_address"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("192.168.43.0/24", excludes);
        Assert.Contains("192.168.43.1/32", excludes);
        Assert.Contains(SingBoxConfigBuilder.TailscaleCgnat, excludes);

        var outbound = root["outbounds"]![0]!;
        Assert.Equal("socks", outbound["type"]!.GetValue<string>());
        Assert.Equal("192.168.43.1", outbound["server"]!.GetValue<string>());
        Assert.Equal(10808, outbound["server_port"]!.GetValue<int>());

        var rules = root["route"]!["rules"]!.AsArray();
        var directProcs = rules.First(r => r!["process_name"] != null)!["process_name"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("sshd.exe", directProcs);
        Assert.Contains("sing-box.exe", directProcs);
        Assert.DoesNotContain("tailscaled.exe", directProcs);
        Assert.Contains(rules, r => r!["network"]?.GetValue<string>() == "udp" && r["port"]?.GetValue<int>() == 443 && r["action"]?.GetValue<string>() == "reject");
        Assert.Equal("phone-proxy", root["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void BuildPhoneBridge_CustomDnsAndDirectTailscale()
    {
        var phone = new PhoneSettings { DnsServer = "94.140.14.14", RouteTailscaleViaPhone = false };
        var root = JsonNode.Parse(SingBoxConfigBuilder.BuildPhoneBridge(phone, "172.20.10.1", null))!;

        Assert.Equal("tcp", root["dns"]!["servers"]![0]!["type"]!.GetValue<string>());
        var rules = root["route"]!["rules"]!.AsArray();
        var directProcs = rules.First(r => r!["process_name"] != null)!["process_name"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("tailscaled.exe", directProcs);
    }

    [Theory]
    [InlineData("192.168.43.15", "255.255.255.0", "192.168.43.0/24")]
    [InlineData("172.20.10.2", "255.255.255.240", "172.20.10.0/28")]
    [InlineData("10.155.34.19", "255.255.0.0", "10.155.0.0/16")]
    public void ComputeSubnetCidr_CalculatesNetworkAndPrefix(string ip, string mask, string expected)
    {
        Assert.Equal(expected, PhoneBridgeService.ComputeSubnetCidr(ip, mask));
    }

    [Fact]
    public void SelectBestGateway_PrefersWifiOverUsbAndIgnoresVirtuals()
    {
        var candidates = new[]
        {
            new GatewayCandidate("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ethernet, true, "100.89.154.125", "255.192.0.0", "100.64.0.1"),
            new GatewayCandidate("vEthernet (FSE HostVnic)", "Hyper-V Virtual Ethernet", NetworkInterfaceType.Ethernet, true, "172.25.0.1", "255.255.240.0", "172.25.0.2"),
            new GatewayCandidate("Ethernet", "Intel(R) Ethernet", NetworkInterfaceType.Ethernet, true, "192.168.1.10", "255.255.255.0", "192.168.1.1"),
            new GatewayCandidate("Ethernet 2", "Remote NDIS based Internet Sharing Device", NetworkInterfaceType.Ethernet, true, "192.168.42.100", "255.255.255.0", "192.168.42.129"),
            new GatewayCandidate("Беспроводная сеть", "Realtek RTL8821CE", NetworkInterfaceType.Wireless80211, true, "192.168.43.25", "255.255.255.0", "192.168.43.1"),
            new GatewayCandidate("Wi-Fi 2", "Down Adapter", NetworkInterfaceType.Wireless80211, false, "192.168.50.2", "255.255.255.0", "192.168.50.1")
        };

        var best = PhoneBridgeService.SelectBestGateway(candidates);
        Assert.NotNull(best);
        Assert.Equal("192.168.43.1", best!.GatewayIp);
        Assert.Equal("192.168.43.0/24", best.SubnetCidr);
        Assert.Equal("Wi-Fi", best.Kind);
    }

    [Fact]
    public void SelectBestGateway_UsesUsbWhenNoWifiAndIgnoresApipa()
    {
        var candidates = new[]
        {
            new GatewayCandidate("Ethernet", "Intel(R) Ethernet", NetworkInterfaceType.Ethernet, true, "169.254.1.2", "255.255.0.0", "169.254.1.1"),
            new GatewayCandidate("Ethernet 3", "Apple Mobile Device Ethernet", NetworkInterfaceType.Ethernet, true, "172.20.10.2", "255.255.255.240", "172.20.10.1")
        };

        var best = PhoneBridgeService.SelectBestGateway(candidates);
        Assert.NotNull(best);
        Assert.Equal("172.20.10.1", best!.GatewayIp);
        Assert.Equal("172.20.10.0/28", best.SubnetCidr);
        Assert.Equal("USB", best.Kind);
    }

    [Fact]
    public void SelectBestGateway_IgnoresPlainWiredEthernet()
    {
        var candidates = new[]
        {
            new GatewayCandidate("Ethernet", "Intel(R) Ethernet Controller", NetworkInterfaceType.Ethernet, true, "10.0.0.5", "255.255.255.0", "10.0.0.1"),
            new GatewayCandidate("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ethernet, true, "100.89.154.125", "255.192.0.0", "100.64.0.1")
        };

        Assert.Null(PhoneBridgeService.SelectBestGateway(candidates));
    }

    [Fact]
    public void ResolvePhoneTarget_UsesOverrideAndGatewaySubnet()
    {
        var gw = new PhoneGatewayInfo("Беспроводная сеть", "Wi-Fi", "192.168.43.20", "192.168.43.0/24", "192.168.43.1", "Wi-Fi");
        var settings = new PhoneSettings();

        var autoRes = PhoneBridgeService.ResolvePhoneTarget(settings, null, gw);
        Assert.True(autoRes.Ok);
        Assert.Equal("192.168.43.1", autoRes.PhoneIp);
        Assert.Equal("192.168.43.0/24", autoRes.WifiSubnet);

        var manualRes = PhoneBridgeService.ResolvePhoneTarget(settings, "172.20.10.1", gw);
        Assert.True(manualRes.Ok);
        Assert.Equal("172.20.10.1", manualRes.PhoneIp);
        Assert.Equal("172.20.10.0/24", manualRes.WifiSubnet);

        var badRes = PhoneBridgeService.ResolvePhoneTarget(settings, "not-an-ip", gw);
        Assert.False(badRes.Ok);
    }

    [Fact]
    public void ParseSc_ParsesStartTypeAndRunningState()
    {
        const string qcAuto = """
            [SC] QueryServiceConfig SUCCESS
            SERVICE_NAME: HappService
                    TYPE               : 10  WIN32_OWN_PROCESS
                    START_TYPE         : 2   AUTO_START
            """;
        const string queryRunning = """
            SERVICE_NAME: HappService
                    TYPE               : 10  WIN32_OWN_PROCESS
                    STATE              : 4  RUNNING
            """;
        Assert.Equal("auto", PhoneBridgeService.ParseScStartType(qcAuto));
        Assert.True(PhoneBridgeService.ParseScRunning(queryRunning));
        Assert.Equal("disabled", PhoneBridgeService.ParseScStartType("START_TYPE : 4 DISABLED"));
        Assert.False(PhoneBridgeService.ParseScRunning("STATE : 1 STOPPED"));
    }

    [Fact]
    public async Task Cli_PhoneSetConfigAndBoolFlagsWork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tss-phone-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RoutingConfigStore(Path.Combine(dir, "routing.json"));
            var outW = new StringWriter();
            var errW = new StringWriter();
            var cli = new RoutingCli(store, outW, errW);

            Assert.Equal(0, await cli.RunAsync(["phone", "--help"]));
            Assert.Equal(0, await cli.RunAsync(["phone", "set", "--mode", "proxy", "--ip", "192.168.43.1", "--socks-port", "10818", "--http-port", "10819", "--dns", "8.8.8.8", "--stop-happ", "false", "--ts-route", "false"]));

            var p = store.Load().Phone;
            Assert.Equal(PhoneBridgeMode.Proxy, p.Mode);
            Assert.Equal("192.168.43.1", p.PhoneIp);
            Assert.Equal(10818, p.SocksPort);
            Assert.Equal(10819, p.HttpPort);
            Assert.Equal("8.8.8.8", p.DnsServer);
            Assert.False(p.StopLocalHapp);
            Assert.False(p.RouteTailscaleViaPhone);

            Assert.Equal(0, await cli.RunAsync(["phone", "config"]));
            var txt = outW.ToString();
            var brace = txt.IndexOf('{');
            Assert.True(brace >= 0);
            var json = JsonNode.Parse(txt[brace..]);
            Assert.NotNull(json);

            Assert.Equal(0, await cli.RunAsync(["phone", "set", "--ip", "auto"]));
            Assert.Equal("", store.Load().Phone.PhoneIp);

            Assert.Equal(1, await cli.RunAsync(["phone", "set", "--mode", "weird"]));
            Assert.Equal(1, await cli.RunAsync(["phone", "set", "--socks-port", "99999"]));

            var parsed = CliArgs.Parse(["on", "--clean", "--skip-health", "--no-stop-happ", "--no-ts-route", "--ip", "192.168.43.1"]);
            Assert.Null(parsed.Error);
            Assert.Equal(["on"], parsed.Positional);
            Assert.True(parsed.Has("--clean"));
            Assert.True(parsed.Has("--skip-health"));
            Assert.True(parsed.Has("--no-stop-happ"));
            Assert.True(parsed.Has("--no-ts-route"));
            Assert.Equal("192.168.43.1", parsed.Get("--ip"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
