using System.Text;
using ErikTerm.Core;

namespace ErikTerm.App.Notifications;

/// <summary>
/// Text hygiene for notifications. Everything in a notification can come from bytes a program printed (an escape
/// sequence in a file you <c>cat</c>), so: control and bidi characters are removed (<see cref="AgentSignals.Clean"/>),
/// lengths are capped, the desktop body is markup-escaped (notification daemons parse a subset of HTML in it), and
/// erikterm's own bar draws it as plain text.
/// </summary>
internal static class NotificationText
{
    public const int MaxTitle = 120, MaxBody = 400, MaxApp = 40, MaxPlace = 120;

    /// <summary>One plain line: control/bidi characters gone, whitespace collapsed, capped with an ellipsis; "" when empty.</summary>
    public static string Clean(string? s, int max) => s is null ? "" : AgentSignals.Clean(s, max) ?? "";

    /// <summary>
    /// Escapes the characters the freedesktop body markup (and Pango) treat specially, so a program can't add links,
    /// images or formatting, or hide text: <c>&amp; &lt; &gt; " '</c>.
    /// </summary>
    public static string EscapeMarkup(string s)
    {
        int i = s.AsSpan().IndexOfAny("&<>\"'");
        if (i < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        sb.Append(s, 0, i);
        for (; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A title notify-send can't take for an option: it goes after "--" anyway, but never starts with '-'.</summary>
    public static string DesktopTitle(string title) => title.StartsWith('-') ? "‐" + title[1..] : title;

    /// <summary>"a · b", leaving out empty parts.</summary>
    public static string Join(string a, string b)
    {
        if (a.Length == 0) return b;
        return b.Length == 0 ? a : $"{a} · {b}";
    }

    /// <summary>"just now", "1 min ago", "12 min ago", "3 h ago", "2 d ago" — coarse on purpose: the bar redraws once a minute at most.</summary>
    public static string Age(long ms)
    {
        long min = Math.Max(0, ms) / 60_000;
        if (min < 1) return "just now";
        if (min < 60) return $"{min} min ago";
        long h = min / 60;
        return h < 24 ? $"{h} h ago" : $"{h / 24} d ago";
    }

    /// <summary>Milliseconds until <see cref="Age"/> changes for something <paramref name="ms"/> old.</summary>
    public static long UntilAgeChanges(long ms)
    {
        ms = Math.Max(0, ms);
        long step = ms switch
        {
            < 3_600_000 => 60_000,
            < 86_400_000 => 3_600_000,
            _ => 86_400_000,
        };
        return step - ms % step;
    }
}
