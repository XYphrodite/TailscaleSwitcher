using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using TailscaleSwitcher.Services;
using TailscaleSwitcher.Update;

namespace TailscaleSwitcher;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        if (CliRunner.ShouldHandleAsCli(args))
        {
            var code = Task.Run(() => CliRunner.RunAsync(args)).GetAwaiter().GetResult();
            Environment.ExitCode = code;
            Shutdown();
            return;
        }

        var tray = args.Contains("-Tray", StringComparer.OrdinalIgnoreCase);

        AppUpdater.CleanUpRetiredCopies();

        _mutex = new Mutex(false, @"Local\TailscaleSwitcher.UI");
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }

        if (!_ownsMutex)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(@"Local\TailscaleSwitcher.Show");
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show(
                    "Tailscale Switcher уже запущен. Нажми на значок в системном трее возле часов.",
                    "Tailscale Switcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TailscaleSwitcher.Show");

        var window = new MainWindow(() => _showEvent, startHidden: tray);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        if (!tray) window.Show();
    }

    public void RestartApplication(bool asAdmin, bool startInTray)
    {
        var exe = Environment.ProcessPath;
        ReleaseSingleInstanceHandles();

        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            };
            if (asAdmin)
                startInfo.Verb = "runas";
            if (startInTray)
                startInfo.Arguments = "-Tray";

            try
            {
                Process.Start(startInfo);
            }
            catch
            {
                // Пользователь мог отменить UAC — просто закрываемся или остаёмся?
                // Так как хэндлы уже освобождены, при отказе UAC просто завершаем текущий процесс.
            }
        }

        (MainWindow as global::TailscaleSwitcher.MainWindow)?.PrepareExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceHandles();
        base.OnExit(e);
    }

    private void ReleaseSingleInstanceHandles()
    {
        _showEvent?.Dispose();
        _showEvent = null;
        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
