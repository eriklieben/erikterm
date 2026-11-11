using System.Globalization;
using System.Text;

namespace ErikTerm.App.Tmux;

/// <summary>
/// <c>send-keys</c> key names → the bytes a terminal sends for them (xterm encoding, as tmux uses for
/// <c>xterm-256color</c> panes). Names are case-insensitive like tmux's: <c>Enter</c>, <c>Escape</c>, <c>Tab</c>,
/// <c>BTab</c>, <c>Space</c>, <c>BSpace</c>, arrows, <c>Home</c>/<c>End</c>, <c>PageUp</c>/<c>PPage</c>/<c>PgUp</c>,
/// <c>PageDown</c>/<c>NPage</c>/<c>PgDn</c>, <c>IC</c>/<c>Insert</c>, <c>DC</c>/<c>Delete</c>, <c>F1</c>–<c>F12</c>,
/// with <c>C-</c>, <c>M-</c>, <c>S-</c> prefixes (also <c>^x</c>). A word that isn't a key name is typed as text.
/// </summary>
internal static class TmuxKeys
{
    private const string Esc = "\e";

    // Named keys: (unmodified sequence, CSI final or "~" number for modified forms).
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = "\r", ["Escape"] = Esc, ["Tab"] = "\t", ["BTab"] = "\e[Z", ["Space"] = " ", ["BSpace"] = "\x7f",
        ["Up"] = "\e[A", ["Down"] = "\e[B", ["Right"] = "\e[C", ["Left"] = "\e[D",
        ["Home"] = "\e[H", ["End"] = "\e[F",
        ["PageUp"] = "\e[5~", ["PgUp"] = "\e[5~", ["PPage"] = "\e[5~",
        ["PageDown"] = "\e[6~", ["PgDn"] = "\e[6~", ["NPage"] = "\e[6~",
        ["IC"] = "\e[2~", ["Insert"] = "\e[2~", ["DC"] = "\e[3~", ["Delete"] = "\e[3~",
        ["F1"] = "\eOP", ["F2"] = "\eOQ", ["F3"] = "\eOR", ["F4"] = "\eOS",
        ["F5"] = "\e[15~", ["F6"] = "\e[17~", ["F7"] = "\e[18~", ["F8"] = "\e[19~",
        ["F9"] = "\e[20~", ["F10"] = "\e[21~", ["F11"] = "\e[23~", ["F12"] = "\e[24~",
    };

    /// <summary>
    /// The bytes for one send-keys argument. <paramref name="appCursor"/>: the pane is in application cursor-key
    /// mode (DECCKM), so unmodified arrows / Home / End are sent as SS3.
    /// </summary>
    public static string Translate(string key, bool appCursor = false) =>
        TryTranslateKey(key, appCursor, out string? bytes) ? bytes : key;

    /// <summary>True when <paramref name="key"/> is a key name (possibly with modifiers) rather than text.</summary>
    public static bool TryTranslateKey(string key, bool appCursor, out string bytes)
    {
        bytes = "";
        if (key.Length == 0) return false;
        string rest = StripModifiers(key, out bool ctrl, out bool meta, out bool shift);
        bool modified = ctrl || meta || shift;
        if (!modified && rest.Length == 1) return false; // plain text, e.g. "y"

        if (Named.TryGetValue(rest, out var named))
        {
            if (Modify(rest, named, ctrl, meta, shift, appCursor) is not { } seq) return false;
            bytes = seq;
            return true;
        }
        if (rest.Length != 1 || !modified) return false;
        return TranslateCharacter(rest[0], ctrl, meta, shift, out bytes);
    }

    /// <summary>The key name after its <c>C-</c>, <c>M-</c>, <c>S-</c> and <c>^</c> prefixes.</summary>
    private static string StripModifiers(string key, out bool ctrl, out bool meta, out bool shift)
    {
        ctrl = meta = shift = false;
        string rest = key;
        while (rest.Length > 2 && rest[1] == '-' && "CcMmSs".Contains(rest[0]))
        {
            switch (char.ToUpperInvariant(rest[0]))
            {
                case 'C': ctrl = true; break;
                case 'M': meta = true; break;
                default: shift = true; break;
            }
            rest = rest[2..];
        }
        if (rest.Length == 2 && rest[0] == '^') { ctrl = true; rest = rest[1..]; }
        return rest;
    }

    private static bool TranslateCharacter(char c, bool ctrl, bool meta, bool shift, out string bytes)
    {
        bytes = "";
        string s = c.ToString();
        if (shift) s = s.ToUpperInvariant();
        if (ctrl)
        {
            if (Control(c) is not { } cc) return false;
            s = cc.ToString();
        }
        bytes = meta ? Esc + s : s;
        return true;
    }

    private static char? Control(char c) => c switch
    {
        >= 'a' and <= 'z' => (char)(c - 'a' + 1),
        >= 'A' and <= 'Z' => (char)(c - 'A' + 1),
        '@' or ' ' or '2' => '\0',
        '[' or '3' => '\e',
        '\\' or '4' => (char)28,
        ']' or '5' => (char)29,
        '^' or '6' or '~' => (char)30,
        '_' or '7' or '/' => (char)31,
        '?' or '8' => (char)127,
        _ => null,
    };

    private static string? Modify(string name, string seq, bool ctrl, bool meta, bool shift, bool appCursor)
    {
        if (!(ctrl || meta || shift)) return Unmodified(seq, appCursor);
        return ModifyCharacterKey(name.ToLowerInvariant(), ctrl, meta, shift) ?? ModifySequence(seq, ctrl, meta, shift);
    }

    private static string Unmodified(string seq, bool appCursor)
    {
        if (appCursor && seq.Length == 3 && seq[1] == '[' && seq[2] is 'A' or 'B' or 'C' or 'D' or 'H' or 'F')
            return "\eO" + seq[2];
        return seq;
    }

    /// <summary>Keys with a plain-character encoding: modifiers work like on a character. Null for the other keys.</summary>
    private static string? ModifyCharacterKey(string lowerName, bool ctrl, bool meta, bool shift)
    {
        switch (lowerName)
        {
            case "enter": return WithMeta(meta, "\r");
            case "space": return ctrl ? "\0" : WithMeta(meta, " ");
            case "bspace": return WithMeta(meta, ctrl ? "\b" : "\x7f");
            case "escape": return WithMeta(meta, Esc);
            case "tab": return shift ? "\e[Z" : WithMeta(meta, "\t");
            case "btab": return "\e[Z";
            default: return null;
        }
    }

    private static string WithMeta(bool meta, string s) => meta ? Esc + s : s;

    /// <summary>The sequence with xterm's modifier parameter: 1 + shift(1) + alt(2) + ctrl(4). Null when it takes none.</summary>
    private static string? ModifySequence(string seq, bool ctrl, bool meta, bool shift)
    {
        int m = 1 + (shift ? 1 : 0) + (meta ? 2 : 0) + (ctrl ? 4 : 0);
        string mod = m.ToString(CultureInfo.InvariantCulture);
        if (seq.StartsWith("\eO", StringComparison.Ordinal)) return $"\e[1;{mod}{seq[2]}"; // F1–F4
        if (seq.EndsWith('~')) return $"{seq[..^1]};{mod}~";
        if (seq.Length == 3 && seq[1] == '[') return $"\e[1;{mod}{seq[2]}";
        return null;
    }

    /// <summary><c>send-keys -H</c>: each argument is one byte in hex.</summary>
    public static string FromHex(IEnumerable<string> args)
    {
        var bytes = new List<byte>();
        foreach (string a in args)
        {
            if (!byte.TryParse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                throw new TmuxUsageException($"invalid key: {a}");
            bytes.Add(b);
        }
        return Encoding.Latin1.GetString(bytes.ToArray());
    }
}
