namespace ErikTerm.Ui.VimMode;

/// <summary>
/// A parsed ex command line. <see cref="Name"/> is canonical ("split", "theme", "goto", "action", …) whatever alias was
/// typed; <see cref="Arg"/> is the rest of the line (for "action": the action as <c>Name(arg)</c>); <see cref="Bang"/>
/// is a ! after the name (<c>:w! file</c>); <see cref="Number"/> is the line of <c>:{n}</c> or a count (<c>:tabn 3</c>).
/// </summary>
public sealed record ExCommand(string Name, string Arg = "", bool Bang = false, int? Number = null);

/// <summary>One built-in ex command: its canonical name, the aliases vim users type, what it does, and its argument.</summary>
public sealed record ExSpec(string Name, string[] Aliases, string Help, ExArg Arg = ExArg.None);

public enum ExArg { None, Optional, Required, Number }

/// <summary>Parses ex command lines and completes them (built-ins, their aliases, erikterm action names and arguments).</summary>
public static class Ex
{
    public static readonly IReadOnlyList<ExSpec> Builtins =
    [
        new("split", ["sp", "split", "new"], "Split the pane down", ExArg.Optional),
        new("vsplit", ["vs", "vsp", "vsplit", "vnew", "vne"], "Split the pane right", ExArg.Optional),
        new("quit", ["q", "quit", "clo", "close", "x", "xit", "wq"], "Close the pane"),
        new("only", ["on", "only"], "Close the other panes in this tab (! also busy ones)"),
        new("tabnew", ["tabnew", "tabe", "tabedit"], "New tab"),
        new("tabnext", ["tabn", "tabnext"], "Next tab (or tab N)", ExArg.Number),
        new("tabprevious", ["tabp", "tabprevious", "tabN", "tabNext"], "Previous tab", ExArg.Number),
        new("tabclose", ["tabc", "tabclose"], "Close the tab"),
        new("theme", ["theme", "colo", "colorscheme"], "Switch color scheme (previews while you type)", ExArg.Required),
        new("set", ["se", "set"], "Override a setting for this session: :set font_size=13", ExArg.Required),
        new("nohlsearch", ["noh", "nohlsearch"], "Clear search highlights"),
        new("write", ["w", "write", "sav", "saveas"], "Write the scrollback to a file (! overwrites)", ExArg.Required),
        new("send", ["send"], "Send the selection or last yank to a pane or agent (bracketed paste)", ExArg.Required),
        new("layout", ["layout"], "Arrange the panes: even-horizontal, even-vertical, main-vertical, main-horizontal, tiled", ExArg.Required),
        new("help", ["h", "help"], "Vim mode cheat sheet"),
    ];

    private static readonly Dictionary<string, ExSpec> ByAlias = BuildAliases();

    private static Dictionary<string, ExSpec> BuildAliases()
    {
        var map = new Dictionary<string, ExSpec>(StringComparer.Ordinal);
        foreach (var spec in Builtins)
            foreach (var alias in spec.Aliases) map[alias] = spec;
        return map;
    }

    public static ExSpec? Find(string name) => ByAlias.GetValueOrDefault(name);

    /// <summary>
    /// Parses a command line (without the ':'). <paramref name="actions"/> are erikterm action names, which work as
    /// commands too (<c>:SplitHorizontal</c>, <c>:ActivatePaneDirection Left</c>, <c>:AdjustPaneSize(Left, 5)</c>).
    /// </summary>
    public static ExCommand? Parse(string line, IEnumerable<string> actions, out string? error)
    {
        error = null;
        string s = line.Trim();
        while (s.StartsWith(':')) s = s[1..].TrimStart();
        if (s.Length == 0) return null;

        // :{n} — a line of the scrollback, counted from the top.
        if (s.All(char.IsAsciiDigit)) return ParseLineNumber(s, out error);
        if (s == "$") return new ExCommand("goto", Number: int.MaxValue);

        int i = 0;
        while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
        if (i == 0)
        {
            error = $"Not an editor command: {s}";
            return null;
        }
        string name = s[..i];
        bool bang = i < s.Length && s[i] == '!';
        string rest = s[(i + (bang ? 1 : 0))..].Trim();

        if (Find(name) is { } spec) return ParseBuiltin(spec, rest, bang, out error);

        // An erikterm action: Name, Name(arg) or Name arg.
        var match = actions.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return ActionCommand(match, rest, bang);
        error = $"Not an editor command: {s}";
        return null;
    }

    private static ExCommand? ParseLineNumber(string digits, out string? error)
    {
        error = null;
        if (int.TryParse(digits, out int n)) return new ExCommand("goto", Number: n);
        error = "Number too large";
        return null;
    }

    private static ExCommand? ParseBuiltin(ExSpec spec, string rest, bool bang, out string? error)
    {
        error = null;
        switch (spec.Arg)
        {
            case ExArg.None when rest.Length > 0:
                error = $"Trailing characters: {rest}";
                return null;
            case ExArg.Required when rest.Length == 0:
                error = spec.Name == "write" ? "No file name" : "Argument required";
                return null;
            case ExArg.Number when rest.Length > 0:
                if (!int.TryParse(rest, out int count) || count < 0)
                {
                    error = $"Invalid argument: {rest}";
                    return null;
                }
                return new ExCommand(spec.Name, rest, bang, count);
        }
        if (spec.Name == "set" && !rest.Contains('='))
        {
            // :set key value, as well as :set key=value
            int space = rest.IndexOfAny([' ', '\t']);
            if (space < 0)
            {
                error = $"Expected KEY=VALUE: {rest}";
                return null;
            }
            rest = rest[..space].Trim() + "=" + rest[(space + 1)..].Trim();
        }
        return new ExCommand(spec.Name, rest, bang);
    }

    private static ExCommand ActionCommand(string action, string rest, bool bang)
    {
        string arg = rest;
        if (arg.StartsWith('(') && arg.EndsWith(')')) arg = arg[1..^1].Trim();
        return new ExCommand("action", arg.Length > 0 ? $"{action}({arg})" : action, bang);
    }

    /// <summary>
    /// Completions for a partly typed command line, best first (fuzzy, like the palette): command names while the
    /// first word is typed, then that command's arguments (<paramref name="arguments"/>: canonical name → candidates).
    /// Each completion is a whole command line.
    /// </summary>
    public static List<string> Complete(string input, IEnumerable<string> actions, Func<string, IEnumerable<string>>? arguments, int max = 50)
    {
        string s = input.TrimStart();
        while (s.StartsWith(':')) s = s[1..];
        int space = s.IndexOf(' ');
        var results = space < 0 ? CompleteName(s, actions) : CompleteArgument(s, space, arguments);
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Order).Take(max).Select(r => r.Text).ToList();
    }

    private readonly record struct Completion(string Text, int Score, int Order);

    private static List<Completion> CompleteName(string typed, IEnumerable<string> actions)
    {
        var results = new List<Completion>();
        int order = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Consider(string name)
        {
            order++;
            if (!seen.Add(name)) return;
            if (typed.Length == 0) { results.Add(new Completion(name, 0, order)); return; }
            if (Fuzzy.Match(typed, name, out int score)) results.Add(new Completion(name, score + (name.StartsWith(typed, StringComparison.Ordinal) ? 1000 : 0), order));
        }
        foreach (var spec in Builtins) Consider(spec.Name);
        foreach (var name in NamesWithAliasStartingWith(typed)) Consider(name);
        foreach (var a in actions) Consider(a);
        return results;
    }

    /// <summary>A builtin's canonical name, once for every alias that starts with <paramref name="prefix"/>; nothing for an empty prefix.</summary>
    private static IEnumerable<string> NamesWithAliasStartingWith(string prefix)
    {
        foreach (var spec in Builtins)
            foreach (var alias in spec.Aliases)
                if (alias.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > 0) yield return spec.Name;
    }

    private static List<Completion> CompleteArgument(string s, int space, Func<string, IEnumerable<string>>? arguments)
    {
        var results = new List<Completion>();
        string verb = s[..space].TrimEnd('!');
        string arg = s[(space + 1)..].TrimStart();
        string canonical = Find(verb)?.Name ?? verb;
        if (arguments?.Invoke(canonical) is not { } candidates) return results;
        int order = 0;
        string head = s[..space] + " ";
        foreach (var c in candidates)
        {
            order++;
            if (arg.Length == 0) results.Add(new Completion(head + c, 0, order));
            else if (Fuzzy.Match(arg, c, out int score))
                results.Add(new Completion(head + c, score + (c.StartsWith(arg, StringComparison.OrdinalIgnoreCase) ? 1000 : 0), order));
        }
        return results;
    }
}
