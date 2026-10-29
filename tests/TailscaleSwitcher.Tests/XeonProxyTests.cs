using TailscaleSwitcher.Proxy;
using Xunit;

namespace TailscaleSwitcher.Tests;

public class XeonProxyTests
{
    [Fact]
    public void ParseCli_DefaultsToStatus()
    {
        var opts = XeonProxyService.ParseCli(["proxy"]);
        Assert.Equal(ProxyAction.Status, opts.Action);
        Assert.Null(opts.CustomServer);
        Assert.False(opts.Force);
        Assert.Null(opts.Error);
    }

    [Theory]
    [InlineData("on", ProxyAction.Enable)]
    [InlineData("enable", ProxyAction.Enable)]
    [InlineData("off", ProxyAction.Disable)]
    [InlineData("disable", ProxyAction.Disable)]
    [InlineData("toggle", ProxyAction.Toggle)]
    [InlineData("status", ProxyAction.Status)]
    public void ParseCli_ParsesActionsAndFlags(string sub, ProxyAction expected)
    {
        var opts = XeonProxyService.ParseCli(["proxy", sub, "--server", "192.168.1.5", "--force", "--winhttp"]);
        Assert.Equal(expected, opts.Action);
        Assert.Equal("192.168.1.5:10809", opts.CustomServer);
        Assert.True(opts.Force);
        Assert.True(opts.AlsoWinHttp);
        Assert.Null(opts.Error);
    }

    [Fact]
    public void ParseCli_RejectsUnknownSubcommand()
    {
        var opts = XeonProxyService.ParseCli(["proxy", "unknown"]);
        Assert.NotNull(opts.Error);
    }

    [Fact]
    public void BuildProxyOverride_PreservesCustomEntriesAndKeepsLocalLast()
    {
        var existing = "localhost;127.*;192.168.*;*.steampowered.com;*.epicgames.com;<local>";
        var merged = XeonProxyService.BuildProxyOverride(existing);
        var parts = merged.Split(';');

        Assert.Contains("*.steampowered.com", parts);
        Assert.Contains("*.epicgames.com", parts);
        Assert.Contains("10.*", parts);
        Assert.Contains("100.*", parts);
        Assert.Contains("*.ts.net", parts);
        Assert.Equal("<local>", parts[^1]);
        Assert.Equal(1, parts.Count(p => p.Equals("<local>", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BuildProxyOverride_HandlesEmptyInput()
    {
        var merged = XeonProxyService.BuildProxyOverride(null);
        var parts = merged.Split(';');

        Assert.Contains("localhost", parts);
        Assert.Contains("127.*", parts);
        Assert.Contains("192.168.*", parts);
        Assert.Contains("100.*", parts);
        Assert.Contains("*.ts.net", parts);
        Assert.Equal("<local>", parts[^1]);
    }

    [Fact]
    public async Task ResolveEndpointAsync_PrefersLanOverTailscale()
    {
        var (ep, ok) = await XeonProxyService.ResolveEndpointAsync(
            probeFunc: (candidate, _) => Task.FromResult(true));

        Assert.True(ok);
        Assert.Equal(XeonProxyService.XeonLanEndpoint, ep);
    }

    [Fact]
    public async Task ResolveEndpointAsync_FallsBackToTailscaleWhenLanDown()
    {
        var (ep, ok) = await XeonProxyService.ResolveEndpointAsync(
            probeFunc: (candidate, _) => Task.FromResult(candidate == XeonProxyService.XeonTailscaleEndpoint));

        Assert.True(ok);
        Assert.Equal(XeonProxyService.XeonTailscaleEndpoint, ep);
    }

    [Fact]
    public async Task ResolveEndpointAsync_ReturnsUnreachableWhenBothDown()
    {
        var (_, ok) = await XeonProxyService.ResolveEndpointAsync(
            probeFunc: (_, _) => Task.FromResult(false));

        Assert.False(ok);
    }

    [Fact]
    public void ExtractHttpProxyEndpoint_HandlesProtocolMapSyntax()
    {
        var ep = XeonProxyService.ExtractHttpProxyEndpoint("http=192.168.1.5:10809;https=192.168.1.5:10809");
        Assert.Equal("192.168.1.5:10809", ep);
    }
}
