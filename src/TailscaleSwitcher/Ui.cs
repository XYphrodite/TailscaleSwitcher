using System.Diagnostics;

namespace TailscaleSwitcher;

/// <summary>Мелкие помощники для консольного меню: заголовки, статусы, выбор из списка.</summary>
internal static class Ui
{
    public const int Width = 62;

    public static void Clear()
    {
        try { if (!Console.IsOutputRedirected) Console.Clear(); } catch { }
    }

    public static void Title(string text)
    {
        Console.WriteLine(new string('═', Width));
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  " + text);
        Console.ResetColor();
        Console.WriteLine(new string('═', Width));
    }

    public static void Rule() => Console.WriteLine(new string('─', Width));

    /// <summary>Строка статуса: «  метка:  ● значение» с цветом.</summary>
    public static void Status(string label, string value, ConsoleColor color, bool dot = true)
    {
        Console.Write($"  {label,-19}");
        Console.ForegroundColor = color;
        Console.WriteLine((dot ? (color == ConsoleColor.DarkGray ? "○ " : "● ") : "") + value);
        Console.ResetColor();
    }

    public static void Item(string key, string text, string? hint = null)
    {
        Console.Write($"  {key,3}) {text}");
        if (hint != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  " + hint);
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    public static void Section(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  ── {text}");
        Console.ResetColor();
    }

    public static void Ok(string text) => Write(text, ConsoleColor.Green);
    public static void Warn(string text) => Write(text, ConsoleColor.Yellow);
    public static void Err(string text) => Write(text, ConsoleColor.Red);
    public static void Dim(string text) => Write(text, ConsoleColor.DarkGray);

    private static void Write(string text, ConsoleColor c)
    {
        Console.ForegroundColor = c;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static string Prompt(string text = "Выбери пункт")
    {
        Console.Write($"  {text} > ");
        return Console.ReadLine()?.Trim() ?? "";
    }

    public static string Ask(string question, string? defaultValue = null)
    {
        Console.Write(defaultValue == null ? $"  {question}: " : $"  {question} [{defaultValue}]: ");
        var v = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(v) ? defaultValue ?? "" : v;
    }

    public static bool Confirm(string question, bool defaultYes = true)
    {
        Console.Write($"  {question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        var v = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v)) return defaultYes;
        return v is "y" or "yes" or "д" or "да";
    }

    public static void Pause()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  Нажми Enter, чтобы продолжить...");
        Console.ResetColor();
        Console.ReadLine();
    }

    /// <summary>Нумерованный выбор из списка. Возвращает индекс или null (пусто/0/не число).</summary>
    public static int? Pick(string title, IReadOnlyList<string> items, string cancelHint = "0 — отмена")
    {
        if (items.Count == 0) return null;
        Console.WriteLine($"  {title}");
        for (var i = 0; i < items.Count; i++)
            Console.WriteLine($"  {i + 1,3}) {items[i]}");
        var s = Prompt($"Номер ({cancelHint})");
        return int.TryParse(s, out var n) && n >= 1 && n <= items.Count ? n - 1 : null;
    }

    public static bool RelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; } // пользователь отказал в UAC
    }
}
