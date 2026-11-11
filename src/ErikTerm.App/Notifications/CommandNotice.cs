using System.Globalization;

namespace ErikTerm.App.Notifications;

/// <summary>
/// notify_command_duration: whether a finished shell command (OSC 133;C … D) is worth a notification, and its text —
/// "✓ cargo build finished in 1m 12s" / "✗ cargo build exited 2 after 1m 12s", with the whole command line as the
/// body when the title had to shorten it. Pure, so it can be tested without a window (WindowNotifications.cs).
/// </summary>
internal static class CommandNotice
{
    /// <summary>How much of the command line the title shows.</summary>
    public const int TitleCommandChars = 48;

    /// <summary>Long enough to tell: <paramref name="thresholdSeconds"/> &gt; 0 and the command ran at least that long.</summary>
    public static bool ShouldNotify(long durationMs, double thresholdSeconds) =>
        thresholdSeconds > 0 && durationMs >= thresholdSeconds * 1000;

    /// <summary>"12s", "1m 12s", "1h 3m", "2d 4h" (seconds below a minute are rounded down; at least "0s").</summary>
    public static string Duration(long ms)
    {
        long s = Math.Max(0, ms) / 1000;
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + "s";
        long m = s / 60;
        if (m < 60) return $"{m}m {s % 60}s";
        long h = m / 60;
        if (h < 24) return $"{h}h {m % 60}m";
        return $"{h / 24}d {h % 24}h";
    }

    /// <summary>The notification's title and body. An unknown command line ("") reads as "Command".</summary>
    public static (string Title, string Body) Text(string? command, int exitCode, long durationMs)
    {
        string full = NotificationText.Clean(command, NotificationText.MaxBody);
        int cut = TitleCommandChars - 1;
        if (full.Length > TitleCommandChars && char.IsHighSurrogate(full[cut - 1])) cut--; // never half a character
        string shown;
        if (full.Length == 0) shown = "Command";
        else shown = full.Length <= TitleCommandChars ? full : full[..cut].TrimEnd() + "…";
        string duration = Duration(durationMs);
        string title = exitCode == 0 ? $"✓ {shown} finished in {duration}" : $"✗ {shown} exited {exitCode} after {duration}";
        return (title, shown.Length < full.Length ? full : "");
    }
}
