namespace ErikTerm.Ui.VimMode;

/// <summary>
/// Keys in Vim's notation: a printable character is itself ("j", "$", "%"), everything else is a name in angle
/// brackets ("&lt;Esc&gt;", "&lt;CR&gt;", "&lt;BS&gt;", "&lt;Tab&gt;", "&lt;S-Tab&gt;", "&lt;C-u&gt;", "&lt;Up&gt;"). The app turns key and text
/// events into these; tests write whole sequences ("5j&lt;C-v&gt;jjy").
/// </summary>
public static class VimKeys
{
    public const string Esc = "<Esc>", Enter = "<CR>", Backspace = "<BS>", Tab = "<Tab>", ShiftTab = "<S-Tab>";
    public const string Up = "<Up>", Down = "<Down>", Left = "<Left>", Right = "<Right>";
    public const string Home = "<Home>", End = "<End>", PageUp = "<PageUp>", PageDown = "<PageDown>", Delete = "<Del>";

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Esc", "CR", "Enter", "Return", "BS", "Tab", "S-Tab", "Up", "Down", "Left", "Right", "Home", "End",
        "PageUp", "PageDown", "Del", "lt", "Space", "Bar", "Bslash",
    };

    /// <summary>The Ctrl+letter key (&lt;C-u&gt;); <paramref name="c"/> is lowercased.</summary>
    public static string Ctrl(char c) => $"<C-{char.ToLowerInvariant(c)}>";

    /// <summary>True for "&lt;C-x&gt;" style keys; <paramref name="c"/> is the letter.</summary>
    public static bool IsCtrl(string key, out char c)
    {
        c = '\0';
        if (key.Length != 5 || !key.StartsWith("<C-", StringComparison.Ordinal) || key[4] != '>') return false;
        c = key[3];
        return true;
    }

    /// <summary>A single printable character key (not a named key).</summary>
    public static bool IsChar(string key, out char c)
    {
        c = key.Length == 1 ? key[0] : '\0';
        return key.Length == 1;
    }

    /// <summary>
    /// Splits notation into keys: "&lt;C-w&gt;v" → ["&lt;C-w&gt;", "v"]. A '&lt;' that doesn't start a known name is itself,
    /// so "&lt;C-w&gt;&lt;" is Ctrl-w then '&lt;'. &lt;lt&gt; is a literal '&lt;', &lt;Space&gt; a space.
    /// </summary>
    public static List<string> Parse(string notation)
    {
        var keys = new List<string>();
        int i = 0;
        while (i < notation.Length)
        {
            keys.Add(ReadKey(notation, i, out int consumed));
            i += consumed;
        }
        return keys;
    }

    private static string ReadKey(string notation, int i, out int consumed)
    {
        char ch = notation[i];
        if (ch == '<')
        {
            int close = notation.IndexOf('>', i + 1);
            if (close > i + 1 && Normalize(notation[(i + 1)..close]) is { } named)
            {
                consumed = close - i + 1;
                return named;
            }
        }
        if (char.IsHighSurrogate(ch) && i + 1 < notation.Length)
        {
            consumed = 2;
            return notation.Substring(i, 2);
        }
        consumed = 1;
        return ch.ToString();
    }

    private static string? Normalize(string name)
    {
        if (name.Length == 3 && name[1] == '-' && (name[0] is 'C' or 'c') && char.IsAsciiLetter(name[2])) return Ctrl(name[2]);
        if (name is "C-[" or "c-[") return Esc;
        if (!Names.Contains(name)) return null;
        return name.ToLowerInvariant() switch
        {
            "esc" => Esc,
            "cr" or "enter" or "return" => Enter,
            "bs" => Backspace,
            "tab" => Tab,
            "s-tab" => ShiftTab,
            "up" => Up,
            "down" => Down,
            "left" => Left,
            "right" => Right,
            "home" => Home,
            "end" => End,
            "pageup" => PageUp,
            "pagedown" => PageDown,
            "del" => Delete,
            "lt" => "<",
            "space" => " ",
            "bar" => "|",
            "bslash" => "\\",
            _ => null,
        };
    }

    /// <summary>Keys as notation again (for the pending-keys display): ["5", "g"] → "5g".</summary>
    public static string Format(IEnumerable<string> keys) => string.Concat(keys.Select(k => k == " " ? "<Space>" : k));
}
