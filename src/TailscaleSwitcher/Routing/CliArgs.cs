namespace TailscaleSwitcher.Routing;

/// <summary>Простой разбор: позиционные аргументы + --флаги (со значением или без).</summary>
public sealed class CliArgs
{
    private static readonly HashSet<string> BoolFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--force", "-f", "--no-remote-dns", "--help", "-h", "--persist"
    };

    public List<string> Positional { get; } = [];
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; private set; }

    public static CliArgs Parse(IEnumerable<string> args)
    {
        var r = new CliArgs();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith('-') || a.Length == 1 || a.Length > 1 && char.IsDigit(a[1]))
            {
                r.Positional.Add(a);
                continue;
            }
            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                r.Options[a[..eq]] = a[(eq + 1)..];
                continue;
            }
            if (BoolFlags.Contains(a))
            {
                r.Flags.Add(a);
                continue;
            }
            if (i + 1 >= list.Count)
            {
                r.Error = $"Параметр {a} требует значение.";
                return r;
            }
            r.Options[a] = list[++i];
        }
        return r;
    }

    public string? Get(string name) => Options.TryGetValue(name, out var v) ? v : null;
    public bool Has(params string[] flags) => flags.Any(Flags.Contains);
}
