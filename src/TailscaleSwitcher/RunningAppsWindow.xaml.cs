using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TailscaleSwitcher.Services;

namespace TailscaleSwitcher;

public partial class RunningAppsWindow : Window
{
    private List<(string ProcessExe, string WindowTitle)> _allApps = [];

    public string? SelectedExe { get; private set; }

    public RunningAppsWindow()
    {
        InitializeComponent();
        LoadApps();
    }

    private void LoadApps()
    {
        _allApps = TailscaleService.GetRunningWindowApps();
        RenderFilter();
    }

    private void RenderFilter()
    {
        var query = SearchBox.Text.Trim();
        AppsList.Items.Clear();

        foreach (var (exe, title) in _allApps)
        {
            if (query.Length > 0 &&
                !exe.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppsList.Items.Add(new AppEntry(exe, title));
        }

        if (AppsList.Items.Count > 0)
            AppsList.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RenderFilter();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadApps();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void OnAddClick(object sender, RoutedEventArgs e) => AcceptSelection();

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void AcceptSelection()
    {
        if (AppsList.SelectedItem is AppEntry entry)
        {
            SelectedExe = entry.Exe;
            DialogResult = true;
        }
    }

    private sealed record AppEntry(string Exe, string Title)
    {
        public override string ToString() => $"{Exe}   —   {Title}";
    }
}
