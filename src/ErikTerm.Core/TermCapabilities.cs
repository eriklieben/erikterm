using System.Text;

namespace ErikTerm.Core;

public enum CapKind : byte { Boolean, Number, String, Cancel }

/// <summary>Where a capability comes from in the xterm-erikterm entry.</summary>
public enum CapSource : byte
{
    /// <summary>Written in the entry (contrib/terminfo/erikterm.terminfo).</summary>
    Entry,
    /// <summary>Inherited from xterm-256color through <c>use=</c>, with this value; answered by XTGETTCAP too.</summary>
    Inherited,
    /// <summary>Only an XTGETTCAP answer (xterm's pseudo-capabilities like <c>TN</c>, or what ncurses would misread).</summary>
    QueryOnly,
}

/// <summary>One capability: terminfo name, kind, value in terminfo source notation (<c>\E</c>, <c>^?</c>, <c>%p1%d</c>).</summary>
public readonly record struct TermCap(string Name, CapKind Kind, string Value, CapSource Source, string Comment);

/// <summary>
/// What erikterm supports, as terminfo capabilities: the single source of truth for both the <c>xterm-erikterm</c>
/// terminfo entry (<see cref="TerminfoSource"/>, checked in as contrib/terminfo/erikterm.terminfo — a test fails if they
/// differ) and the XTGETTCAP replies (<c>DCS + q name ST</c>, <see cref="Lookup"/>).
/// The entry is xterm-256color plus the extensions kitty, wezterm, ghostty and tmux use (styled and colored
/// underlines, cursor shape, OSC 52, synchronized output, true color), minus what erikterm doesn't do.
/// </summary>
public static class TermCapabilities
{
    public const string Name = "xterm-erikterm";
    public const string BaseEntry = "xterm-256color";

    public static readonly TermCap[] All =
    [
        // ---- Booleans
        new("Tc", CapKind.Boolean, "", CapSource.Entry, "24-bit color (tmux, Neovim)"),
        new("Su", CapKind.Boolean, "", CapSource.Entry, "styled and colored underlines (kitty's flag; Neovim checks it)"),
        new("fullkbd", CapKind.Boolean, "", CapSource.Entry, "the kitty keyboard protocol"),
        // ---- Numbers
        new("colors", CapKind.Number, "256", CapSource.Entry, "palette size"),
        // ---- Strings: underlines, overline, strikethrough
        new("Smulx", CapKind.String, @"\E[4:%p1%dm", CapSource.Entry, "underline style: 0 none, 1 single, 2 double, 3 curly, 4 dotted, 5 dashed"),
        new("Setulc", CapKind.String, @"\E[58:2::%p1%{65536}%/%d:%p1%{256}%/%{255}%&%d:%p1%{255}%&%dm", CapSource.Entry, "underline color, 0xRRGGBB"),
        new("Smol", CapKind.String, @"\E[53m", CapSource.Entry, "overline"),
        new("Rmol", CapKind.String, @"\E[55m", CapSource.Entry, "no overline"),
        new("smxx", CapKind.String, @"\E[9m", CapSource.Entry, "strikethrough"),
        new("rmxx", CapKind.String, @"\E[29m", CapSource.Entry, "no strikethrough"),
        // ---- Cursor
        new("Ss", CapKind.String, @"\E[%p1%d q", CapSource.Entry, "cursor shape (DECSCUSR)"),
        new("Se", CapKind.String, @"\E[0 q", CapSource.Entry, "cursor shape back to the configured default"),
        new("Cs", CapKind.String, @"\E]12;%p1%s\007", CapSource.Entry, "cursor color (OSC 12)"),
        new("Cr", CapKind.String, @"\E]112\007", CapSource.Entry, "cursor color back to the scheme's (OSC 112)"),
        new("cnorm", CapKind.String, @"\E[?25h", CapSource.Entry, "show the cursor without forcing it steady (xterm's adds ?12l)"),
        new("cvvis", CapKind.Cancel, "", CapSource.Entry, "no separate 'very visible' cursor"),
        // ---- Clipboard, sync, modes
        new("Ms", CapKind.String, @"\E]52;%p1%s;%p2%s\007", CapSource.Entry, "set the clipboard (OSC 52)"),
        new("Sync", CapKind.String, @"\E[?2026%?%p1%{1}%-%tl%eh%;", CapSource.Entry, "synchronized output (mode 2026)"),
        new("BE", CapKind.String, @"\E[?2004h", CapSource.Entry, "bracketed paste on"),
        new("BD", CapKind.String, @"\E[?2004l", CapSource.Entry, "bracketed paste off"),
        new("PS", CapKind.String, @"\E[200~", CapSource.Entry, "start of a paste"),
        new("PE", CapKind.String, @"\E[201~", CapSource.Entry, "end of a paste"),
        new("fe", CapKind.String, @"\E[?1004h", CapSource.Entry, "focus reporting on"),
        new("fd", CapKind.String, @"\E[?1004l", CapSource.Entry, "focus reporting off"),
        new("XM", CapKind.String, @"\E[?1006;1000%?%p1%{1}%=%th%el%;", CapSource.Entry, "SGR mouse reporting on / off (ncurses)"),
        new("XR", CapKind.String, @"\E[>0q", CapSource.Entry, "ask for the terminal's name and version (XTVERSION)"),
        new("E3", CapKind.String, @"\E[3J", CapSource.Inherited, "clear the scrollback"),
        // ---- What xterm-256color has and erikterm doesn't
        new("smm", CapKind.Cancel, "", CapSource.Entry, "no meta mode (?1034)"),
        new("rmm", CapKind.Cancel, "", CapSource.Entry, "no meta mode (?1034)"),
        new("smglp", CapKind.Cancel, "", CapSource.Entry, "no left/right margins (DECSLRM)"),
        new("smglr", CapKind.Cancel, "", CapSource.Entry, "no left/right margins (DECSLRM)"),
        new("smgrp", CapKind.Cancel, "", CapSource.Entry, "no left/right margins (DECSLRM)"),
        new("mgc", CapKind.Cancel, "", CapSource.Entry, "no left/right margins (DECSLRM)"),
        // ---- Keys (legacy encoding, application cursor mode), as in xterm-256color
        new("kcuu1", CapKind.String, @"\EOA", CapSource.Inherited, "up"),
        new("kcud1", CapKind.String, @"\EOB", CapSource.Inherited, "down"),
        new("kcuf1", CapKind.String, @"\EOC", CapSource.Inherited, "right"),
        new("kcub1", CapKind.String, @"\EOD", CapSource.Inherited, "left"),
        new("khome", CapKind.String, @"\EOH", CapSource.Inherited, "home"),
        new("kend", CapKind.String, @"\EOF", CapSource.Inherited, "end"),
        new("kich1", CapKind.String, @"\E[2~", CapSource.Inherited, "insert"),
        new("kdch1", CapKind.String, @"\E[3~", CapSource.Inherited, "delete"),
        new("kpp", CapKind.String, @"\E[5~", CapSource.Inherited, "page up"),
        new("knp", CapKind.String, @"\E[6~", CapSource.Inherited, "page down"),
        new("kbs", CapKind.String, @"^?", CapSource.Entry, "backspace: written, because macOS's xterm-256color says ^H and the key sends ^?"),
        new("kcbt", CapKind.String, @"\E[Z", CapSource.Inherited, "shift+tab"),
        new("kf1", CapKind.String, @"\EOP", CapSource.Inherited, "F1"),
        new("kf2", CapKind.String, @"\EOQ", CapSource.Inherited, "F2"),
        new("kf3", CapKind.String, @"\EOR", CapSource.Inherited, "F3"),
        new("kf4", CapKind.String, @"\EOS", CapSource.Inherited, "F4"),
        new("kf5", CapKind.String, @"\E[15~", CapSource.Inherited, "F5"),
        new("kf6", CapKind.String, @"\E[17~", CapSource.Inherited, "F6"),
        new("kf7", CapKind.String, @"\E[18~", CapSource.Inherited, "F7"),
        new("kf8", CapKind.String, @"\E[19~", CapSource.Inherited, "F8"),
        new("kf9", CapKind.String, @"\E[20~", CapSource.Inherited, "F9"),
        new("kf10", CapKind.String, @"\E[21~", CapSource.Inherited, "F10"),
        new("kf11", CapKind.String, @"\E[23~", CapSource.Inherited, "F11"),
        new("kf12", CapKind.String, @"\E[24~", CapSource.Inherited, "F12"),
        // ---- XTGETTCAP only
        new("TN", CapKind.String, "", CapSource.QueryOnly, "terminal name: the TERM erikterm set (xterm's pseudo-capability)"),
        new("Co", CapKind.Number, "256", CapSource.QueryOnly, "termcap name of colors"),
        new("RGB", CapKind.String, "8/8/8", CapSource.QueryOnly, "true color, bits per channel (kitty's answer; not in the entry: ncurses would read it as direct-color setaf)"),
    ];

    private static readonly Dictionary<string, TermCap> ByName = All.ToDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>A capability XTGETTCAP can answer (cancelled ones are not).</summary>
    public static bool Lookup(string name, out TermCap cap) =>
        ByName.TryGetValue(name, out cap) && cap.Kind != CapKind.Cancel;

    /// <summary>
    /// The XTGETTCAP value of a capability: the string with terminfo escapes decoded (ESC, controls; parameters
    /// like <c>%p1%d</c> stay as they are, as xterm and kitty send them), a number in decimal, null for a boolean.
    /// </summary>
    public static string? ReplyValue(in TermCap cap, string terminalName) => cap.Kind switch
    {
        CapKind.Boolean => null,
        CapKind.Number => cap.Value,
        _ => cap.Name == "TN" ? terminalName : DecodeTerminfo(cap.Value),
    };

    /// <summary>Decodes terminfo source escapes: \E \e ESC, ^X controls, \\ \, \, \: \^ \n \r \t \b \f \s, \0ooo octal.</summary>
    public static string DecodeTerminfo(string value)
    {
        var sb = new StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c == '^' && i + 1 < value.Length)
            {
                char n = value[i + 1];
                sb.Append(n == '?' ? '\x7f' : (char)(char.ToUpperInvariant(n) & 0x1F));
                i += 2;
            }
            else if (c == '\\' && i + 1 < value.Length)
            {
                AppendTerminfoEscape(sb, value, ref i);
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Decodes the escape after the backslash at <paramref name="i"/>, leaving <paramref name="i"/> on its last character.</summary>
    private static void AppendTerminfoEscape(StringBuilder sb, string value, ref int i)
    {
        char e = value[++i];
        switch (e)
        {
            case 'E' or 'e': sb.Append('\e'); break;
            case 'n': sb.Append('\n'); break;
            case 'r': sb.Append('\r'); break;
            case 't': sb.Append('\t'); break;
            case 'b': sb.Append('\b'); break;
            case 'f': sb.Append('\f'); break;
            case 's': sb.Append(' '); break;
            case >= '0' and <= '7': AppendTerminfoOctal(sb, value, ref i); break;
            default: sb.Append(e); break; // \\ \, \: \^
        }
    }

    private static void AppendTerminfoOctal(StringBuilder sb, string value, ref int i)
    {
        int v = 0, k = 0;
        while (k < 3 && i < value.Length && value[i] is >= '0' and <= '7') { v = v * 8 + (value[i] - '0'); i++; k++; }
        i--;
        sb.Append(v == 0 ? '\x80' : (char)v); // \0 is NUL encoded as 0x80 in terminfo
    }

    /// <summary>The terminfo source of the entry (contrib/terminfo/erikterm.terminfo; compile with <c>tic -x</c>).</summary>
    public static string TerminfoSource()
    {
        var sb = new StringBuilder();
        sb.Append("# xterm-erikterm: terminfo for erikterm. GENERATED from src/ErikTerm.Core/TermCapabilities.cs\n");
        sb.Append("# (the same table answers XTGETTCAP): edit that file, then run `erikterm --terminfo > contrib/terminfo/erikterm.terminfo`.\n");
        sb.Append("# Install: scripts/install-terminfo.sh (tic -x -o ~/.terminfo). On another machine (ssh):\n");
        sb.Append("#   infocmp -x xterm-erikterm | ssh HOST 'tic -x -o ~/.terminfo /dev/stdin'\n");
        sb.Append(Name).Append("|erikterm terminal emulator,\n");
        foreach (var kind in (ReadOnlySpan<CapKind>)[CapKind.Boolean, CapKind.Number, CapKind.String, CapKind.Cancel])
        {
            foreach (var cap in All)
            {
                if (cap.Source != CapSource.Entry || cap.Kind != kind) continue;
                sb.Append('\t').Append(cap.Name);
                switch (kind)
                {
                    case CapKind.Number: sb.Append('#').Append(cap.Value); break;
                    case CapKind.String: sb.Append('=').Append(cap.Value); break;
                    case CapKind.Cancel: sb.Append('@'); break;
                }
                sb.Append(",\n");
            }
        }
        sb.Append("\tuse=").Append(BaseEntry).Append(",\n");
        return sb.ToString();
    }
}
