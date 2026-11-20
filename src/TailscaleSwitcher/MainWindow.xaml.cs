using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TailscaleSwitcher.Proxy;
using TailscaleSwitcher.Routing;
using TailscaleSwitcher.Services;
using TailscaleSwitcher.Update;

namespace TailscaleSwitcher;

public partial class MainWindow : Window
{
    private readonly Func<EventWaitHandle?>? _showEventProvider;
    private readonly RoutingConfigStore _store = new();
    private readonly TrayIcon _tray;

    private RoutingConfig _cfg = new();
    private List<ExitNodeInfo> _exitNodes = [];
    private EgressInfo? _egress;

    private bool _busy;
    private bool _reallyExit;
    private bool _trayHintShown;
    private bool _suppressProfile;
    private bool _suppressMode;
    private bool _initialized;

    public MainWindow(Func<EventWaitHandle?>? showEventProvider = null, bool startHidden = false)
    {
        InitializeComponent();
        _initialized = true;
        _showEventProvider = showEventProvider;
        WindowFrame.Hook(this);

        VersionLabel.Text = $"v{AppVersion.Informational}";

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.ToggleRouteRequested += () => Dispatcher.InvokeAsync(() => ToggleRouteAsync());
        _tray.ToggleProxyRequested += () => Dispatcher.InvokeAsync(() => ToggleProxyAsync());
        _tray.DisconnectExitNodeRequested += () => Dispatcher.InvokeAsync(() => ExitDisconnectAsync());
        _tray.CheckIpRequested += () => Dispatcher.InvokeAsync(() => CheckIpAsync());
        _tray.UpdateRequested += () => Dispatcher.InvokeAsync(() => UpdateCheckAsync());
        _tray.RestartRequested += () => Dispatcher.Invoke(RestartFromTray);
        _tray.ExitRequested += () => Dispatcher.Invoke(() => { _reallyExit = true; Close(); });

        Loaded += async (_, _) => await RefreshAllAsync();
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";

        if (startHidden) Hide();
        StartShowEventListener();
        _ = RefreshAllAsync();
    }

    // ────────────────────────────────────────────── Окно и трей

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideToTray();

    /// <summary>Разрешает реальное закрытие окна (выход из приложения или перезапуск).</summary>
    public void PrepareExit() => _reallyExit = true;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloon("Tailscale Switcher", "Приложение продолжает работать в трее возле часов.");
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void StartShowEventListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                EventWaitHandle? handle;
                try { handle = _showEventProvider?.Invoke(); }
                catch { return; }
                if (handle == null) { Thread.Sleep(500); continue; }
                try { handle.WaitOne(); }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { return; }
                Dispatcher.BeginInvoke(ShowFromTray);
            }
        })
        { IsBackground = true, Name = "tray-show-listener" };
        thread.Start();
    }

    private void OnAdminRestartClick(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).RestartApplication(asAdmin: true, startInTray: false);

    /// <summary>Перезапуск той же копией, сразу в трей — иконку искать не нужно.</summary>
    private void RestartFromTray() =>
        ((App)Application.Current).RestartApplication(asAdmin: TailscaleService.IsAdmin(), startInTray: true);

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
        await UpdateCheckAsync(quiet: true);
    }

    // ────────────────────────────────────────────── Вкладки

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        PanelOverview.Visibility = TabOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelRouting.Visibility = TabRouting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelExit.Visibility = TabExit.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelRelay.Visibility = TabRelay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility = TabLog.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ────────────────────────────────────────────── Обзор

    private async void OnCheckIpClick(object sender, RoutedEventArgs e) => await CheckIpAsync();

    private async void OnGoDirectClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdminAsync("«Прямой выход» (выключение маршрутизации и exit-node)")) return;
        await RunRoutingAsync("route", "off");
        await RunTailscaleAsync("set --exit-node=");
        XeonProxyService.Disable(TailscaleService.IsAdmin());
        AppendLog("Системный прокси Xeon выключен.");
        await RefreshAllAsync();
        SetStatus("Прямой выход: маршрут, exit-node и прокси выключены.");
    }

    private async void OnRouteToggleClick(object sender, RoutedEventArgs e) => await ToggleRouteAsync();

    private void OnGoRoutingClick(object sender, RoutedEventArgs e) => TabRouting.IsChecked = true;

    private async void OnExitOffClick(object sender, RoutedEventArgs e) => await ExitDisconnectAsync();

    private void OnGoExitClick(object sender, RoutedEventArgs e) => TabExit.IsChecked = true;

    private async void OnProxyToggleClick(object sender, RoutedEventArgs e) => await ToggleProxyAsync();

    private async void OnProxyCheckClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var state = XeonProxyService.GetState();
            var (endpoint, ok) = await XeonProxyService.ResolveEndpointAsync(null);
            var direct = await XeonProxyService.CheckEgressAsync(new ProxyState(false, "", ""), 6);
            var proxied = await XeonProxyService.CheckEgressMultiAsync(state, 6);
            _egress = direct ?? (proxied.Count > 0 ? proxied[0] : null);
            ProxyDetailLabel.Text = ok
                ? $"Xeon доступен ({endpoint}); прокси {(state.Enabled ? $"включён → {state.Server}" : "выключен")}"
                : "Xeon не отвечает (ни 192.168.1.5:10809, ни 100.119.48.15:10809)";
            var both = FormatEgressBoth(direct, state.Enabled ? proxied : []);
            HeroIpLine.Text = both;
            AppendLog($"Проверка прокси: {ProxyDetailLabel.Text}\n{both}");
        }
        finally { SetBusy(false); }
    }

    // ────────────────────────────────────────────── Маршрутизация: профили

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfile) return;
        if (ProfileCombo.SelectedIndex < 0) return;
        var name = ProfileCombo.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        _cfg.Active = name;
        SaveCfg();
        FillRoutingTab();
    }

    private async void OnProfileAddClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        List<TailnetPeerInfo> peers;
        try { peers = await Task.Run(() => TailscaleService.GetTailnetPeers()); }
        finally { SetBusy(false); }

        var wizard = new ProfileWizardWindow(_cfg.Profiles.Select(p => p.Name), peers) { Owner = this };
        if (wizard.ShowDialog() != true || wizard.CreatedProfile == null) return;
        _cfg.Profiles.Add(wizard.CreatedProfile);
        _cfg.Active = wizard.CreatedProfile.Name;
        SaveCfg();
        FillRoutingTab();
        SetStatus($"Профиль «{wizard.CreatedProfile.Name}» создан.");
        AppendLog($"Создан профиль «{wizard.CreatedProfile.Name}» → {wizard.CreatedProfile.Node}:{wizard.CreatedProfile.Port}.");
    }

    private async void OnProfileEditClick(object sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile();
        if (profile == null) { SetStatus("Нет профиля для изменения."); return; }
        var wasActive = string.Equals(_cfg.Active, profile.Name, StringComparison.OrdinalIgnoreCase);
        SetBusy(true);
        List<TailnetPeerInfo> peers;
        try { peers = await Task.Run(() => TailscaleService.GetTailnetPeers()); }
        finally { SetBusy(false); }

        var otherNames = _cfg.Profiles
            .Select(p => p.Name)
            .Where(n => !string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var wizard = new ProfileWizardWindow(otherNames, peers, editing: profile) { Owner = this };
        if (wizard.ShowDialog() != true) return;
        if (wasActive) _cfg.Active = profile.Name;
        SaveCfg();
        FillRoutingTab();
        SetStatus($"Профиль «{profile.Name}» сохранён." +
            (RoutingCli.ClientStatus().Running ? " Маршрутизация включена — выключи и включи её снова, чтобы применилось." : ""));
        AppendLog($"Профиль изменён: «{profile.Name}» → {profile.Node}:{profile.Port}, {RoutingCli.ModeName(profile)}.");
    }

    private void OnProfileDeleteClick(object sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile();
        if (profile == null) return;
        if (MessageBox.Show(this, $"Удалить профиль «{profile.Name}»?", "Tailscale Switcher",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _cfg.Profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_cfg.Active, profile.Name, StringComparison.OrdinalIgnoreCase)) _cfg.Active = null;
        SaveCfg();
        FillRoutingTab();
        SetStatus($"Профиль «{profile.Name}» удалён.");
    }

    // ────────────────────────────────────────────── Маршрутизация: режим и списки

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressMode) return;
        var profile = ActiveProfile();
        if (profile == null) return;
        var all = ModeAllRadio.IsChecked == true;
        if (profile.AllTraffic == all) return;
        profile.AllTraffic = all;
        SaveCfg();
        FillRoutingTab();
        SetStatus(all
            ? "Режим: весь трафик через узел. Если маршрутизация включена — выключи и включи её снова."
            : "Режим: только выбранные программы и адреса. Если маршрутизация включена — выключи и включи её снова.");
    }

    private void OnAppAddClick(object sender, RoutedEventArgs e) => AddListItem(AppAddBox, (p, v) => p.Apps.Add(v), "программа");

    private void OnAppFromRunningClick(object sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile();
        if (profile == null) { SetStatus("Сначала создай профиль (шаг 1)."); return; }
        var picker = new RunningAppsWindow { Owner = this };
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedExe)) return;
        if (!profile.Apps.Contains(picker.SelectedExe, StringComparer.OrdinalIgnoreCase))
            profile.Apps.Add(picker.SelectedExe!);
        SaveCfg();
        FillRoutingTab();
    }

    private void OnAppRemoveClick(object sender, RoutedEventArgs e) => RemoveListItem(AppsList, (p, v) => p.Apps.Remove(v));

    private void OnHostAddClick(object sender, RoutedEventArgs e) => AddListItem(HostAddBox, (p, v) => p.Hosts.Add(v), "адрес");

    private void OnHostRemoveClick(object sender, RoutedEventArgs e) => RemoveListItem(HostsList, (p, v) => p.Hosts.Remove(v));

    private void OnBypassAppAddClick(object sender, RoutedEventArgs e) => AddListItem(BypassAppAddBox, (p, v) => p.BypassApps.Add(v), "программа");

    private void OnBypassAppRemoveClick(object sender, RoutedEventArgs e) => RemoveListItem(BypassAppsList, (p, v) => p.BypassApps.Remove(v));

    private void OnBypassHostAddClick(object sender, RoutedEventArgs e) => AddListItem(BypassHostAddBox, (p, v) => p.BypassHosts.Add(v), "адрес");

    private void OnBypassHostRemoveClick(object sender, RoutedEventArgs e) => RemoveListItem(BypassHostsList, (p, v) => p.BypassHosts.Remove(v));

    private void AddListItem(TextBox box, Action<RouteProfile, string> add, string kind)
    {
        var profile = ActiveProfile();
        if (profile == null) { SetStatus("Сначала создай профиль (шаг 1)."); return; }
        var value = box.Text.Trim();
        if (value.Length == 0 || value.Contains(' '))
        {
            SetStatus($"Пустое или некорректное значение ({kind}).");
            return;
        }
        add(profile, value);
        box.Text = "";
        SaveCfg();
        FillRoutingTab();
    }

    private void RemoveListItem(ListBox list, Action<RouteProfile, string> remove)
    {
        var profile = ActiveProfile();
        if (profile == null || list.SelectedItem is not string value) return;
        remove(profile, value);
        SaveCfg();
        FillRoutingTab();
    }

    // ────────────────────────────────────────────── Маршрутизация: вкл/выкл

    private async void OnRoutePowerClick(object sender, RoutedEventArgs e) => await ToggleRouteAsync();

    private async void OnRouteProbeClick(object sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile();
        if (profile == null) { SetStatus("Сначала создай профиль (шаг 1)."); return; }
        SetBusy(true);
        RouteProbeLabel.Text = $"Проверяю {profile.Node}:{profile.Port}…";
        try
        {
            var ok = await XeonProxyService.ProbeTcpAsync($"{profile.Node}:{profile.Port}", 2500);
            RouteProbeLabel.Text = ok
                ? $"Узел {profile.Node}:{profile.Port} отвечает — relay работает."
                : $"Нет ответа от {profile.Node}:{profile.Port}. На выходном ПК нужны 'relay start' и 'relay firewall'.";
        }
        finally { SetBusy(false); }
    }

    private async Task ToggleRouteAsync()
    {
        if (_busy) return;
        if (RoutingCli.ClientStatus().Running)
        {
            if (!await EnsureAdminAsync("Выключение маршрутизации")) return;
            await RunRoutingAsync("route", "off");
        }
        else
        {
            var profile = ActiveProfile();
            if (profile == null)
            {
                SetStatus("Нет профиля выходного узла — открой «Маршрутизация» и нажми «Новый профиль».");
                TabRouting.IsChecked = true;
                return;
            }
            if (!await EnsureAdminAsync("Включение маршрутизации (TUN)")) return;
            await RunRoutingAsync("route", "on", profile.Name);
        }
        await RefreshAllAsync();
    }

    private async Task ToggleProxyAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var state = XeonProxyService.GetState();
            if (state.Enabled)
            {
                XeonProxyService.Disable(TailscaleService.IsAdmin());
                AppendLog("Системный прокси Xeon выключен.");
            }
            else
            {
                var (endpoint, ok) = await XeonProxyService.ResolveEndpointAsync(null);
                if (!ok)
                {
                    SetStatus("Прокси на Xeon не отвечает (192.168.1.5:10809 / 100.119.48.15:10809). Проверь, что xeon включён и на нём работает Happ.");
                    return;
                }
                XeonProxyService.Enable(endpoint, alsoWinHttp: false, isAdmin: TailscaleService.IsAdmin());
                AppendLog($"Системный прокси Xeon включён → {endpoint}");
            }
        }
        finally { SetBusy(false); }
        await RefreshAllAsync();
    }

    // ────────────────────────────────────────────── Exit-node

    private async void OnExitConnectClick(object sender, RoutedEventArgs e)
    {
        if (ExitNodesCombo.SelectedIndex < 0 || ExitNodesCombo.SelectedIndex >= _exitNodes.Count)
        {
            SetStatus("Выбери узел из списка (сначала нажми «Обновить список», если он пуст).");
            return;
        }
        if (!await EnsureAdminAsync("Подключение exit-node")) return;
        var node = _exitNodes[ExitNodesCombo.SelectedIndex];
        var lan = AllowLanCheck.IsChecked == true;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => TailscaleService.SetExitNode(node.TailscaleIP, lan));
            AppendLog($"tailscale set --exit-node={node.TailscaleIP} → {(result.Ok ? "OK" : result.Error.Trim())}");
            SetStatus(result.Ok ? $"Exit-node: {node.HostName} ({node.TailscaleIP})." : $"Ошибка: {result.Error.Trim()}");
        }
        finally { SetBusy(false); }
        await RefreshAllAsync();
    }

    private async void OnExitDisconnectClick(object sender, RoutedEventArgs e) => await ExitDisconnectAsync();

    private async void OnExitRefreshClick(object sender, RoutedEventArgs e) => await LoadExitNodesAsync();

    private async void OnAutoFixClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdminAsync("Авто-фикс Happ + exit-node")) return;
        SetBusy(true);
        try
        {
            var ok = await TailscaleService.AutoFixHappExitNodeAsync(msg => Dispatcher.Invoke(() => AppendLog(msg)));
            SetStatus(ok ? "Авто-фикс завершён." : "Авто-фикс не полностью успешен — смотри журнал.");
        }
        finally { SetBusy(false); }
        await RefreshAllAsync();
    }

    private void OnOpenAdminClick(object sender, RoutedEventArgs e) => TailscaleService.OpenAdmin();

    private async Task ExitDisconnectAsync()
    {
        if (!await EnsureAdminAsync("Отключение exit-node")) return;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => TailscaleService.SetExitNode(null, false));
            AppendLog($"tailscale set --exit-node= → {(result.Ok ? "OK" : result.Error.Trim())}");
        }
        finally { SetBusy(false); }
        await RefreshAllAsync();
    }

    // ────────────────────────────────────────────── Relay

    private void OnRelayInitClick(object sender, RoutedEventArgs e)
    {
        var args = new List<string> { "relay", "init" };
        if (int.TryParse(RelayPortBox.Text.Trim(), out var port)) args.Add($"--port {port}");
        if (RelayUserBox.Text.Trim().Length > 0) args.Add($"--user {RelayUserBox.Text.Trim()}");
        if (RelayUpstreamBox.Text.Trim().Length > 0) args.Add($"--upstream {RelayUpstreamBox.Text.Trim()}");
        _ = RunRoutingAndRefreshAsync(args.ToArray());
    }

    private async void OnRelayStartClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdminAsync("Запуск relay")) return;
        await RunRoutingAndRefreshAsync("relay", "start");
    }

    private async void OnRelayPersistClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdminAsync("Запуск relay с автозапуском")) return;
        await RunRoutingAndRefreshAsync("relay", "start", "--persist");
    }

    private async void OnRelayStopClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdminAsync("Остановка relay")) return;
        await RunRoutingAndRefreshAsync("relay", "stop");
    }

    private async void OnRelayUninstallClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdminAsync("Удаление автозапуска relay")) return;
        await RunRoutingAndRefreshAsync("relay", "uninstall");
    }

    private async void OnRelayFirewallClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdminAsync("Создание правила firewall")) return;
        await RunRoutingAndRefreshAsync("relay", "firewall");
    }

    private void OnRelayInfoClick(object sender, RoutedEventArgs e) => _ = FillRelayInfoAsync();

    private void OnRelayCopyClick(object sender, RoutedEventArgs e)
    {
        if (RelayInfoBox.Text.Trim().Length == 0) { _ = FillRelayInfoAsync(copy: true); return; }
        try
        {
            Clipboard.SetText(RelayInfoBox.Text.Trim());
            SetStatus("Команда скопирована в буфер обмена.");
        }
        catch (Exception ex) { SetStatus($"Не удалось скопировать: {ex.Message}"); }
    }

    private async Task FillRelayInfoAsync(bool copy = false)
    {
        var (_, output) = await RunRoutingAsync("relay", "info");
        var line = output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("TailscaleSwitcher route profile add", StringComparison.Ordinal));
        RelayInfoBox.Text = line ?? output;
        if (copy && RelayInfoBox.Text.Trim().Length > 0)
        {
            try
            {
                Clipboard.SetText(RelayInfoBox.Text.Trim());
                SetStatus("Команда скопирована в буфер обмена.");
            }
            catch (Exception ex) { SetStatus($"Не удалось скопировать: {ex.Message}"); }
        }
    }

    // ────────────────────────────────────────────── Журнал

    private void OnLogCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            SetStatus("Журнал скопирован в буфер обмена.");
        }
        catch (Exception ex) { SetStatus($"Не удалось скопировать: {ex.Message}"); }
    }

    private void OnLogClearClick(object sender, RoutedEventArgs e) => LogBox.Text = "";

    private void OnLogClientTailClick(object sender, RoutedEventArgs e) =>
        AppendLog("─── хвост лога маршрутизации (sing-box) ───\n" + RoutingCli.ClientLogTail(40));

    private void OnLogRelayTailClick(object sender, RoutedEventArgs e) =>
        AppendLog("─── хвост лога relay (sing-box) ───\n" + RoutingCli.RelayLogTail(40));

    // ────────────────────────────────────────────── Обновления

    private async Task UpdateCheckAsync(bool quiet = false)
    {
        if (!AppVersion.CanSelfUpdate(Environment.ProcessPath))
        {
            SetStatus(quiet
                ? "Состояние обновлено."
                : "Self-update доступен только для установленной копии (не из bin/).");
            return;
        }
        SetBusy(true);
        try
        {
            var report = await AppUpdater.CheckAsync();
            if (report.Status == SelfUpdateStatus.AlreadyCurrent)
            {
                SetStatus(quiet
                    ? $"Состояние обновлено. TailscaleSwitcher {report.Installed} — актуальна."
                    : $"Уже актуален: {report.Installed}");
                return;
            }
            if (MessageBox.Show(this, $"Доступно обновление {report.Installed} → {report.Release}. Установить?",
                    "Tailscale Switcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var applied = await AppUpdater.ApplyInProcessAsync((recv, total) =>
                Dispatcher.Invoke(() => SetStatus($"Загрузка обновления: {recv / 1048576.0:0.0}/{(total ?? recv) / 1048576.0:0.0} MB")));
            AppendLog($"Обновление установлено: {applied.Tag}. Перезапуск.");
            SetStatus($"Установлено {applied.Tag}. Перезапуск…");
            RestartFromTray();
            return;
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка обновления: {ex.Message}");
            SetStatus($"Ошибка обновления: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    // ────────────────────────────────────────────── Состояние

    private async Task RefreshAllAsync()
    {
        try
        {
            var snap = await Task.Run(() =>
            {
                var cfg = _store.Load();
                var route = RoutingCli.ClientStatus();
                var relay = RoutingCli.RelayStatusSnapshot();
                var proxy = XeonProxyService.GetState();
                return (cfg, route, relay, proxy,
                    ip: TailscaleService.GetTailscaleIp4(),
                    prefs: TailscaleService.GetPrefs(),
                    admin: TailscaleService.IsAdmin());
            });

            _cfg = snap.cfg;

            MachineLabel.Text = $"ПК: {Environment.MachineName}";
            IpLabel.Text = $"Tailscale IP: {snap.ip ?? "—"}";
            AdminLabel.Text = snap.admin ? "Администратор" : "Обычные права";
            AdminPill.Background = snap.admin
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x33, 0x2A))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3D, 0x32, 0x1B));
            AdminLabel.Foreground = snap.admin
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x61, 0xE8, 0xBA))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD1, 0x66));
            AdminRestartButton.Visibility = snap.admin ? Visibility.Collapsed : Visibility.Visible;

            // Профиль и маршрутизация
            var profile = _cfg.FindActiveOrOnly();
            var running = snap.route.Running;
            RouteStateLabel.Text = running ? "Включена" : "Выключена";
            RouteDetailLabel.Text = profile == null
                ? "Профиль не выбран"
                : $"{profile.Name} → {profile.Node}:{profile.Port}, {RoutingCli.ModeName(profile)}";
            RouteToggle.IsChecked = running;
            RoutePowerLabel.Text = running
                ? $"Маршрутизация включена (sing-box pid {snap.route.Pid})"
                : "Маршрутизация выключена";
            RoutePowerButton.Content = running ? "Выключить маршрутизацию" : "Включить маршрутизацию";
            if (RoutingCli.IsSystemProxySuspended())
                RouteDetailLabel.Text += " · системный прокси Windows временно отключён";

            // Exit-node
            var exitIp = snap.prefs?.ExitNodeIP ?? "";
            var hasExit = exitIp.Length > 0;
            ExitStateLabel.Text = hasExit ? "Подключён" : "Не подключён";
            ExitDetailLabel.Text = hasExit ? $"Весь трафик через {exitIp}" : "Трафик идёт напрямую";
            ExitOffButton.IsEnabled = hasExit;

            // Прокси
            ProxyToggle.IsChecked = snap.proxy.Enabled;
            ProxyStateLabel.Text = snap.proxy.Enabled ? "Включён" : "Выключен";
            ProxyDetailLabel.Text = snap.proxy.Enabled
                ? $"Системный прокси Windows → {snap.proxy.Server}"
                : "Системный прокси Windows выключен";

            // Relay
            RelayStatusLabel.Text = snap.relay.Running
                ? $"Relay работает (pid {snap.relay.Pid})"
                : "Relay остановлен";
            var listen = RoutingCli.ResolveRelayListenAddress(_cfg.Relay);
            RelayListenLabel.Text = $"Слушает: {listen ?? "?"}:{_cfg.Relay.Port}, апстрим: {(string.IsNullOrWhiteSpace(_cfg.Relay.Upstream) ? "direct" : _cfg.Relay.Upstream)}";

            FillRoutingTab();
            FillRelayTab();
            LoadExitNodesFromCache(snap.prefs);
            UpdateHero(snap.route, profile, exitIp, snap.proxy);
            UpdateTray(snap.route, snap.proxy, hasExit, snap.relay);
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка обновления состояния: {ex.Message}");
        }
    }

    private void UpdateHero(RunnerStatus route, RouteProfile? profile, string exitIp, ProxyState proxy)
    {
        if (route.Running && profile != null)
        {
            HeroTitle.Text = $"Через узел «{profile.Name}» ({profile.Node}:{profile.Port})";
            HeroDetail.Text = profile.AllTraffic
                ? $"Весь трафик идёт через этот узел. Исключений: приложений {profile.BypassApps.Count}, адресов {profile.BypassHosts.Count}."
                : $"Только выбранные программы ({profile.Apps.Count}) и адреса ({profile.Hosts.Count}).";
        }
        else if (exitIp.Length > 0)
        {
            var host = _exitNodes.FirstOrDefault(n => n.TailscaleIP == exitIp)?.HostName;
            HeroTitle.Text = $"Через exit-node {(string.IsNullOrWhiteSpace(host) ? exitIp : $"{host} ({exitIp})")}";
            HeroDetail.Text = "Весь трафик Tailscale идёт через выбранный узел Tailnet.";
        }
        else if (proxy.Enabled)
        {
            var isLocal = proxy.Server.StartsWith("127.", StringComparison.Ordinal) ||
                          proxy.Server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
            HeroTitle.Text = isLocal
                ? $"Через прокси Happ ({proxy.Server})"
                : $"Через прокси Xeon ({proxy.Server})";
            HeroDetail.Text = "Системный прокси Windows направляет трафик через этот прокси.";
        }
        else
        {
            HeroTitle.Text = "Напрямую — без узлов и прокси";
            HeroDetail.Text = "Маршрутизация, exit-node и системный прокси выключены.";
        }
    }

    private void UpdateTray(RunnerStatus route, ProxyState proxy, bool hasExit, RunnerStatus relay)
    {
        var routed = route.Running || proxy.Enabled || hasExit;
        var state = routed ? TrayVisualState.Routed : relay.Running ? TrayVisualState.RelayOnly : TrayVisualState.Direct;
        var summary = route.Running
            ? "маршрутизация включена"
            : hasExit
                ? "exit-node подключён"
                : proxy.Enabled
                    ? "прокси Xeon включён"
                    : relay.Running
                        ? "relay работает"
                        : "прямой выход";
        _tray.UpdateState(state, summary, route.Running, proxy.Enabled, hasExit, _busy);
    }

    private void FillRoutingTab()
    {
        _suppressProfile = true;
        try
        {
            ProfileCombo.Items.Clear();
            foreach (var p in _cfg.Profiles) ProfileCombo.Items.Add(p.Name);
            var active = ActiveProfile();
            if (active != null) ProfileCombo.SelectedItem = active.Name;
            else if (ProfileCombo.Items.Count > 0) ProfileCombo.SelectedIndex = 0;
        }
        finally { _suppressProfile = false; }

        var profile = ActiveProfile();
        if (profile == null)
        {
            ProfileInfoLabel.Text = _cfg.Profiles.Count == 0
                ? "Профилей пока нет — нажми «Новый профиль»."
                : "Активный профиль не выбран.";
            return;
        }
        ProfileInfoLabel.Text = profile.AllTraffic
            ? $"Режим: весь трафик через {profile.Node}:{profile.Port}. Не через узел: приложений {profile.BypassApps.Count}, адресов {profile.BypassHosts.Count}."
            : $"Режим: только выбранные. Программ: {profile.Apps.Count}, адресов: {profile.Hosts.Count}. Relay: {profile.Node}:{profile.Port}.";

        _suppressMode = true;
        try
        {
            ModeAllRadio.IsChecked = profile.AllTraffic;
            ModeSelectiveRadio.IsChecked = !profile.AllTraffic;
        }
        finally { _suppressMode = false; }

        SelectiveSection.Visibility = profile.AllTraffic ? Visibility.Collapsed : Visibility.Visible;
        AllSection.Visibility = profile.AllTraffic ? Visibility.Visible : Visibility.Collapsed;

        FillList(AppsList, profile.Apps);
        FillList(HostsList, profile.Hosts);
        FillList(BypassAppsList, profile.BypassApps);
        FillList(BypassHostsList, profile.BypassHosts);
    }

    private static void FillList(ListBox list, List<string> items)
    {
        list.Items.Clear();
        foreach (var item in items) list.Items.Add(item);
    }

    private void FillRelayTab()
    {
        RelayPortBox.Text = _cfg.Relay.Port.ToString();
        RelayUserBox.Text = _cfg.Relay.Username;
        RelayUpstreamBox.Text = _cfg.Relay.Upstream;
    }

    private void LoadExitNodesFromCache(TailscalePrefs? prefs)
    {
        ExitNodesCombo.Items.Clear();
        foreach (var n in _exitNodes)
        {
            var loc = string.IsNullOrWhiteSpace(n.Location) ? "" : $", {n.Location}";
            ExitNodesCombo.Items.Add($"{n.HostName} — {n.TailscaleIP}{loc}, {(n.Online ? "online" : "offline")}");
        }
        if (_exitNodes.Count == 0)
        {
            ExitNodesCombo.Items.Add("Узлы не найдены — нажми «Обновить список»");
            ExitNodesCombo.SelectedIndex = 0;
            ExitNodesCombo.IsEnabled = false;
            return;
        }
        ExitNodesCombo.IsEnabled = true;
        var idx = _exitNodes.FindIndex(n => n.TailscaleIP == (prefs?.ExitNodeIP ?? ""));
        ExitNodesCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private async Task LoadExitNodesAsync()
    {
        SetBusy(true);
        ExitStatusLabel.Text = "Загружаю список exit-node…";
        try
        {
            _exitNodes = await Task.Run(() => TailscaleService.GetExitNodes());
            var prefs = await Task.Run(() => TailscaleService.GetPrefs());
            LoadExitNodesFromCache(prefs);
            ExitStatusLabel.Text = _exitNodes.Count == 0
                ? "Узлы exit-node не найдены. Проверь 'tailscale exit-node list' и статус сети."
                : $"Найдено узлов: {_exitNodes.Count}.";
        }
        finally { SetBusy(false); }
    }

    // ────────────────────────────────────────────── Инфраструктура

    private RouteProfile? ActiveProfile() => _cfg.Find(_cfg.Active) ?? (_cfg.Profiles.Count == 1 ? _cfg.Profiles[0] : null);

    private void SaveCfg()
    {
        try { _store.Save(_cfg); }
        catch (Exception ex) { SetStatus($"Не удалось сохранить настройки: {ex.Message}"); }
    }

    private async Task<(int Code, string Output)> RunRoutingAsync(params string[] args)
    {
        SetBusy(true);
        try
        {
            var sw = new StringWriter();
            var cli = new RoutingCli(output: sw, error: sw);
            var code = await cli.RunAsync(args);
            var text = sw.ToString().Trim();
            AppendLog($"> {string.Join(' ', args)}\n{(text.Length > 0 ? text : "(нет вывода)")}");
            return (code, text);
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка: {ex.Message}");
            return (-1, ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async Task RunRoutingAndRefreshAsync(params string[] args)
    {
        var (code, output) = await RunRoutingAsync(args);
        SetStatus(code == 0
            ? (output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "Готово.")
            : "Ошибка — смотри журнал.");
        await RefreshAllAsync();
    }

    private async Task RunTailscaleAsync(string tailscaleArgs)
    {
        var result = await Task.Run(() => TailscaleService.RunTailscale(tailscaleArgs, 10000));
        AppendLog($"> tailscale {tailscaleArgs}\n{(result.Ok ? (result.Output.Trim().Length > 0 ? result.Output.Trim() : "OK") : result.Error.Trim())}");
    }

    private async Task<bool> EnsureAdminAsync(string action)
    {
        if (TailscaleService.IsAdmin()) return true;
        var answer = MessageBox.Show(this,
            $"{action} требует права Администратора.\nПерезапустить приложение от Администратора?",
            "Tailscale Switcher", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            ((App)Application.Current).RestartApplication(asAdmin: true, startInTray: false);
        return false;
    }

    private async Task CheckIpAsync()
    {
        SetBusy(true);
        try
        {
            var state = XeonProxyService.GetState();
            var direct = await XeonProxyService.CheckEgressAsync(new ProxyState(false, "", ""), 6);
            var viaProxy = state.Enabled
                ? await XeonProxyService.CheckEgressMultiAsync(state, 6)
                : [];

            var text = FormatEgressBoth(direct, viaProxy);
            _egress = direct ?? (viaProxy.Count > 0 ? viaProxy[0] : null);
            HeroIpLine.Text = text;
            AppendLog(text);
        }
        finally { SetBusy(false); }
    }

    /// <summary>Показывает оба выхода двумя строками: напрямую и через системный прокси.</summary>
    private static string FormatEgressBoth(EgressInfo? direct, IReadOnlyList<EgressInfo> viaProxy)
    {
        var proxied = viaProxy.Where(e => !string.IsNullOrWhiteSpace(e.Ip)).ToList();
        if (direct == null && proxied.Count == 0) return "Внешний IP: не удалось проверить";
        if (proxied.Count == 0) return $"Внешний IP: {FormatEgress("", direct).Trim()} (напрямую)";

        var viaText = string.Join(" / ", proxied.Select(e => FormatEgress("", e).Trim()));
        if (direct == null) return $"Внешний IP через прокси: {viaText}";

        var same = proxied.Count == 1 && proxied[0].Ip == direct.Ip;
        return $"Напрямую: {FormatEgress("", direct).Trim()}\nЧерез прокси: {(same ? "то же (одинаково)" : viaText)}";
    }

    private static string FormatEgress(string prefix, EgressInfo? egress)
    {
        if (egress == null) return $"{prefix}не удалось проверить";
        var loc = string.Join(", ", new[] { egress.City, egress.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(loc) ? $"{prefix}{egress.Ip}" : $"{prefix}{egress.Ip} ({loc})";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void AppendLog(string text)
    {
        void Append()
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
            if (LogBox.Text.Length > 120_000) LogBox.Text = LogBox.Text[^60_000..];
            LogBox.ScrollToEnd();
        }
        if (Dispatcher.CheckAccess()) Append();
        else Dispatcher.Invoke(Append);
    }
}
