using System.Text;
using TailscaleSwitcher.Routing;

namespace TailscaleSwitcher.Routing;

public sealed record ParsedProfileImport(
    string Name,
    string Node,
    int Port,
    string Username,
    string Password,
    bool AllTraffic);

/// <summary>
/// Разбирает команду подключения, скопированную из `relay init` / вкладки «Этот ПК (Relay)»,
/// чтобы профиль на клиентском ПК заполнялся одной вставкой.
/// </summary>
public static class RouteProfileCommandParser
{
    public static bool TryParse(string? raw, out ParsedProfileImport? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var tokens = SplitCommandLine(raw);
        if (tokens.Count == 0) return false;

        // Убираем необязательный префикс: TailscaleSwitcher(.exe) route profile add <name>
        var idx = 0;
        if (idx < tokens.Count && tokens[idx].Contains("TailscaleSwitcher", StringComparison.OrdinalIgnoreCase))
            idx++;
        if (idx < tokens.Count && tokens[idx].Equals("route", StringComparison.OrdinalIgnoreCase))
            idx++;
        if (idx < tokens.Count && tokens[idx].Equals("profile", StringComparison.OrdinalIgnoreCase))
            idx++;
        if (idx < tokens.Count && tokens[idx].Equals("add", StringComparison.OrdinalIgnoreCase))
            idx++;

        var parsed = CliArgs.Parse(tokens.Skip(idx));
        if (parsed.Error != null) return false;

        var node = parsed.Get("--node")?.Trim();
        if (string.IsNullOrWhiteSpace(node)) return false;

        var name = parsed.Positional.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "club";

        var port = RelaySettings.DefaultPort;
        if (parsed.Get("--port") is { } portText)
        {
            if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
                return false;
        }

        result = new ParsedProfileImport(
            Name: name,
            Node: node,
            Port: port,
            Username: parsed.Get("--user") ?? "club",
            Password: parsed.Get("--pass") ?? "",
            AllTraffic: parsed.Has("--all"));
        return true;
    }

    private static List<string> SplitCommandLine(string input)
    {
        var list = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        char quoteChar = '\0';

        foreach (var ch in input.Trim())
        {
            if (inQuotes)
            {
                if (ch == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    list.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            list.Add(current.ToString());

        return list;
    }
}
