using ErikTerm.Config;

namespace ErikTerm.App.Panes;

/// <summary>What a close asks about: one pane, a tab, a window, the last window (quitting erikterm), or restarting a pane's program.</summary>
internal enum CloseScope { Pane, Tab, Window, Quit, Restart }

/// <summary>
/// The close prompt's decisions and texts (window_close_confirmation, wezterm-style): which panes would lose something
/// when closed, and what the prompt says. Pure, so it can be tested without a window.
/// </summary>
internal static class CloseConfirmation
{
    /// <summary>The info bar message's key (one close prompt per window at a time).</summary>
    public const string Key = "close-confirmation";
    /// <summary>Above the OSC 52 and notification questions: the user just asked for this.</summary>
    public const int Priority = Security.Osc52Prompt.Priority + 10;
    /// <summary>How many names the prompt lists before "and N more".</summary>
    public const int MaxNames = 3;

    /// <summary>
    /// The name to list for a pane ("nvim", "Claude"), or null when closing it loses nothing. A coding agent counts
    /// even when it is idle (<paramref name="agent"/>: its display name); a foreground program counts unless it is in
    /// <paramref name="skip"/> (shells). <paramref name="foreground"/> is the foreground process name (/proc comm, or
    /// null when unknown).
    /// </summary>
    public static string? RunningProgram(WindowCloseConfirmation policy, string? foreground, string? agent, IReadOnlyList<string> skip)
    {
        if (policy == WindowCloseConfirmation.NeverPrompt) return null;
        if (foreground is { Length: > 0 } && IsSkipped(foreground, skip)) return null; // a shell at its prompt (a stale agent record doesn't count)
        if (agent is { Length: > 0 }) return agent;
        return foreground is { Length: > 0 } ? foreground : null;
    }

    /// <summary>
    /// Whether <paramref name="foreground"/> is in the list: exact, a login shell's "-zsh", "pwsh.exe" for "pwsh", or
    /// a name /proc cut at 15 bytes.
    /// </summary>
    public static bool IsSkipped(string foreground, IReadOnlyList<string> skip)
    {
        string name = foreground.TrimStart('-');
        foreach (string s in skip)
        {
            if (string.Equals(name, s, StringComparison.Ordinal)) return true;
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(name, s[..^4], StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Length == 15 && s.Length > 15 && s.StartsWith(name, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// "nvim", "nvim and Claude", "nvim, Claude and cargo", "nvim, Claude, cargo and 2 more"; a name that runs in
    /// several panes once, with its count: "nvim (2) and Claude".
    /// </summary>
    public static string Names(IReadOnlyList<string> names)
    {
        var distinct = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string n in names)
        {
            if (counts.TryGetValue(n, out int c)) counts[n] = c + 1;
            else { counts[n] = 1; distinct.Add(n); }
        }
        var shown = distinct.Select(n => counts[n] > 1 ? $"{n} ({counts[n]})" : n).ToList();
        if (shown.Count == 0) return "";
        if (shown.Count == 1) return shown[0];
        if (shown.Count <= MaxNames) return string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
        return string.Join(", ", shown.Take(MaxNames)) + $" and {shown.Count - MaxNames} more";
    }

    /// <summary>The question: "Close this pane? nvim is still running." / "Quit erikterm? nvim and Claude are still running."</summary>
    public static string Text(CloseScope scope, IReadOnlyList<string> names)
    {
        string question = scope switch
        {
            CloseScope.Pane => "Close this pane?",
            CloseScope.Tab => "Close this tab?",
            CloseScope.Window => "Close this window?",
            CloseScope.Restart => "Restart this pane?",
            _ => "Quit erikterm?",
        };
        return $"{question} {Names(names)} {(names.Count == 1 ? "is" : "are")} still running";
    }

    /// <summary>The button that closes: "Close Pane", "Close Tab", "Close Window", "Quit" (and "Restart").</summary>
    public static string ConfirmLabel(CloseScope scope) => scope switch
    {
        CloseScope.Pane => "Close Pane",
        CloseScope.Tab => "Close Tab",
        CloseScope.Window => "Close Window",
        CloseScope.Restart => "Restart",
        _ => "Quit",
    };
}
