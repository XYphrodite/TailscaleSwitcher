using System.Net;
using Xunit;

namespace TailscaleSwitcher.Tests;

public class HappAutoFixTests
{
    // helpers extracted from Program.cs logic for testability
    private static string? ParseGatewayFromRoutePrint(string routeOutput)
    {
        string? happGw = null;
        string gateway = "";
        foreach (var line in routeOutput.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("0.0.0.0")) continue;
            var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var g = parts[2];
                if (g == "On-link") continue;
                if (g.StartsWith("172.19.") || g.StartsWith("100.")) { happGw = g; continue; }
                if (IPAddress.TryParse(g, out _)) { gateway = g; break; }
            }
        }
        return string.IsNullOrEmpty(gateway) ? null : gateway;
    }

    private static HashSet<string> ParseHappIpsFromNetstat(string netstatOutput)
    {
        var ips = new HashSet<string>();
        foreach (var line in netstatOutput.Split('\n'))
        {
            if (!line.Contains("ESTABLISHED")) continue;
            if (line.Contains("100.108.") || line.Contains("100.119.") || line.Contains("127.0.0.1") || line.Contains("172.19.224.")) continue;
            if (line.Contains("172.19.0.1:") || line.Contains("10.217."))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var remote = parts[2];
                    var ip = remote.Split(':')[0];
                    if (IPAddress.TryParse(ip, out var a) && !a.ToString().StartsWith("172.") && !a.ToString().StartsWith("192.168.") && !a.ToString().StartsWith("100."))
                        ips.Add(ip);
                }
            }
        }
        return ips;
    }

    [Fact]
    public void GatewayParsing_FindsPhysicalGateway()
    {
        var sample = "     0.0.0.0          0.0.0.0     10.47.226.95    10.47.226.217     25\n          0.0.0.0          0.0.0.0         On-link        172.19.0.1      0\n          0.0.0.0          0.0.0.0         On-link     100.108.85.82      0\n";
        var gw = ParseGatewayFromRoutePrint(sample);
        Assert.Equal("10.47.226.95", gw);
    }

    [Fact]
    public void GatewayParsing_IgnoresHappAndTailscale()
    {
        var sample = "          0.0.0.0          0.0.0.0         On-link        172.19.0.1      0\n";
        var gw = ParseGatewayFromRoutePrint(sample);
        Assert.Null(gw);
    }

    [Fact]
    public void HappParsing_FindsServerIps()
    {
        var sample = "  TCP    172.19.0.1:50165       51.178.65.231:443      ESTABLISHED\n  TCP    172.19.0.1:50171       4.207.247.137:443      ESTABLISHED\n  TCP    100.108.85.82:22       100.89.154.125:43940   ESTABLISHED\n  TCP    127.0.0.1:6463         127.0.0.1:54999        ESTABLISHED\n";
        var ips = ParseHappIpsFromNetstat(sample);
        Assert.Contains("51.178.65.231", ips);
        Assert.Contains("4.207.247.137", ips);
        Assert.DoesNotContain("100.89.154.125", ips);
        Assert.Equal(2, ips.Count);
    }

    [Fact]
    public void HappParsing_EmptyWhenNoHappTraffic()
    {
        var sample = "  TCP    100.108.85.82:22       100.89.154.125:43940   ESTABLISHED\n";
        var ips = ParseHappIpsFromNetstat(sample);
        Assert.Empty(ips);
    }
}
