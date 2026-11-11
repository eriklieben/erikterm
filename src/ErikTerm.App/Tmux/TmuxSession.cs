using System.Globalization;
using ErikTerm.App.Ipc;

namespace ErikTerm.App.Tmux;

/// <summary>A target that doesn't name anything in the fake session: it belongs to a real tmux server, if any.</summary>
public sealed class TmuxNotOursException(string message) : Exception(message);

/// <summary>A target that names the fake session but something that isn't there (exit 1 with tmux's message).</summary>
public sealed class TmuxTargetException(string message) : Exception(message);

/// <summary>One erikterm tab, seen as a tmux window.</summary>
internal sealed class TmuxWindow
{
    public required int TabId { get; init; }
    public required int Index { get; init; }
    public List<PaneInfo> Panes { get; } = [];
    public PaneInfo Active => Panes.FirstOrDefault(p => p.IsActive) ?? Panes[0];
    public bool IsCurrent => Panes.Any(p => p.TabActive || p.IsFocused);
    public string Name => Panes[0].TabTitle is { Length: > 0 } t ? t : Active.ForegroundProcessName ?? Active.Title;
    public int Width => Panes.Max(p => p.Left + p.Size.Cols);
    public int Height => Panes.Max(p => p.Top + p.Size.Rows);
    public bool Zoomed => Panes.Any(p => p.IsZoomed);
}

/// <summary>
/// A snapshot of one erikterm window as a tmux session (from the IPC <c>list</c>): tabs are windows, panes
/// are panes (<c>%id</c> = erikterm pane id), and there is one session, <see cref="Name"/> (<c>$0</c>): the
/// erikterm window of the calling pane. Window indexes, names and listings stay within that window; <c>%id</c> and
/// <c>@id</c> (process-wide ids) also reach panes and tabs of the process's other windows.
/// Resolves tmux targets and supplies format variables.
/// </summary>
internal sealed class TmuxSession
{
    public const string Name = "erikterm";
    public const string VersionNumber = "3.5";
    public const string TermType = "erikterm 0.1"; // what erikterm answers to XTVERSION

    /// <summary>The session's windows: the tabs of the caller's erikterm window, in order.</summary>
    public List<TmuxWindow> Windows { get; } = [];
    /// <summary>The panes of the caller's erikterm window.</summary>
    public IReadOnlyList<PaneInfo> Panes { get; }
    /// <summary>The pane the command runs in ($TMUX_PANE), else the focused one.</summary>
    public PaneInfo Current { get; }
    public string Socket { get; init; } = "";
    public int ServerPid { get; init; }
    /// <summary>Option values for formats and show-options.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = TmuxOptions.Defaults;

    private readonly IReadOnlyList<PaneInfo> _allPanes;   // every window's: %id targets
    private readonly List<TmuxWindow> _allWindows = [];   // every window's tabs: @id targets, WindowOf

    public TmuxSession(IReadOnlyList<PaneInfo> panes, int? callerPane)
    {
        if (panes.Count == 0) throw new TmuxTargetException("no current session");
        _allPanes = panes;
        Current = (callerPane is { } id ? panes.FirstOrDefault(p => p.PaneId == id) : null)
                  ?? panes.FirstOrDefault(p => p.IsFocused) ?? panes[0];
        int windowId = Current.WindowId;
        Panes = panes.Where(p => p.WindowId == windowId).ToList();
        foreach (var p in panes)
        {
            var w = _allWindows.FirstOrDefault(x => x.TabId == p.TabId);
            if (w is null)
            {
                w = new TmuxWindow { TabId = p.TabId, Index = p.TabIndex };
                _allWindows.Add(w);
                if (p.WindowId == windowId) Windows.Add(w);
            }
            w.Panes.Add(p);
        }
        Windows.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    public TmuxWindow WindowOf(PaneInfo pane) => _allWindows.First(w => w.TabId == pane.TabId);
    public TmuxWindow CurrentWindow => WindowOf(Current);
    public int IndexInWindow(PaneInfo pane) => WindowOf(pane).Panes.IndexOf(pane);

    public static string PaneId(PaneInfo p) => "%" + p.PaneId.ToString(CultureInfo.InvariantCulture);
    public static string WindowId(TmuxWindow w) => "@" + w.TabId.ToString(CultureInfo.InvariantCulture);

    // ==== Targets ==============================================================================

    /// <summary>Whether a session target (has-session -t, kill-session -t) is this session.</summary>
    public static bool IsOurSessionName(string s) => s is Name or "$0" || (s.Length > 0 && Name.StartsWith(s, StringComparison.Ordinal));

    /// <summary>A target-pane (<c>-t</c>): <c>%id</c>, <c>@win[.pane]</c>, <c>[session:][window][.pane]</c>, a pane index, or null for the current pane.</summary>
    public PaneInfo ResolvePane(string? target)
    {
        if (string.IsNullOrEmpty(target)) return Current;
        if (target[0] == '%') return PaneById(target) ?? throw new TmuxNotOursException($"can't find pane: {target}");
        var (session, window, pane, hasColon) = Split(target);
        if (!hasColon && window is not null && pane is null && window[0] != '@')
        {
            // A bare word: a pane index in the current window, else a session or window name.
            if (IsNumber(window)) return PaneByIndex(CurrentWindow, window, target);
            if (IsOurSessionName(window)) return Current;
            return WindowByName(window)?.Active ?? throw new TmuxNotOursException($"can't find pane: {target}");
        }
        CheckSession(session);
        var w = window is null ? CurrentWindow : ResolveWindowPart(window, target);
        if (pane is null) return w.Active;
        if (pane[0] == '%') return PaneById(pane) is { } byId && byId.TabId == w.TabId ? byId : throw new TmuxTargetException($"can't find pane: {target}");
        return PaneByIndex(w, pane, target);
    }

    /// <summary>A target-window: <c>@id</c>, <c>[session:]index</c>, a name, or null for the current window. A pane target picks its window.</summary>
    public TmuxWindow ResolveWindow(string? target)
    {
        if (string.IsNullOrEmpty(target)) return CurrentWindow;
        if (target[0] == '%') return WindowOf(PaneById(target) ?? throw new TmuxNotOursException($"can't find window: {target}"));
        var (session, window, _, hasColon) = Split(target);
        if (!hasColon && window is not null && window[0] != '@')
        {
            if (IsNumber(window)) return ResolveWindowPart(window, target);
            if (IsOurSessionName(window)) return CurrentWindow;
            return WindowByName(window) ?? throw new TmuxNotOursException($"can't find window: {target}");
        }
        CheckSession(session);
        return window is null ? CurrentWindow : ResolveWindowPart(window, target);
    }

    /// <summary>A target-session: this session's name or <c>$0</c> (or a window / pane of it), else not ours.</summary>
    public void ResolveSession(string? target)
    {
        if (string.IsNullOrEmpty(target)) return;
        if (target[0] is '%' or '@')
        {
            ResolveWindow(target);
            return;
        }
        string session = target.Contains(':') ? target[..target.IndexOf(':')] : target;
        if (session.Length == 0 || IsOurSessionName(session)) return;
        throw new TmuxNotOursException($"can't find session: {session}");
    }

    private static void CheckSession(string? session)
    {
        if (session is { Length: > 0 } s && !IsOurSessionName(s)) throw new TmuxNotOursException($"can't find session: {s}");
    }

    private static (string? Session, string? Window, string? Pane, bool HasColon) Split(string target)
    {
        string? session = null;
        string rest = target;
        int colon = target.IndexOf(':');
        if (colon >= 0)
        {
            session = target[..colon];
            rest = target[(colon + 1)..];
        }
        string? window = rest, pane = null;
        int dot = rest.IndexOf('.');
        if (dot >= 0)
        {
            window = rest[..dot];
            pane = rest[(dot + 1)..];
        }
        return (session, window is { Length: > 0 } ? window : null, pane is { Length: > 0 } ? pane : null, colon >= 0 || dot >= 0);
    }

    private TmuxWindow ResolveWindowPart(string window, string target)
    {
        if (window[0] == '@')
            return _allWindows.FirstOrDefault(w => "@" + w.TabId.ToString(CultureInfo.InvariantCulture) == window)
                   ?? throw new TmuxNotOursException($"can't find window: {target}");
        int cur = Windows.IndexOf(CurrentWindow);
        switch (window)
        {
            case "{start}" or "^": return Windows[0];
            case "{end}" or "$": return Windows[^1];
            case "{next}" or "+": return Windows[(cur + 1) % Windows.Count];
            case "{previous}" or "-": return Windows[(cur - 1 + Windows.Count) % Windows.Count];
        }
        if (IsNumber(window))
        {
            int index = int.Parse(window, CultureInfo.InvariantCulture);
            return Windows.FirstOrDefault(w => w.Index == index) ?? throw new TmuxTargetException($"can't find window: {window}");
        }
        return WindowByName(window) ?? throw new TmuxTargetException($"can't find window: {window}");
    }

    private TmuxWindow? WindowByName(string name) =>
        Windows.FirstOrDefault(w => w.Name == name) ?? Windows.FirstOrDefault(w => w.Name.StartsWith(name, StringComparison.Ordinal));

    private PaneInfo? PaneById(string id) =>
        id.Length > 1 && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? _allPanes.FirstOrDefault(p => p.PaneId == n) : null;

    private static PaneInfo PaneByIndex(TmuxWindow w, string index, string target)
    {
        var cur = w.Panes.IndexOf(w.Active);
        int i = index switch
        {
            "{top}" or "{left}" or "{top-left}" => 0,
            "{bottom}" or "{right}" or "{bottom-right}" or "{last}" => w.Panes.Count - 1,
            "{next}" or "+" => (cur + 1) % w.Panes.Count,
            "{previous}" or "-" => (cur - 1 + w.Panes.Count) % w.Panes.Count,
            _ when IsNumber(index) => int.Parse(index, CultureInfo.InvariantCulture),
            _ => -1,
        };
        if (i < 0 || i >= w.Panes.Count) throw new TmuxTargetException($"can't find pane: {target}");
        return w.Panes[i];
    }

    private static bool IsNumber(string s) => s.Length is > 0 and < 9 && s.All(char.IsAsciiDigit);

    // ==== Formats ==============================================================================

    /// <summary>Format variables for a pane (and its window, the session, the client and the options).</summary>
    public Func<string, string?> Variables(PaneInfo pane)
    {
        var w = WindowOf(pane);
        int index = w.Panes.IndexOf(pane);
        return name => PaneVariable(name, pane, index) ?? PaneGeometryVariable(name, pane, w) ?? PaneStateVariable(name, pane)
            ?? WindowVariable(name, w) ?? SessionVariable(name) ?? ClientVariable(name, w) ?? ServerVariable(name) ?? OptionVariable(name);
    }

    private static string Flag(bool b) => b ? "1" : "0";

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string? PaneVariable(string name, PaneInfo pane, int index) => name switch
    {
        "pane_id" => PaneId(pane),
        "pane_index" => Number(index),
        "pane_active" => Flag(pane.IsActive),
        "pane_pid" => Number(pane.Pid),
        "pane_tty" => pane.TtyName ?? "",
        "pane_current_command" => pane.ForegroundProcessName ?? "",
        "pane_current_path" => pane.CurrentPath ?? CwdPath(pane.Cwd) ?? "",
        "pane_path" => CwdPath(pane.Cwd) ?? "",
        _ => null,
    };

    private static string? PaneGeometryVariable(string name, PaneInfo pane, TmuxWindow w) => name switch
    {
        "pane_width" => Number(pane.Size.Cols),
        "pane_height" => Number(pane.Size.Rows),
        "pane_top" => Number(pane.Top),
        "pane_left" => Number(pane.Left),
        "pane_bottom" => Number(pane.Top + pane.Size.Rows - 1),
        "pane_right" => Number(pane.Left + pane.Size.Cols - 1),
        "pane_at_top" => Flag(pane.Top == 0),
        "pane_at_left" => Flag(pane.Left == 0),
        "pane_at_bottom" => Flag(pane.Top + pane.Size.Rows >= w.Height),
        "pane_at_right" => Flag(pane.Left + pane.Size.Cols >= w.Width),
        _ => null,
    };

    private static string? PaneStateVariable(string name, PaneInfo pane) => name switch
    {
        "pane_title" => pane.PaneTitle ?? pane.Title,
        "pane_dead" or "pane_in_mode" or "pane_synchronized" or "pane_input_off" or "pane_marked" or "pane_pipe" => "0",
        "pane_mode" => "",
        "pane_last" => "0",
        "pane_start_command" => "",
        "cursor_x" => Number(pane.CursorX),
        "cursor_y" => Number(pane.CursorY),
        "history_size" => Number(pane.HistorySize),
        "history_limit" => Number(pane.HistoryLimit),
        "history_bytes" => "0",
        "alternate_on" => "0",
        _ => null,
    };

    private static string? WindowVariable(string name, TmuxWindow w) => name switch
    {
        "window_id" => WindowId(w),
        "window_index" => Number(w.Index),
        "window_name" => w.Name,
        "window_active" => Flag(w.IsCurrent),
        "window_panes" => Number(w.Panes.Count),
        "window_width" => Number(w.Width),
        "window_height" => Number(w.Height),
        "window_zoomed_flag" => Flag(w.Zoomed),
        "window_flags" => (w.IsCurrent ? "*" : "") + (w.Zoomed ? "Z" : ""),
        "window_bigger" or "window_offset_x" or "window_offset_y" or "window_linked" or "window_silence_flag" or "window_bell_flag" or "window_activity_flag" => "0",
        "window_layout" or "window_visible_layout" => "",
        _ => null,
    };

    private string? SessionVariable(string name) => name switch
    {
        "session_name" => Name,
        "session_id" => "$0",
        "session_windows" => Number(Windows.Count),
        "session_attached" => "1",
        "session_many_attached" or "session_grouped" => "0",
        "session_path" => "",
        _ => null,
    };

    private string? ClientVariable(string name, TmuxWindow w) => name switch
    {
        "client_termtype" => TermType,
        "client_termname" => "xterm-256color",
        "client_termfeatures" => "256,RGB,bpaste,clipboard,ccolour,cstyle,extkeys,focus,hyperlinks,mouse,overline,rectfill,sixel,strikethrough,sync,title,usstyle",
        "client_tty" => Current.TtyName ?? "",
        "client_name" => Current.TtyName ?? "erikterm",
        "client_width" => Number(w.Width),
        "client_height" => Number(w.Height),
        "client_control_mode" or "client_readonly" or "client_prefix" => "0",
        "client_session" => Name,
        "client_utf8" => "1",
        _ => null,
    };

    private string? ServerVariable(string name) => name switch
    {
        "host" => Environment.MachineName,
        "host_short" => Environment.MachineName.Split('.')[0],
        "pid" => Number(ServerPid),
        "version" => VersionNumber,
        "socket_path" => Socket,
        _ => null,
    };

    private string? OptionVariable(string name)
    {
        if (!Options.TryGetValue(name, out string? v)) return null;
        return v switch { "on" => "1", "off" => "0", _ => v };
    }

    public string Expand(string format, PaneInfo pane) => TmuxFormat.Expand(format, Variables(pane));

    /// <summary>
    /// A process name (<c>/proc/PID/comm</c>) that is tmux: "tmux", or "tmux: client" / "tmux: server" as tmux 3.x
    /// renames itself. (The shim calls itself "erikterm-tmux".)
    /// </summary>
    public static bool IsTmuxProcessName(string? name) =>
        name is not null && (name == "tmux" || name.StartsWith("tmux: ", StringComparison.Ordinal));

    /// <summary>
    /// A foreground "tmux" whose executable is <paramref name="self"/> (this erikterm): the shim, not real tmux. False
    /// when this erikterm's path isn't known.
    /// </summary>
    public static bool IsShimExecutable(string executable, string? self) =>
        self is not null && string.Equals(Path.GetFullPath(executable), Path.GetFullPath(self), StringComparison.Ordinal);

    /// <summary>A short name for a pane started with <paramref name="argv"/>: Claude Code's --agent-name, else the program.</summary>
    public static string? TitleFromCommand(IReadOnlyList<string> argv)
    {
        string line = string.Join(' ', argv);
        int at = line.IndexOf("--agent-name ", StringComparison.Ordinal);
        if (at >= 0)
        {
            string rest = line[(at + "--agent-name ".Length)..].TrimStart();
            string name = rest.StartsWith('\'') ? rest[1..Math.Max(1, rest.IndexOf('\'', 1))] : rest.Split(' ')[0];
            if (name.Length > 0) return name;
        }
        return null;
    }

    private static string? CwdPath(string uri) => ErikTerm.App.Panes.PaneCwd.FromOsc7(uri); // a remote host's path is no local path
}

/// <summary>What the emulation answers to show-options (and option names in formats). set-option is accepted and ignored.</summary>
internal static class TmuxOptions
{
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        // Claude Code reads these: images (allow-passthrough), focus tracking and wheel scrolling hints.
        ["allow-passthrough"] = "on",
        ["focus-events"] = "on",
        ["mouse"] = "on",
        ["prefix"] = "C-b",
        ["prefix2"] = "None",
        ["status"] = "off",
        ["status-position"] = "bottom",
        ["base-index"] = "0",
        ["pane-base-index"] = "0",
        ["escape-time"] = "0",
        ["history-limit"] = "10000",
        ["default-terminal"] = "xterm-256color",
        ["default-shell"] = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
        ["set-clipboard"] = "on",
        ["extended-keys"] = "on",
        ["remain-on-exit"] = "off",
        ["automatic-rename"] = "on",
        ["renumber-windows"] = "off",
        ["mode-keys"] = "emacs",
        ["status-keys"] = "emacs",
        ["display-time"] = "750",
        ["pane-border-status"] = "off",
        ["main-pane-width"] = "50%",
        ["main-pane-height"] = "50%",
        ["terminal-features"] = "xterm*:clipboard:ccolour:cstyle:focus:title,*:RGB",
    };
}
