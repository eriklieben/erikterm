using System.Globalization;
using System.Text;

namespace ErikTerm.App.Tmux;

/// <summary>What a teammate's command line says about it (Claude Code's --agent-id / --agent-name / --team-name / --agent-color).</summary>
internal sealed record TeammateArgs(string? AgentId, string Name, string? Team, string? Color);

/// <summary>
/// Recognising the panes Claude Code opens for agent-team teammates through the tmux emulation. Claude Code
/// (2.1, teammateMode tmux, inside tmux) does, per teammate:
/// <code>
/// split-window -d -t %LEAD -h -l 70% -P -F #{pane_id} -- cat        (later ones: -t a teammate, -v or -h)
/// set-option -p -t %NEW pane-border-style fg=blue                    (also window-style, pane-active-border-style)
/// select-pane -t %NEW -T alpha                                      (set-option -p … pane-border-format, remain-on-exit failed)
/// select-layout -t @WIN main-vertical ; resize-pane -t %LEAD -x 30%  (from the third pane on)
/// set-option -p -t %NEW remain-on-exit failed
/// respawn-pane -k -t %NEW -- "cd '/dir' &amp;&amp; env CLAUDECODE=1 … '/path/claude' --agent-id alpha@team --agent-name alpha --team-name team --agent-color blue …"
/// kill-pane -t %NEW                                                  (when the teammate is shut down)
/// </code>
/// The shim runs as Claude Code's child, so it sees Claude Code's environment (see <see cref="FromClaudeCode"/>). A split is a
/// teammate's when Claude Code asks for it and it runs the <c>cat</c> placeholder (or a teammate command line);
/// respawn-pane with a teammate command line confirms it (and also catches a teammate pane the split didn't flag).
/// </summary>
internal static class TmuxTeammates
{
    /// <summary>The program Claude Code puts in a teammate's pane until respawn-pane starts the teammate.</summary>
    public const string Placeholder = "cat";

    /// <summary>
    /// The tmux command comes from Claude Code: its process environment, which its tmux calls inherit, has
    /// <c>AI_AGENT=claude-code_&lt;version&gt;_…</c> and <c>CLAUDE_CODE_ENTRYPOINT</c> (2.1). <c>CLAUDECODE=1</c> is only
    /// set for the commands it runs for the model and for teammates, so it counts too, but isn't relied on.
    /// </summary>
    public static bool FromClaudeCode(Func<string, string?> env) =>
        env("AI_AGENT") is { } agent && agent.StartsWith("claude-code", StringComparison.Ordinal)
        || env("CLAUDE_CODE_ENTRYPOINT") is { Length: > 0 }
        || env("CLAUDECODE") == "1";

    /// <summary>Whether a split-window / new-window with these program arguments opens a teammate's pane.</summary>
    public static bool IsTeammateSplit(IReadOnlyList<string> args, Func<string, string?> env) =>
        FromClaudeCode(env) && (args is [Placeholder] || Parse(args) is not null);

    /// <summary>The teammate a command line starts (argv, or one shell command string as tmux takes it), or null when it isn't one.</summary>
    public static TeammateArgs? Parse(IReadOnlyList<string> argv)
    {
        var words = CommandWords(argv);
        string? id = null, name = null, team = null, color = null;
        for (int i = 0; i < words.Count; i++)
        {
            if (FlagValue(words, ref i, "--agent-id") is { } v1) id = v1;
            else if (FlagValue(words, ref i, "--agent-name") is { } v2) name = v2;
            else if (FlagValue(words, ref i, "--team-name") is { } v3) team = v3;
            else if (FlagValue(words, ref i, "--agent-color") is { } v4) color = v4;
        }
        name = Clean(name) ?? Clean(id is { } x && x.IndexOf('@') is > 0 and var at ? x[..at] : id);
        if (name is null) return null;
        return new TeammateArgs(Clean(id), name, Clean(team), Clean(color));
    }

    private static List<string> CommandWords(IReadOnlyList<string> argv)
    {
        var words = new List<string>();
        foreach (string a in argv) words.AddRange(a.Contains(' ') || a.Contains('\'') || a.Contains('"') ? ShellWords(a) : [a]);
        return words;
    }

    /// <summary>The value when word <paramref name="i"/> is <paramref name="flag"/> (the next word) or <c>flag=value</c>, else null.</summary>
    private static string? FlagValue(List<string> words, ref int i, string flag)
    {
        string w = words[i];
        if (w == flag) return i + 1 < words.Count ? words[++i] : null;
        return w.StartsWith(flag + "=", StringComparison.Ordinal) ? w[(flag.Length + 1)..] : null;
    }

    /// <summary>Names end up on screen: no control characters, at most 40 characters.</summary>
    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder(Math.Min(s.Length, 40));
        foreach (char c in s.Trim())
        {
            if (char.IsControl(c) || c is '\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069') continue;
            if (sb.Length == 40) break;
            sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>A POSIX shell command split into words: '…' and "…" quoting and backslash escapes (no expansion).</summary>
    public static List<string> ShellWords(string command)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        bool inWord = false;
        int i = 0;
        while (i < command.Length)
        {
            char c = command[i];
            if (c == '\'')
            {
                i = AppendSingleQuoted(command, i, cur);
                inWord = true;
            }
            else if (c == '"')
            {
                i = AppendDoubleQuoted(command, i, cur);
                inWord = true;
            }
            else if (c == '\\' && i + 1 < command.Length)
            {
                cur.Append(command[++i]);
                inWord = true;
            }
            else if (char.IsWhiteSpace(c) || c is ';' or '&' or '|')
            {
                if (inWord) words.Add(cur.ToString());
                cur.Clear();
                inWord = false;
            }
            else
            {
                cur.Append(c);
                inWord = true;
            }
            i++;
        }
        if (inWord) words.Add(cur.ToString());
        return words;
    }

    /// <summary>Appends the quoted text after the ' at <paramref name="open"/>; returns the closing quote's index (or the end).</summary>
    private static int AppendSingleQuoted(string command, int open, StringBuilder cur)
    {
        int end = command.IndexOf('\'', open + 1);
        if (end < 0) end = command.Length;
        cur.Append(command, open + 1, end - open - 1);
        return end;
    }

    /// <summary>Appends the quoted text after the " at <paramref name="i"/>; returns the closing quote's index (or the end).</summary>
    private static int AppendDoubleQuoted(string command, int i, StringBuilder cur)
    {
        i++;
        while (i < command.Length && command[i] != '"')
        {
            if (command[i] == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\' or '$' or '`') i++;
            cur.Append(command[i]);
            i++;
        }
        return i;
    }

    /// <summary>
    /// The color in a tmux style Claude Code sets on a teammate's pane (<c>pane-border-style fg=blue</c>,
    /// <c>window-style bg=default,fg=colour208</c>): the fg value, or null.
    /// </summary>
    public static string? StyleForeground(string style)
    {
        foreach (string part in style.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith("fg=", StringComparison.Ordinal) && part.Length > 3 && part[3..] is not ("default" or "terminal")) return part[3..];
        return null;
    }

    /// <summary>
    /// A teammate color as RGB: Claude Code's names (red, blue, green, yellow, purple, orange, pink, cyan), tmux's
    /// (magenta, colour208, brightred, #rrggbb). Basic names take the scheme's ANSI colors (<paramref name="ansi"/>,
    /// 16 entries) so they match the theme; null for anything else.
    /// </summary>
    public static uint? ColorValue(string? spec, ReadOnlySpan<uint> ansi)
    {
        if (spec is null) return null;
        string s = spec.Trim().ToLowerInvariant();
        bool bright = s.StartsWith("bright", StringComparison.Ordinal);
        if (bright) s = s[6..];
        int basic = BasicColorIndex(s);
        if (basic >= 0)
        {
            int index = basic + (bright ? 8 : 0);
            return ansi.Length >= 16 ? ansi[index] : null;
        }
        return NamedColor(s, ansi);
    }

    private static int BasicColorIndex(string s) => s switch
    {
        "black" => 0, "red" => 1, "green" => 2, "yellow" => 3, "blue" => 4, "magenta" or "purple" => 5, "cyan" => 6, "white" => 7,
        _ => -1,
    };

    private static uint? NamedColor(string s, ReadOnlySpan<uint> ansi)
    {
        if (s == "orange") return Xterm256(208, ansi);
        if (s == "pink") return Xterm256(205, ansi);
        int digits = -1;
        if (s.StartsWith("colour", StringComparison.Ordinal)) digits = 6;
        else if (s.StartsWith("color", StringComparison.Ordinal)) digits = 5;
        if (digits > 0 && int.TryParse(s.AsSpan(digits), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= 255)
            return Xterm256(n, ansi);
        if (s.Length == 7 && s[0] == '#' && uint.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return rgb;
        return null;
    }

    private static uint? Xterm256(int n, ReadOnlySpan<uint> ansi)
    {
        if (n < 16) return ansi.Length >= 16 ? ansi[n] : null;
        if (n >= 232)
        {
            uint g = (uint)(8 + (n - 232) * 10);
            return g << 16 | g << 8 | g;
        }
        n -= 16;
        static uint Level(int v) => v == 0 ? 0u : (uint)(55 + v * 40);
        return Level(n / 36) << 16 | Level(n / 6 % 6) << 8 | Level(n % 6);
    }
}
