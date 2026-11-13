using System.Diagnostics;
using System.Security;

namespace TailscaleSwitcher.Routing;

public sealed record RunnerStatus(bool Running, int? Pid, DateTime? StartedUtc);

/// <summary>
/// Управляет фоновым процессом sing-box для роли (client / relay).
/// Процесс стартует через Планировщик заданий от SYSTEM: так он не умирает вместе с
/// родительской сессией (ssh, job-объект терминала), а relay может подниматься при загрузке.
/// </summary>
public sealed class SingBoxRunner
{
    private readonly string _role;

    public SingBoxRunner(string role) { _role = role; }

    public string ConfigPath => Path.Combine(RoutingPaths.RunDir, $"{_role}.config.json");
    public string LogPath => Path.Combine(RoutingPaths.RunDir, $"{_role}.log");
    public string TaskName => $"TailscaleSwitcher-{_role}";
    private string TaskXmlPath => Path.Combine(RoutingPaths.RunDir, $"{_role}.task.xml");

    public RunnerStatus GetStatus()
    {
        var pids = FindPids();
        if (pids.Count == 0) return new RunnerStatus(false, null, null);
        DateTime? started = null;
        try { using var p = Process.GetProcessById(pids[0]); started = p.StartTime.ToUniversalTime(); } catch { }
        return new RunnerStatus(true, pids[0], started);
    }

    /// <summary>Проверяет конфиг и запускает sing-box. Возвращает null при успехе, иначе текст ошибки.</summary>
    /// <param name="persistAtBoot">Запускать при загрузке Windows и перезапускать при падении.</param>
    public async Task<string?> StartAsync(string singBoxExe, string configJson, bool persistAtBoot = false)
    {
        if (GetStatus().Running) return $"Роль '{_role}' уже запущена. Сначала останови её.";

        Directory.CreateDirectory(RoutingPaths.RunDir);
        File.WriteAllText(ConfigPath, configJson);
        try { File.WriteAllText(LogPath, ""); } catch { }

        var (checkOk, checkOut) = RunToEnd(singBoxExe, $"check -c \"{ConfigPath}\"");
        if (!checkOk) return $"sing-box check: {checkOut.Trim()}";

        File.WriteAllText(TaskXmlPath, BuildTaskXml(singBoxExe, ConfigPath, persistAtBoot), System.Text.Encoding.Unicode);
        var (created, createOut) = RunToEnd("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{TaskXmlPath}\" /F");
        if (!created) return $"schtasks /Create (нужен запуск от Администратора): {createOut.Trim()}";
        var (ran, runOut) = RunToEnd("schtasks", $"/Run /TN \"{TaskName}\"");
        if (!ran) return $"schtasks /Run: {runOut.Trim()}";

        // даём время упасть, если порт занят / нет прав на TUN
        await Task.Delay(3000);
        if (!GetStatus().Running)
        {
            DeleteTask();
            return $"sing-box не запустился. Лог:\n{TailLog()}";
        }
        return null;
    }

    /// <summary>Останавливает процесс. deleteTask=false оставляет задание автозапуска.</summary>
    public bool Stop(bool deleteTask = true)
    {
        var pids = FindPids();
        RunToEnd("schtasks", $"/End /TN \"{TaskName}\"");
        foreach (var pid in pids)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
        }
        if (deleteTask) DeleteTask();
        return pids.Count > 0;
    }

    public bool DeleteTask() => RunToEnd("schtasks", $"/Delete /TN \"{TaskName}\" /F").ok;

    public string TailLog(int lines = 15)
    {
        try
        {
            if (!File.Exists(LogPath)) return "";
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd().Split('\n');
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch { return ""; }
    }

    public static string BuildTaskXml(string exe, string configPath, bool persistAtBoot)
    {
        var trigger = persistAtBoot
            ? "<BootTrigger><Enabled>true</Enabled><Delay>PT30S</Delay></BootTrigger>"
            : "";
        var restart = persistAtBoot
            ? "<RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>"
            : "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>{trigger}</Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                {restart}
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>run -c "{SecurityElement.Escape(configPath)}"</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>PID sing-box, запущенных с конфигом этой роли (по командной строке).</summary>
    private List<int> FindPids()
    {
        var needle = ConfigPath.Replace("'", "''");
        var script = "Get-CimInstance Win32_Process -Filter \"Name='sing-box.exe'\" | " +
                     $"Where-Object {{ $_.CommandLine -like '*{needle}*' }} | ForEach-Object {{ $_.ProcessId }}";
        var (_, output) = RunToEnd("powershell", $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"");
        var pids = new List<int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(line, out var pid)) pids.Add(pid);
        return pids;
    }

    private static (bool ok, string output) RunToEnd(string exe, string args)
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
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return (false, "timeout"); }
            return (p.ExitCode == 0, o.Result + e.Result);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
