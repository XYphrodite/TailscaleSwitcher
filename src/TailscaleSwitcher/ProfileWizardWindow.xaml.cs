using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TailscaleSwitcher.Proxy;
using TailscaleSwitcher.Routing;
using TailscaleSwitcher.Services;

namespace TailscaleSwitcher;

public partial class ProfileWizardWindow : Window
{
    private readonly List<TailnetPeerInfo> _peers;
    private readonly HashSet<string> _existingNames;
    private bool _suppressImport;

    public RouteProfile? CreatedProfile { get; private set; }

    public ProfileWizardWindow(IEnumerable<string> existingProfileNames, IReadOnlyList<TailnetPeerInfo>? peers = null)
    {
        _existingNames = new HashSet<string>(existingProfileNames, StringComparer.OrdinalIgnoreCase);
        _peers = (peers ?? []).ToList();

        InitializeComponent();

        if (_peers.Count == 0)
        {
            PeerCombo.Items.Add("Узлы Tailnet не найдены — введите IP вручную");
            PeerCombo.SelectedIndex = 0;
            PeerCombo.IsEnabled = false;
        }
        else
        {
            PeerCombo.Items.Add("— Выбрать из списка Tailnet —");
            foreach (var p in _peers)
            {
                var status = p.Online ? "online" : "offline";
                var os = string.IsNullOrWhiteSpace(p.Os) ? "" : $", {p.Os}";
                PeerCombo.Items.Add($"{p.HostName}  ({p.TailscaleIP}{os}, {status})");
            }
            PeerCombo.SelectedIndex = 0;
        }

        // Подбираем свободное имя по умолчанию
        if (_existingNames.Contains("club"))
        {
            var i = 2;
            while (_existingNames.Contains($"club-{i}")) i++;
            NameBox.Text = $"club-{i}";
        }
    }

    private void OnPeerSelected(object sender, SelectionChangedEventArgs e)
    {
        var idx = PeerCombo.SelectedIndex - 1;
        if (idx >= 0 && idx < _peers.Count)
        {
            var peer = _peers[idx];
            NodeBox.Text = peer.TailscaleIP;
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "club")
            {
                var cleanHost = new string(peer.HostName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
                if (!string.IsNullOrWhiteSpace(cleanHost) && !_existingNames.Contains(cleanHost))
                    NameBox.Text = cleanHost;
            }
        }
    }

    private void OnPasteImportClick(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
            ImportBox.Text = Clipboard.GetText().Trim();
    }

    private void OnImportTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressImport) return;
        if (RouteProfileCommandParser.TryParse(ImportBox.Text, out var parsed) && parsed != null)
        {
            _suppressImport = true;
            NameBox.Text = MakeUniqueName(parsed.Name);
            NodeBox.Text = parsed.Node;
            PortBox.Text = parsed.Port.ToString();
            UserBox.Text = parsed.Username;
            PassBox.Text = parsed.Password;
            ModeAllRadio.IsChecked = parsed.AllTraffic;
            ModeSelectiveRadio.IsChecked = !parsed.AllTraffic;
            _suppressImport = false;

            ImportHint.Text = $"Распознано: {parsed.Node}:{parsed.Port} (логин {parsed.Username}).";
            ImportHint.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0xE8, 0xBA));
            ImportHint.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrWhiteSpace(ImportBox.Text))
        {
            ImportHint.Text = "Ожидается команда вида: TailscaleSwitcher route profile add ... --node <ip> --port <порт> --user <логин> --pass <пароль>";
            ImportHint.Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x9F, 0xB4));
            ImportHint.Visibility = Visibility.Visible;
        }
        else
        {
            ImportHint.Visibility = Visibility.Collapsed;
        }
    }

    private string MakeUniqueName(string baseName)
    {
        if (!_existingNames.Contains(baseName)) return baseName;
        var i = 2;
        while (_existingNames.Contains($"{baseName}-{i}")) i++;
        return $"{baseName}-{i}";
    }

    private async void OnProbeClick(object sender, RoutedEventArgs e)
    {
        var node = NodeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(node) || !int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ProbeLabel.Text = "Укажи корректный IP и порт (1-65535).";
            ProbeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66));
            ProbeLabel.Visibility = Visibility.Visible;
            return;
        }

        ProbeButton.IsEnabled = false;
        ProbeLabel.Text = $"Проверяю {node}:{port}…";
        ProbeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x9F, 0xB4));
        ProbeLabel.Visibility = Visibility.Visible;

        var ok = await XeonProxyService.ProbeTcpAsync($"{node}:{port}", 2500);
        ProbeButton.IsEnabled = true;

        if (ok)
        {
            ProbeLabel.Text = $"Узел {node}:{port} доступен и отвечает.";
            ProbeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0xE8, 0xBA));
        }
        else
        {
            ProbeLabel.Text = $"Нет ответа от {node}:{port}. Убедись, что на выходном ПК запущен relay и открыт firewall.";
            ProbeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var node = NodeBox.Text.Trim();
        var portText = PortBox.Text.Trim();
        var user = UserBox.Text.Trim();
        var pass = PassBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorLabel.Text = "Укажи название профиля.";
            return;
        }
        if (_existingNames.Contains(name))
        {
            ErrorLabel.Text = $"Профиль «{name}» уже существует.";
            return;
        }
        if (string.IsNullOrWhiteSpace(node))
        {
            ErrorLabel.Text = "Укажи Tailscale IP выходного ПК.";
            return;
        }
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            ErrorLabel.Text = "Порт должен быть числом от 1 до 65535.";
            return;
        }

        CreatedProfile = new RouteProfile
        {
            Name = name,
            Node = node,
            Port = port,
            Username = user,
            Password = pass,
            AllTraffic = ModeAllRadio.IsChecked == true
        };
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
