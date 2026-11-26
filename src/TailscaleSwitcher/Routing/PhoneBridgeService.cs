using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text.Json;
using TailscaleSwitcher.Proxy;
using TailscaleSwitcher.Services;

namespace TailscaleSwitcher.Routing;

public sealed record GatewayCandidate(
    string InterfaceName,
    string Description,
    NetworkInterfaceType InterfaceType,
    bool IsUp,
    string IpAddress,
    string SubnetMask,
    string GatewayIp);

public sealed record PhoneGatewayInfo(
    string InterfaceName,
    string Description,
    string LocalIp,
    string SubnetCidr,
    string GatewayIp,
    string Kind);

public sealed record PhoneBackupSnapshot(
    DateTime CreatedUtc,
    ProxyState Proxy,
    bool HappServiceExists,
    string HappServiceStartType,
    bool HappServiceWasRunning);

public sealed record PhoneRuntimeState(
    PhoneBridgeMode Mode,
    string PhoneIp,
    int Port,
    string? WifiSubnet,
    DateTime StartedUtc);

public sealed record PhoneProbeResult(
    string PhoneIp,
    int SocksPort,
    bool SocksOpen,
    int HttpPort,
    bool HttpOpen,
    PhoneGatewayInfo? Gateway);

public sealed record PhoneStatusSnapshot(
    bool Active,
    PhoneBridgeMode? ActiveMode,
    int? TunPid,
    string? PhoneIp,
    int? Port,
    bool HasBackup,
    PhoneGatewayInfo? Gateway);

public sealed record PhoneStartOptions(
    PhoneBridgeMode? Mode = null,
    string? PhoneIp = null,
    int? SocksPort = null,
    int? HttpPort = null,
    string? DnsServer = null,
    bool PersistAtBoot = false,
    bool Force = false,
    bool SkipHealthCheck = false,
    bool? StopLocalHapp = null,
    bool? RouteTailscaleViaPhone = null);

public sealed record PhoneOperationResult(
    bool Ok,
    int ExitCode,
    string Message,
    string? EgressIp = null);

/// <summary>
/// Управляет мостом «ПК -> Wi-Fi/USB телефона -> Happ (Allow LAN Connections)»:
/// режим TUN (весь трафик через sing-box -> SOCKS5 :10808) и режим системного HTTP-прокси (:10809).
/// </summary>
public static class PhoneBridgeService
{
    public const string HappSingBoxPath = @"C:\Program Files\FlyFrogLLC\Happ\tun\sing-box.exe";
    private const string HappServiceName = "HappService";
    private const string HappTunInterfaceName = "happ-default-tun";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] IgnoredAdapterMarkers =
    [
        "Tailscale",
        "Wintun",
        "Sing-Box",
        "sing-box",
        "TSS-",
        "Happ",
        "phone-vpn-tun",
        "OpenVPN",
        "TAP-Windows",
        "WireGuard",
        "Hyper-V",
        "vEthernet",
        "VMware",
        "VirtualBox",
        "Bluetooth",
        "Radmin",
        "ZeroTier",
        "Hamachi",
        "Netch",
        "Clash",
        "Mihomo"
    ];

    public static SingBoxRunner PhoneRunner { get; } = new("phone");
    public static string BackupPath => Path.Combine(RoutingPaths.RunDir, "phone.backup.json");
    public static string StatePath => Path.Combine(RoutingPaths.RunDir, "phone.state.json");

    public static bool IsTunRunning() => PhoneRunner.GetStatus().Running;
    public static string TailLog(int lines = 40) => PhoneRunner.TailLog(lines);

    public static string? ComputeSubnetCidr(string? ipAddress, string? subnetMask)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        if (!IPAddress.TryParse(subnetMask, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var netBytes = new byte[4];
        int prefix = 0;
        for (int i = 0; i < 4; i++)
        {
            netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            prefix += BitOperations.PopCount(maskBytes[i]);
        }

        return $"{new IPAddress(netBytes)}/{prefix}";
    }

    public static string? FallbackSubnetCidr(string? ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var b = ip.GetAddressBytes();
        return $"{b[0]}.{b[1]}.{b[2]}.0/24";
    }

    public static bool IsTailscaleCgnat(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    private static bool SameSubnet(IPAddress a, IPAddress mask, IPAddress b)
    {
        var ab = a.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        var bb = b.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            if ((ab[i] & mb[i]) != (bb[i] & mb[i])) return false;
        return true;
    }

    public static bool IsIgnoredInterface(string? name, string? description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return true;

        var combined = $"{name} {description}";
        foreach (var marker in IgnoredAdapterMarkers)
        {
            if (combined.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static (int Score, string Kind) ClassifyInterface(GatewayCandidate c)
    {
        var combined = $"{c.InterfaceName} {c.Description}";
        if (c.InterfaceType == NetworkInterfaceType.Wireless80211 ||
            combined.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("wifi", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("wlan", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("беспровод", StringComparison.OrdinalIgnoreCase))
        {
            return (300, "Wi-Fi");
        }

        if (combined.Contains("rndis", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("usb", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("android", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("apple mobile", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("tether", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("модем", StringComparison.OrdinalIgnoreCase))
        {
            return (200, "USB");
        }

        return (0, "");
    }

    private static int SubnetPreference(string gatewayIp)
    {
        if (gatewayIp.StartsWith("192.168.", StringComparison.Ordinal) ||
            gatewayIp.StartsWith("172.20.", StringComparison.Ordinal) ||
            gatewayIp.StartsWith("10.", StringComparison.Ordinal))
            return 1;
        return 0;
    }

    public static PhoneGatewayInfo? SelectBestGateway(IEnumerable<GatewayCandidate> candidates)
    {
        var valid = new List<(int Score, int SubPref, PhoneGatewayInfo Info)>();
        foreach (var c in candidates)
        {
            if (!c.IsUp) continue;
            if (IsIgnoredInterface(c.InterfaceName, c.Description, c.InterfaceType)) continue;

            var (score, kind) = ClassifyInterface(c);
            if (score <= 0) continue;

            if (!IPAddress.TryParse(c.GatewayIp, out var gw) || gw.AddressFamily != AddressFamily.InterNetwork)
                continue;
            if (IPAddress.Any.Equals(gw) || IPAddress.IsLoopback(gw) || IsTailscaleCgnat(gw))
                continue;
            var gwBytes = gw.GetAddressBytes();
            if (gwBytes[0] == 169 && gwBytes[1] == 254)
                continue;

            var cidr = ComputeSubnetCidr(c.IpAddress, c.SubnetMask);
            if (cidr == null) continue;

            valid.Add((score, SubnetPreference(c.GatewayIp), new PhoneGatewayInfo(
                c.InterfaceName, c.Description, c.IpAddress, cidr, c.GatewayIp, kind)));
        }

        return valid
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.SubPref)
            .ThenBy(x => x.Info.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Info)
            .FirstOrDefault();
    }

    public static PhoneGatewayInfo? DetectPhoneGateway()
    {
        var candidates = new List<GatewayCandidate>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (IsIgnoredInterface(nic.Name, nic.Description, nic.NetworkInterfaceType)) continue;

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                var gw = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(a));
                if (gw == null) continue;

                var uni = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                    .OrderByDescending(u => SameSubnet(u.Address, u.IPv4Mask, gw))
                    .FirstOrDefault();
                if (uni == null) continue;

                candidates.Add(new GatewayCandidate(
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType,
                    true,
                    uni.Address.ToString(),
                    uni.IPv4Mask.ToString(),
                    gw.ToString()));
            }
        }
        catch
        {
            return null;
        }

        return SelectBestGateway(candidates);
    }

    public static (bool Ok, string PhoneIp, string WifiSubnet, string? Error) ResolvePhoneTarget(
        PhoneSettings settings,
        string? ipOverride,
        PhoneGatewayInfo? detected)
    {
        var rawIp = !string.IsNullOrWhiteSpace(ipOverride)
            ? ipOverride.Trim()
            : !string.IsNullOrWhiteSpace(settings.PhoneIp)
                ? settings.PhoneIp.Trim()
                : detected?.GatewayIp;

        if (string.IsNullOrWhiteSpace(rawIp))
        {
            return (false, "", "", "Не найден активный Wi-Fi или USB-модем с шлюзом телефона. Подключись к точке доступа телефона или укажи --ip <IP>.");
        }

        if (!IPAddress.TryParse(rawIp, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return (false, "", "", $"Некорректный IPv4-адрес телефона: '{rawIp}'.");
        }

        var normalizedIp = parsed.ToString();
        var subnet = detected != null && string.Equals(detected.GatewayIp, normalizedIp, StringComparison.Ordinal)
            ? detected.SubnetCidr
            : (FallbackSubnetCidr(normalizedIp) ?? $"{normalizedIp}/32");

        return (true, normalizedIp, subnet, null);
    }

    [SupportedOSPlatform("windows")]
    public static PhoneStatusSnapshot GetStatus()
    {
        var runner = PhoneRunner.GetStatus();
        var state = LoadState();
        var gateway = DetectPhoneGateway();
        var hasBackup = File.Exists(BackupPath);

        if (runner.Running)
        {
            return new PhoneStatusSnapshot(
                Active: true,
                ActiveMode: PhoneBridgeMode.Tun,
                TunPid: runner.Pid,
                PhoneIp: state?.PhoneIp,
                Port: state?.Port ?? PhoneSettings.DefaultSocksPort,
                HasBackup: hasBackup,
                Gateway: gateway);
        }

        if (state is { Mode: PhoneBridgeMode.Proxy } && OperatingSystem.IsWindows())
        {
            var proxy = XeonProxyService.GetState();
            var expected = $"{state.PhoneIp}:{state.Port}";
            if (proxy.Enabled &&
                string.Equals(XeonProxyService.ExtractHttpProxyEndpoint(proxy.Server), expected, StringComparison.OrdinalIgnoreCase))
            {
                return new PhoneStatusSnapshot(
                    Active: true,
                    ActiveMode: PhoneBridgeMode.Proxy,
                    TunPid: null,
                    PhoneIp: state.PhoneIp,
                    Port: state.Port,
                    HasBackup: hasBackup,
                    Gateway: gateway);
            }
        }

        return new PhoneStatusSnapshot(
            Active: false,
            ActiveMode: null,
            TunPid: null,
            PhoneIp: null,
            Port: null,
            HasBackup: hasBackup,
            Gateway: gateway);
    }

    public static async Task<PhoneProbeResult> ProbeAsync(
        PhoneSettings settings,
        string? ipOverride = null,
        int timeoutMs = 1500)
    {
        var gateway = DetectPhoneGateway();
        var target = ResolvePhoneTarget(settings, ipOverride, gateway);
        var socksPort = settings.SocksPort is >= 1 and <= 65535 ? settings.SocksPort : PhoneSettings.DefaultSocksPort;
        var httpPort = settings.HttpPort is >= 1 and <= 65535 ? settings.HttpPort : PhoneSettings.DefaultHttpPort;

        if (!target.Ok)
            return new PhoneProbeResult("", socksPort, false, httpPort, false, gateway);

        var socksTask = XeonProxyService.ProbeTcpAsync($"{target.PhoneIp}:{socksPort}", timeoutMs);
        var httpTask = XeonProxyService.ProbeTcpAsync($"{target.PhoneIp}:{httpPort}", timeoutMs);
        await Task.WhenAll(socksTask, httpTask).ConfigureAwait(false);

        return new PhoneProbeResult(target.PhoneIp, socksPort, socksTask.Result, httpPort, httpTask.Result, gateway);
    }

    [SupportedOSPlatform("windows")]
    public static async Task<PhoneOperationResult> StartAsync(
        PhoneSettings settings,
        PhoneStartOptions? options = null,
        Action<string>? log = null)
    {
        options ??= new PhoneStartOptions();
        var mode = options.Mode ?? settings.Mode;
        var socksPort = options.SocksPort ?? settings.SocksPort;
        var httpPort = options.HttpPort ?? settings.HttpPort;
        var dnsServer = string.IsNullOrWhiteSpace(options.DnsServer) ? settings.DnsServer : options.DnsServer.Trim();
        var stopLocalHapp = options.StopLocalHapp ?? settings.StopLocalHapp;
        var routeTailscale = options.RouteTailscaleViaPhone ?? settings.RouteTailscaleViaPhone;

        if (socksPort is < 1 or > 65535 || httpPort is < 1 or > 65535)
            return new PhoneOperationResult(false, 1, "Порт должен быть в диапазоне 1..65535.");

        var gateway = DetectPhoneGateway();
        var target = ResolvePhoneTarget(settings, options.PhoneIp, gateway);
        if (!target.Ok)
            return new PhoneOperationResult(false, 1, target.Error!);

        var effectiveSettings = new PhoneSettings
        {
            PhoneIp = target.PhoneIp,
            SocksPort = socksPort,
            HttpPort = httpPort,
            Mode = mode,
            StopLocalHapp = stopLocalHapp,
            RouteTailscaleViaPhone = routeTailscale,
            DnsServer = string.IsNullOrWhiteSpace(dnsServer) ? "1.1.1.1" : dnsServer
        };

        if (mode == PhoneBridgeMode.Proxy)
        {
            if (!options.Force && !await XeonProxyService.ProbeTcpAsync($"{target.PhoneIp}:{httpPort}", 2500).ConfigureAwait(false))
            {
                return new PhoneOperationResult(false, 2,
                    $"HTTP-прокси телефона {target.PhoneIp}:{httpPort} не отвечает. В Happ на телефоне включи: Настройки -> Расширенные -> Allow LAN Connections (или передай --force).");
            }

            if (PhoneRunner.GetStatus().Running)
            {
                if (!RoutingCli.IsAdmin())
                    return new PhoneOperationResult(false, 3, "Чтобы выключить активный TUN-мост телефона, нужны права Администратора.");
                PhoneRunner.Stop(deleteTask: true);
                await WaitForAdapterRemovedAsync(SingBoxConfigBuilder.PhoneTunInterfaceName, 4000).ConfigureAwait(false);
            }

            EnsureBackupSnapshot();
            XeonProxyService.Enable($"{target.PhoneIp}:{httpPort}", alsoWinHttp: false, isAdmin: RoutingCli.IsAdmin());
            SaveState(new PhoneRuntimeState(PhoneBridgeMode.Proxy, target.PhoneIp, httpPort, target.WifiSubnet, DateTime.UtcNow));

            var egress = await XeonProxyService.CheckEgressAsync(XeonProxyService.GetState(), 5).ConfigureAwait(false);
            var ipNote = egress != null ? $" Внешний IP: {egress.Ip}" : "";
            return new PhoneOperationResult(true, 0,
                $"Системный прокси через телефон включён -> {target.PhoneIp}:{httpPort}.{ipNote}", egress?.Ip);
        }

        // Режим TUN
        if (!RoutingCli.IsAdmin())
            return new PhoneOperationResult(false, 3, "Для режима TUN нужны права Администратора (или используй --mode proxy).");

        if (!options.Force && !await XeonProxyService.ProbeTcpAsync($"{target.PhoneIp}:{socksPort}", 2500).ConfigureAwait(false))
        {
            return new PhoneOperationResult(false, 2,
                $"SOCKS5-прокси телефона {target.PhoneIp}:{socksPort} не отвечает. В Happ на телефоне включи: Настройки -> Расширенные -> Allow LAN Connections, подключи VPN и точку доступа (или --force).");
        }

        string singBoxExe;
        try
        {
            singBoxExe = await EnsureSingBoxForPhoneAsync(log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PhoneOperationResult(false, 1, $"Не удалось подготовить sing-box: {ex.Message}");
        }

        var configJson = SingBoxConfigBuilder.BuildPhoneBridge(effectiveSettings, target.PhoneIp, target.WifiSubnet, PhoneRunner.LogPath);
        Directory.CreateDirectory(RoutingPaths.RunDir);
        var checkPath = Path.Combine(RoutingPaths.RunDir, "phone.check.json");
        File.WriteAllText(checkPath, configJson);
        var (checkOk, checkOut) = RunProcess(singBoxExe, $"check -c \"{checkPath}\"", 10000);
        try { File.Delete(checkPath); } catch { }
        if (!checkOk)
            return new PhoneOperationResult(false, 1, $"sing-box check: {checkOut.Trim()}");

        // Если была включена обычная маршрутизация `route`, останавливаем до снятия снимка, чтобы не запомнить её временное отключение прокси
        RoutingCli.StopClientIfRunning();
        if (PhoneRunner.GetStatus().Running)
        {
            PhoneRunner.Stop(deleteTask: true);
            await WaitForAdapterRemovedAsync(SingBoxConfigBuilder.PhoneTunInterfaceName, 4000).ConfigureAwait(false);
        }

        EnsureBackupSnapshot();

        if (stopLocalHapp)
        {
            log?.Invoke("Останавливаю локальный Happ и отключаю системный прокси (чтобы не конфликтовали с TUN)...");
            StopLocalHappProcessesAndService();
            var happGone = await WaitForAdapterRemovedAsync(HappTunInterfaceName, 10000).ConfigureAwait(false);
            if (!happGone)
            {
                await StopAsync(clean: false, log).ConfigureAwait(false);
                return new PhoneOperationResult(false, 1, $"Не удалось убрать адаптер '{HappTunInterfaceName}'. Изменения откачены.");
            }
        }

        XeonProxyService.SetEnabled(false);

        log?.Invoke($"Запускаю sing-box TUN ({SingBoxConfigBuilder.PhoneTunInterfaceName}) -> {target.PhoneIp}:{socksPort}...");
        var err = await PhoneRunner.StartAsync(singBoxExe, configJson, options.PersistAtBoot).ConfigureAwait(false);
        if (err != null)
        {
            await StopAsync(clean: false, log).ConfigureAwait(false);
            return new PhoneOperationResult(false, 1, $"{err}\nИзменения откачены.");
        }

        var tunIdx = await WaitForAdapterUpAsync(SingBoxConfigBuilder.PhoneTunInterfaceName, 10000).ConfigureAwait(false);
        if (tunIdx == null)
        {
            await StopAsync(clean: false, log).ConfigureAwait(false);
            return new PhoneOperationResult(false, 1, $"Адаптер '{SingBoxConfigBuilder.PhoneTunInterfaceName}' не поднялся. Изменения откачены.");
        }

        if (routeTailscale)
        {
            var (routeOk, routeErr) = ConfigureTunDefaultRoute(tunIdx.Value);
            if (!routeOk)
                log?.Invoke($"Предупреждение: не удалось добавить 0.0.0.0/0 на {SingBoxConfigBuilder.PhoneTunInterfaceName}: {routeErr}");
        }

        SaveState(new PhoneRuntimeState(PhoneBridgeMode.Tun, target.PhoneIp, socksPort, target.WifiSubnet, DateTime.UtcNow));

        string? egressIp = null;
        if (!options.SkipHealthCheck)
        {
            log?.Invoke("Проверяю интернет через туннель телефона...");
            var healthy = await VerifyTunnelHealthAsync(log).ConfigureAwait(false);
            if (!healthy)
            {
                log?.Invoke("Проверка не прошла — откатываю изменения...");
                await StopAsync(clean: false, log).ConfigureAwait(false);
                return new PhoneOperationResult(false, 1, "Интернет через туннель телефона не отвечает (google.com/generate_204). Изменения откачены.");
            }
            var egress = await XeonProxyService.CheckEgressAsync(new ProxyState(false, "", ""), 4).ConfigureAwait(false);
            egressIp = egress?.Ip;
        }

        var exitNote = egressIp != null ? $" Внешний IP: {egressIp}." : "";
        var persistNote = options.PersistAtBoot ? " Автозапуск при старте Windows включён." : "";
        return new PhoneOperationResult(true, 0,
            $"Мост через телефон включён (TUN {SingBoxConfigBuilder.PhoneTunInterfaceName}) -> {target.PhoneIp}:{socksPort}.{exitNote}{persistNote}",
            egressIp);
    }

    [SupportedOSPlatform("windows")]
    public static async Task<PhoneOperationResult> StopAsync(bool clean = false, Action<string>? log = null)
    {
        var runner = PhoneRunner.GetStatus();
        var state = LoadState();
        var backup = LoadBackup();
        var proxy = XeonProxyService.GetState();

        var isPhoneProxyActive = state is { Mode: PhoneBridgeMode.Proxy } && proxy.Enabled &&
                                 string.Equals(XeonProxyService.ExtractHttpProxyEndpoint(proxy.Server), $"{state.PhoneIp}:{state.Port}", StringComparison.OrdinalIgnoreCase);

        if (!runner.Running && !isPhoneProxyActive && backup == null && state == null && !clean)
        {
            PhoneRunner.DeleteTask();
            return new PhoneOperationResult(true, 0, "Мост через телефон уже выключен.");
        }

        if (runner.Running && !RoutingCli.IsAdmin())
            return new PhoneOperationResult(false, 3, "Для остановки TUN-моста требуются права Администратора.");

        if (runner.Running || RoutingCli.IsAdmin())
        {
            PhoneRunner.Stop(deleteTask: true);
            await WaitForAdapterRemovedAsync(SingBoxConfigBuilder.PhoneTunInterfaceName, 4000).ConfigureAwait(false);
        }

        string note;
        if (!clean && backup != null)
        {
            XeonProxyService.RestoreState(backup.Proxy);
            if (backup.HappServiceExists && RoutingCli.IsAdmin())
                RestoreHappService(backup);
            note = backup.Proxy.Enabled
                ? $"Системный прокси восстановлен ({backup.Proxy.Server})."
                : "Системный прокси возвращён (выключен).";
        }
        else if (clean)
        {
            XeonProxyService.Disable(RoutingCli.IsAdmin());
            if (RoutingCli.IsAdmin())
            {
                KillHappProcesses();
                var (exists, _, _) = QueryHappService();
                if (exists)
                {
                    RunProcess("sc.exe", $"stop {HappServiceName}", 8000);
                    RunProcess("sc.exe", $"config {HappServiceName} start= demand", 5000);
                }
                RunProcess("ipconfig.exe", "/flushdns", 5000);
            }
            note = "Чистый сброс: прокси и локальный Happ отключены.";
        }
        else
        {
            if (isPhoneProxyActive)
                XeonProxyService.Disable(RoutingCli.IsAdmin());
            note = isPhoneProxyActive ? "Системный прокси телефона выключен." : "Выключено.";
        }

        DeleteFileQuiet(BackupPath);
        DeleteFileQuiet(StatePath);
        return new PhoneOperationResult(true, 0, $"Мост через телефон выключен. {note}");
    }

    private static async Task<string> EnsureSingBoxForPhoneAsync(Action<string>? log)
    {
        if (SingBoxInstaller.IsInstalled())
            return SingBoxInstaller.SingBoxExe;

        var hasHapp = File.Exists(HappSingBoxPath);
        using var cts = new CancellationTokenSource(hasHapp ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(10));
        try
        {
            return await SingBoxInstaller.EnsureAsync(log, cts.Token).ConfigureAwait(false);
        }
        catch when (hasHapp)
        {
            log?.Invoke($"Использую sing-box из Happ ({HappSingBoxPath}).");
            return HappSingBoxPath;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureBackupSnapshot()
    {
        if (File.Exists(BackupPath)) return;
        Directory.CreateDirectory(RoutingPaths.RunDir);
        var proxy = XeonProxyService.GetState();
        var (exists, startType, running) = QueryHappService();
        var snap = new PhoneBackupSnapshot(DateTime.UtcNow, proxy, exists, startType, running);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(snap, JsonOptions));
    }

    private static PhoneBackupSnapshot? LoadBackup()
    {
        try
        {
            return File.Exists(BackupPath)
                ? JsonSerializer.Deserialize<PhoneBackupSnapshot>(File.ReadAllText(BackupPath), JsonOptions)
                : null;
        }
        catch { return null; }
    }

    private static void SaveState(PhoneRuntimeState state)
    {
        Directory.CreateDirectory(RoutingPaths.RunDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static PhoneRuntimeState? LoadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<PhoneRuntimeState>(File.ReadAllText(StatePath), JsonOptions)
                : null;
        }
        catch { return null; }
    }

    private static void DeleteFileQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string ParseScStartType(string scQcOutput)
    {
        foreach (var raw in scQcOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("START_TYPE", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) || line.Contains(": 4"))
                return "disabled";
            if (line.Contains("DEMAND_START", StringComparison.OrdinalIgnoreCase) || line.Contains(": 3"))
                return "demand";
            if (line.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase) || line.Contains(": 2"))
            {
                return scQcOutput.Contains("DELAYED_AUTO_START", StringComparison.OrdinalIgnoreCase) &&
                       scQcOutput.Contains(": 1", StringComparison.Ordinal)
                    ? "delayed-auto"
                    : "auto";
            }
        }
        return "demand";
    }

    public static bool ParseScRunning(string scQueryOutput)
    {
        foreach (var raw in scQueryOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;
            return line.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || line.Contains(": 4");
        }
        return false;
    }

    private static (bool Exists, string StartType, bool Running) QueryHappService()
    {
        var (okQc, outQc) = RunProcess("sc.exe", $"qc {HappServiceName}", 5000);
        if (!okQc) return (false, "", false);
        var (_, outQuery) = RunProcess("sc.exe", $"query {HappServiceName}", 5000);
        return (true, ParseScStartType(outQc), ParseScRunning(outQuery));
    }

    private static void StopLocalHappProcessesAndService()
    {
        var (exists, _, _) = QueryHappService();
        if (exists)
        {
            RunProcess("sc.exe", $"config {HappServiceName} start= disabled", 5000);
            RunProcess("sc.exe", $"stop {HappServiceName}", 10000);
        }
        KillHappProcesses();
    }

    private static void KillHappProcesses()
    {
        foreach (var name in new[] { "Happ", "xray", "happd" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { }
                finally { p.Dispose(); }
            }
        }
    }

    private static void RestoreHappService(PhoneBackupSnapshot backup)
    {
        var startType = string.IsNullOrWhiteSpace(backup.HappServiceStartType) ? "demand" : backup.HappServiceStartType;
        RunProcess("sc.exe", $"config {HappServiceName} start= {startType}", 5000);
        if (backup.HappServiceWasRunning && !QueryHappService().Running)
            RunProcess("sc.exe", $"start {HappServiceName}", 10000);
    }

    private static async Task<int?> WaitForAdapterUpAsync(string adapterName, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Name, adapterName, StringComparison.OrdinalIgnoreCase) &&
                                         n.OperationalStatus == OperationalStatus.Up);
                if (nic != null)
                {
                    var idx = nic.GetIPProperties().GetIPv4Properties()?.Index;
                    if (idx is > 0) return idx.Value;
                }
            }
            catch { }
            await Task.Delay(300).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<bool> WaitForAdapterRemovedAsync(string adapterName, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var exists = NetworkInterface.GetAllNetworkInterfaces()
                    .Any(n => string.Equals(n.Name, adapterName, StringComparison.OrdinalIgnoreCase));
                if (!exists) return true;
            }
            catch { return true; }
            await Task.Delay(300).ConfigureAwait(false);
        }
        return false;
    }

    private static (bool Ok, string Output) ConfigureTunDefaultRoute(int interfaceIndex)
    {
        var ps =
            $"Set-NetIPInterface -InterfaceIndex {interfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1 -ErrorAction Stop; " +
            $"Get-NetRoute -InterfaceIndex {interfaceIndex} -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"New-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceIndex {interfaceIndex} -NextHop '{SingBoxConfigBuilder.PhoneTunNextHop}' -RouteMetric 0 -ErrorAction Stop | Out-Null";
        return RunProcess("powershell.exe", $"-NoProfile -NonInteractive -Command \"{ps}\"", 10000);
    }

    private static async Task<bool> VerifyTunnelHealthAsync(Action<string>? log)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        for (int i = 1; i <= 6; i++)
        {
            await Task.Delay(1500).ConfigureAwait(false);
            if (!NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                    string.Equals(n.Name, SingBoxConfigBuilder.PhoneTunInterfaceName, StringComparison.OrdinalIgnoreCase) &&
                    n.OperationalStatus == OperationalStatus.Up))
            {
                continue;
            }

            try
            {
                using var resp = await http.GetAsync("https://www.google.com/generate_204").ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                log?.Invoke($"  попытка {i}: google.com/generate_204 -> HTTP {code}");
                if (code == 204) return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"  попытка {i}: {ex.Message}");
            }
        }
        return false;
    }

    private static (bool Ok, string Output) RunProcess(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (false, "timeout"); }
            return (p.ExitCode == 0, o.Result + e.Result);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
