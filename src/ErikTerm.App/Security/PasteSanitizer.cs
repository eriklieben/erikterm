using System.Text;

namespace ErikTerm.App.Security;

/// <summary>
/// What a paste sends to the program (clipboard, primary selection, drag and drop, quick select, <c>cli send-text</c>).
/// Pasted text comes from anywhere — a web page, or a program that set the clipboard with OSC 52 — so it must not be
/// able to leave a bracketed paste early (a nested or split <c>ESC [201~</c>) or press keys (^C, ^U, ESC …) for the user.
/// </summary>
internal static class PasteSanitizer
{
    public const string BracketStart = "\e[200~";
    public const string BracketEnd = "\e[201~";

    /// <summary>The bytes' text and how many characters were taken out (for a "sanitised" toast).</summary>
    public readonly record struct Result(string Text, int Removed);

    /// <summary>
    /// Line endings become CR (Enter). Inside a bracketed paste every ESC sequence and every C0/C1 control character
    /// other than TAB and CR is removed, always; outside one the same happens when <paramref name="sanitize"/> is on
    /// (paste_sanitize), else the text goes as it is. The result is wrapped in the bracketed-paste markers when
    /// <paramref name="bracketed"/>.
    /// </summary>
    public static Result Prepare(string text, bool bracketed, bool sanitize = true)
    {
        text = text.ReplaceLineEndings("\r");
        int removed = 0;
        if (bracketed || sanitize) (text, removed) = Clean(text);
        if (text.Length == 0) return new Result("", removed);
        return new Result(bracketed ? BracketStart + text + BracketEnd : text, removed);
    }

    /// <summary>A control character a paste may carry: TAB and CR (Enter, the normalised line ending).</summary>
    private static bool Kept(char c) => c is '\t' or '\r';

    /// <summary>C0, DEL and C1 (U+0080–U+009F, which include the 8-bit CSI/OSC/DCS/ST introducers).</summary>
    public static bool IsControl(char c) => c < ' ' || c is >= '\x7f' and <= '\x9f';

    /// <summary>
    /// Removes ESC sequences whole (CSI, OSC/DCS/APC/PM/SOS strings, two-character escapes — 7-bit or C1-introduced) so
    /// pasted colour codes don't leave "[31m" behind, then every remaining control except TAB and CR. Returns how many
    /// characters went. An unterminated string sequence loses only its introducer.
    /// </summary>
    public static (string Text, int Removed) Clean(string text)
    {
        int i = 0;
        for (; i < text.Length; i++) if (IsControl(text[i]) && !Kept(text[i])) break;
        if (i == text.Length) return (text, 0);

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, i);
        while (i < text.Length)
        {
            char c = text[i];
            if (!IsControl(c) || Kept(c)) { sb.Append(c); i++; continue; }
            i = SkipSequence(text, i);
        }
        return (sb.ToString(), text.Length - sb.Length);
    }

    /// <summary>The index after the control at <paramref name="i"/> and the sequence it introduces (if any).</summary>
    private static int SkipSequence(string s, int i)
    {
        char c = s[i];
        if (c == '\e') return SkipEscape(s, i);
        if (c == '\x9b') return SkipCsi(s, i + 1);
        if (c == '\x9d') return SkipString(s, i + 1, ']');
        if (c is '\x90' or '\x98' or '\x9e' or '\x9f') return SkipString(s, i + 1, 'P');
        return i + 1;
    }

    /// <summary>An ESC at <paramref name="i"/>: what it introduces decides how far it reaches.</summary>
    private static int SkipEscape(string s, int i)
    {
        if (i + 1 >= s.Length) return i + 1;
        char n = s[i + 1];
        if (n == '[') return SkipCsi(s, i + 2);
        if (n is ']' or 'P' or 'X' or '^' or '_') return SkipString(s, i + 2, n);
        if (n is >= ' ' and <= '/') return SkipIntermediates(s, i + 1);
        return n is >= '0' and <= '~' ? i + 2 : i + 1; // ESC 7, ESC c, ESC M …
    }

    /// <summary>ESC intermediates final (e.g. ESC ( B): the final byte ends it. <paramref name="j"/> is the first intermediate.</summary>
    private static int SkipIntermediates(string s, int j)
    {
        while (j < s.Length && s[j] is >= ' ' and <= '/') j++;
        return j < s.Length && s[j] is >= '0' and <= '~' ? j + 1 : j;
    }

    /// <summary>
    /// CSI: parameters 0x30–0x3F, intermediates 0x20–0x2F, a final 0x40–0x7E. Anything else (another ESC, a newline)
    /// aborts it there, like a terminal does — so "ESC [20 ESC [201~" loses both halves. <paramref name="j"/> is the
    /// index after the introducer.
    /// </summary>
    private static int SkipCsi(string s, int j)
    {
        while (j < s.Length && s[j] is >= '0' and <= '?') j++;
        while (j < s.Length && s[j] is >= ' ' and <= '/') j++;
        return j < s.Length && s[j] is >= '@' and <= '~' ? j + 1 : j;
    }

    /// <summary>
    /// A string (OSC, DCS, SOS, PM, APC) runs to ST (ESC \ or 0x9C) — OSC also to BEL. <paramref name="j"/> is the
    /// index after the introducer; <paramref name="kind"/> is ']' for OSC.
    /// </summary>
    private static int SkipString(string s, int j, char kind)
    {
        for (int k = j; k < s.Length; k++)
        {
            char t = s[k];
            if (t == '\x9c' || (t == '\a' && kind == ']')) return k + 1;
            if (t == '\e' && k + 1 < s.Length && s[k + 1] == '\\') return k + 2;
        }
        return j; // unterminated: drop the introducer; the rest is checked character by character
    }
}
