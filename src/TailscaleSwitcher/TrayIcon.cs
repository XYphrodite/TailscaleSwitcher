using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TailscaleSwitcher.Update;

namespace TailscaleSwitcher;

public enum TrayVisualState
{
    Direct,
    Routed,
    RelayOnly
}

/// <summary>Иконка в системном трее с цветовым индикатором состояния и быстрыми действиями.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _routeItem;
    private readonly ToolStripMenuItem _phoneItem;
    private readonly ToolStripMenuItem _proxyItem;
    private readonly ToolStripMenuItem _exitNodeOffItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly Icon _directIcon;
    private readonly Icon _routedIcon;
    private readonly Icon _relayIcon;
    private TrayVisualState _lastState = (TrayVisualState)(-1);

    public event Action? OpenRequested;
    public event Action? ToggleRouteRequested;
    public event Action? TogglePhoneRequested;
    public event Action? ToggleProxyRequested;
    public event Action? DisconnectExitNodeRequested;
    public event Action? CheckIpRequested;
    public event Action? UpdateRequested;
    public event Action? RestartRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _routedIcon = CreateBadgeIcon(Color.FromArgb(255, 97, 232, 186));  // Mint: active routing / exit-node / proxy
        _relayIcon = CreateBadgeIcon(Color.FromArgb(255, 255, 209, 102));  // Amber: relay server running
        _directIcon = CreateBadgeIcon(Color.FromArgb(255, 132, 148, 171)); // Slate: direct output

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem($"Открыть Tailscale Switcher (v{AppVersion.Informational})");
        _routeItem = new ToolStripMenuItem("Маршрутизация: выключена");
        _phoneItem = new ToolStripMenuItem("Телефон (Happ): выключен");
        _proxyItem = new ToolStripMenuItem("Прокси Xeon: выключен");
        _exitNodeOffItem = new ToolStripMenuItem("Отключить exit-node");
        var ipItem = new ToolStripMenuItem("Проверить внешний IP");
        _updateItem = new ToolStripMenuItem("Проверить обновления");
        var restart = new ToolStripMenuItem("Перезапустить");
        var exit = new ToolStripMenuItem("Закрыть приложение");

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_routeItem);
        menu.Items.Add(_phoneItem);
        menu.Items.Add(_proxyItem);
        menu.Items.Add(_exitNodeOffItem);
        menu.Items.Add(ipItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updateItem);
        menu.Items.Add(restart);
        menu.Items.Add(exit);

        open.Click += (_, _) => OpenRequested?.Invoke();
        _routeItem.Click += (_, _) => ToggleRouteRequested?.Invoke();
        _phoneItem.Click += (_, _) => TogglePhoneRequested?.Invoke();
        _proxyItem.Click += (_, _) => ToggleProxyRequested?.Invoke();
        _exitNodeOffItem.Click += (_, _) => DisconnectExitNodeRequested?.Invoke();
        ipItem.Click += (_, _) => CheckIpRequested?.Invoke();
        _updateItem.Click += (_, _) => UpdateRequested?.Invoke();
        restart.Click += (_, _) => RestartRequested?.Invoke();
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _icon = new NotifyIcon
        {
            Icon = _directIcon,
            Text = $"Tailscale Switcher v{AppVersion.Informational}",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void UpdateState(
        TrayVisualState state,
        string summaryText,
        bool routeRunning,
        bool phoneRunning,
        bool proxyEnabled,
        bool hasExitNode,
        bool busy)
    {
        if (state != _lastState)
        {
            _icon.Icon = state switch
            {
                TrayVisualState.Routed => _routedIcon,
                TrayVisualState.RelayOnly => _relayIcon,
                _ => _directIcon
            };
            _lastState = state;
        }

        var tip = $"Tailscale Switcher — {summaryText}";
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];

        _routeItem.Text = routeRunning ? "Выключить маршрутизацию" : "Включить маршрутизацию";
        _routeItem.Enabled = !busy;

        _phoneItem.Text = phoneRunning ? "Выключить мост телефона" : "Включить мост через телефон";
        _phoneItem.Enabled = !busy;

        _proxyItem.Text = proxyEnabled ? "Выключить прокси Xeon" : "Включить прокси Xeon";
        _proxyItem.Enabled = !busy;

        _exitNodeOffItem.Enabled = !busy && hasExitNode;
    }

    public void SetUpdateState(string label, bool enabled)
    {
        _updateItem.Text = label;
        _updateItem.Enabled = enabled;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    private static Icon CreateBadgeIcon(Color accentColor)
    {
        const int sz = 32;
        using var bmp = new Bitmap(sz, sz);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(255, 16, 20, 29));
        using var bgPath = CreateRoundedRect(1, 1, sz - 2, sz - 2, 7);
        g.FillPath(bg, bgPath);

        using var linePen = new Pen(accentColor, 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var c1 = sz * 0.28f;
        var c2 = sz * 0.50f;
        var c3 = sz * 0.72f;
        var r = sz * 0.09f;

        g.DrawLine(linePen, c1, c3, c2, c2);
        g.DrawLine(linePen, c2, c2, c3, c1);

        using var activeBrush = new SolidBrush(accentColor);
        using var dimBrush = new SolidBrush(Color.FromArgb(255, 84, 102, 128));
        (float x, float y, bool active)[] nodes =
        [
            (c1, c1, false), (c2, c1, false), (c3, c1, true),
            (c1, c2, false), (c2, c2, true),  (c3, c2, false),
            (c1, c3, true),  (c2, c3, false), (c3, c3, false)
        ];
        foreach (var (x, y, active) in nodes)
            g.FillEllipse(active ? activeBrush : dimBrush, x - r, y - r, r * 2, r * 2);

        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _directIcon.Dispose();
        _routedIcon.Dispose();
        _relayIcon.Dispose();
    }
}
