using System.Globalization;
using ErikTerm.App.Ipc;

namespace ErikTerm.App.Cli;

public sealed class CliUsageException(string message) : Exception(message);

/// <summary>A parsed <c>erikterm cli ...</c> command line.</summary>
internal sealed class CliInvocation
{
    public required IpcRequest Request { get; init; }
    /// <summary>--socket; null = $ERIKTERM_SOCKET or discovery.</summary>
    public string? Socket { get; init; }
    /// <summary>list --format: "table" or "json".</summary>
    public string Format { get; init; } = "table";
    /// <summary>send-text without TEXT: the text comes from stdin.</summary>
    public bool ReadStdin { get; init; }
    public bool Help { get; init; }
    /// <summary>set-agent-status --hook-input: stdin is a Claude Code hook's JSON (message / prompt / tool).</summary>
    public bool HookInput { get; init; }
    /// <summary>statusline --passthrough CMD: run the user's own status line command and print its output.</summary>
    public string? Passthrough { get; init; }
}

/// <summary>
/// Parses <c>erikterm cli</c> arguments (everything after "cli"). Subcommand and flag names follow
/// <c>wezterm cli</c>; flags may come before or after positional arguments, as <c>--flag value</c> or
/// <c>--flag=value</c>, and everything after <c>--</c> is the program to run.
/// </summary>
internal static class CliParser
{
    public const string Usage = """
        usage: erikterm cli [--socket PATH] <subcommand> [options]
               erikterm cli --version                 print the version and commit

        Controls a running erikterm. Inside an erikterm pane it targets that pane and its window
        ($ERIKTERM_SOCKET, $ERIKTERM_PANE); elsewhere the most recently focused window of the most
        recently focused instance. --pane-id ID picks another pane, --window-id ID another window
        (ids from `list`; one instance can have several windows).

          list [--format table|json]                  panes of every tab of every window
          split-pane [--left|--right|--top|--bottom] [--percent N] [--cwd DIR] [--pane-id ID] [-- PROG ARGS...]
                                                      split a pane (default --bottom); prints the new pane id
          spawn [--cwd DIR] [--pane-id ID] [--window-id ID | --new-window] [-- PROG ARGS...]
                                                      open a new tab (in that window), or a new window;
                                                      prints its pane id
          activate-window --window-id ID              raise / focus a window (Wayland: the compositor decides)
          send-text [--pane-id ID] [--no-paste] [TEXT]
                                                      type TEXT (or stdin), as a bracketed paste unless --no-paste
          get-text [--pane-id ID] [--start-line N] [--end-line N] [--escapes]
                                                      print the screen (negative lines reach into the scrollback)
          activate-pane [--pane-id ID]                focus a pane (switching tabs if needed)
          activate-pane-direction [--pane-id ID] Left|Right|Up|Down
          get-pane-direction [--pane-id ID] Left|Right|Up|Down
                                                      print the neighbouring pane's id (nothing at an edge)
          adjust-pane-size [--pane-id ID] [--amount N] Left|Right|Up|Down
                                                      move the pane's edge N cells (default 1)
          zoom-pane [--pane-id ID] [--zoom|--unzoom|--toggle]
          kill-pane [--pane-id ID]
          activate-tab (--tab-index N | --tab-relative N | --tab-id ID)
                                                      tab index is 0-based, negative counts from the end
          swap-pane [--pane-id ID] [--with ID | --previous]
                                                      trade places with another pane (default: the next one)
          rotate-panes [--pane-id ID | --tab-id ID] [--counter-clockwise]
                                                      move every pane of the tab one place on
          select-layout [--pane-id ID | --tab-id ID] even-horizontal|even-vertical|main-vertical|main-horizontal|tiled|even
                                                      arrange the tab's panes (even: equal sizes, same splits)
          move-pane-to-new-tab [--pane-id ID]         take a pane out of its split into a tab of its own
          set-tab-title [--pane-id ID | --tab-id ID] TITLE
          set-tab-color [--pane-id ID | --tab-id ID] COLOR
                                                      red, yellow, green, cyan, blue, magenta (the theme's),
                                                      #rrggbb, or none
          set-window-title TITLE
          reload-config
          agents [--format table|json]                coding agents in every tab, waiting first
          set-agent-status [--pane-id ID] --state waiting|working|done|idle|clear [--message TEXT] [--kind KIND] [--hook-input]
                                                      report an agent's state (for Claude Code hooks; --hook-input
                                                      reads the hook's JSON from stdin for the message)
          new-worktree [--cwd DIR] [--branch NAME] [--base REF] [--agent KEY [--prompt TEXT] [--agent-args ARGS]]
                       [--tab|--split] [--background] [--pane-id ID] [--window-id ID]
                                                      create a git worktree and open it like New Agent Worktree (a shell
                                                      without --agent); looks in --cwd, else the pane given by --pane-id,
                                                      else this directory; prints the new pane id
          wait-for-state [--pane-id ID] --state STATE[,STATE...] [--timeout DURATION]
                                                      block until the pane's agent is idle, working, waiting, done or
                                                      none, or the pane's program is gone (exit); prints the state.
                                                      idle counts a finished turn you haven't seen; done also counts a
                                                      turn that ended while waiting, so use it right after a launch.
                                                      DURATION: 30, 30s, 500ms, 5m, 1h (default: no limit)
          statusline [--pane-id ID] [--passthrough CMD]
                                                      Claude Code status line: reads its JSON on stdin, tells erikterm
                                                      the model, cost and context use, prints a status line

        Exit status: 0 on success, 1 on any error (with a message on stderr). wait-for-state also exits 2 on
        timeout and 3 when the pane is unknown or closed.
        """;

    private static readonly HashSet<string> ValueFlags =
        ["socket", "pane-id", "percent", "cwd", "start-line", "end-line", "amount", "tab-index", "tab-relative", "tab-id", "format", "window-id",
         "state", "message", "kind", "passthrough", "with", "branch", "base", "agent", "prompt", "agent-args", "timeout"];

    private static readonly HashSet<string> BoolFlags =
        ["left", "right", "top", "bottom", "horizontal", "no-paste", "escapes", "zoom", "unzoom", "toggle", "help", "new-window", "hook-input",
         "previous", "counter-clockwise", "tab", "split", "background"];

    /// <summary>What a subcommand adds to the request once its arguments have passed <see cref="CheckAgainstSpec"/>.</summary>
    private delegate void FillRequest(CommandLine line, IpcRequest request);

    private readonly record struct CommandSpec(string[] Flags, int MinArgs, int MaxArgs, bool Program, FillRequest? Fill);

    /// <summary>The arguments read so far, and what <see cref="FillRequest"/> rules decide beyond the request itself.</summary>
    private sealed class CommandLine
    {
        public string? Subcommand;
        public readonly Dictionary<string, string?> Flags = new(StringComparer.Ordinal);
        public readonly List<string> Positionals = [];
        public string[]? Program;
        public string Format = "table";
        public bool ReadStdin;

        public bool Has(string name) => Flags.ContainsKey(name);

        public string? Value(string name) => Flags.GetValueOrDefault(name);

        public int? Int(string name)
        {
            if (!Flags.TryGetValue(name, out var v)) return null;
            return int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)
                ? n
                : throw new CliUsageException($"--{name}: expected a whole number, got '{v}'");
        }
    }

    /// <summary>Flags each subcommand accepts (besides --socket and --help), its positional count, whether it takes "-- PROG" and what it adds to the request.</summary>
    private static readonly Dictionary<string, CommandSpec> Commands = new()
    {
        ["list"] = new(["format"], 0, 0, false, FillFormat),
        ["split-pane"] = new(["pane-id", "left", "right", "top", "bottom", "horizontal", "percent", "cwd"], 0, 0, true, FillSplit),
        ["spawn"] = new(["pane-id", "cwd", "window-id", "new-window"], 0, 0, true, FillSpawn),
        ["activate-window"] = new(["window-id"], 0, 0, false, FillActivateWindow),
        ["send-text"] = new(["pane-id", "no-paste"], 0, 1, false, FillSendText),
        ["get-text"] = new(["pane-id", "start-line", "end-line", "escapes"], 0, 0, false, FillGetText),
        ["activate-pane"] = new(["pane-id"], 0, 0, false, null),
        ["activate-pane-direction"] = new(["pane-id"], 1, 1, false, FillDirection),
        ["get-pane-direction"] = new(["pane-id"], 1, 1, false, FillDirection),
        ["adjust-pane-size"] = new(["pane-id", "amount"], 1, 1, false, FillAdjustSize),
        ["zoom-pane"] = new(["pane-id", "zoom", "unzoom", "toggle"], 0, 0, false, FillZoom),
        ["kill-pane"] = new(["pane-id"], 0, 0, false, null),
        ["activate-tab"] = new(["pane-id", "tab-index", "tab-relative", "tab-id"], 0, 0, false, FillActivateTab),
        ["set-tab-title"] = new(["pane-id", "tab-id"], 1, 1, false, FillTitle),
        ["set-tab-color"] = new(["pane-id", "tab-id"], 1, 1, false, FillTabColor),
        ["set-window-title"] = new(["pane-id"], 1, 1, false, FillTitle),
        ["reload-config"] = new([], 0, 0, false, null),
        ["agents"] = new(["format"], 0, 0, false, FillFormat),
        ["set-agent-status"] = new(["pane-id", "state", "message", "kind", "hook-input"], 0, 1, false, FillAgentStatus),
        ["statusline"] = new(["pane-id", "passthrough"], 0, 0, false, FillStatusline),
        ["swap-pane"] = new(["pane-id", "with", "previous"], 0, 0, false, FillSwap),
        ["rotate-panes"] = new(["pane-id", "tab-id", "counter-clockwise"], 0, 0, false, FillRotate),
        ["select-layout"] = new(["pane-id", "tab-id"], 1, 1, false, FillLayout),
        ["move-pane-to-new-tab"] = new(["pane-id"], 0, 0, false, null),
        ["new-worktree"] = new(["pane-id", "window-id", "cwd", "branch", "base", "agent", "prompt", "agent-args", "tab", "split", "background"], 0, 0, false, FillNewWorktree),
        ["wait-for-state"] = new(["pane-id", "state", "timeout"], 0, 1, false, FillWaitForState),
    };

    /// <summary>select-layout's names (tmux's, plus even: the current splits with equal shares).</summary>
    public static readonly string[] Layouts = ["even-horizontal", "even-vertical", "main-vertical", "main-horizontal", "tiled", "even"];

    public static IEnumerable<string> CommandNames => Commands.Keys;

    /// <exception cref="CliUsageException">The arguments don't make sense; the message says why.</exception>
    public static CliInvocation Parse(IReadOnlyList<string> args)
    {
        var line = ReadArguments(args);
        string? socket = line.Value("socket");
        if (line.Has("help") || line.Subcommand is not { } sub)
            return new CliInvocation { Request = new IpcRequest { Command = line.Subcommand ?? "" }, Socket = socket, Help = true };
        if (!Commands.TryGetValue(sub, out var spec))
            throw new CliUsageException($"unknown subcommand '{sub}' (one of: {string.Join(", ", Commands.Keys)})");
        CheckAgainstSpec(sub, spec, line);

        var r = new IpcRequest { Command = sub, PaneId = line.Int("pane-id"), TabId = line.Int("tab-id"), WindowId = line.Int("window-id") };
        spec.Fill?.Invoke(line, r);
        return new CliInvocation
        {
            Request = r, Socket = socket, Format = line.Format, ReadStdin = line.ReadStdin,
            HookInput = line.Has("hook-input"), Passthrough = line.Value("passthrough"),
        };
    }

    private static CommandLine ReadArguments(IReadOnlyList<string> args)
    {
        var line = new CommandLine();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--")
            {
                line.Program = args.Skip(i + 1).ToArray();
                break;
            }
            if (a is "-h") line.Flags["help"] = null;
            else if (a.StartsWith("--", StringComparison.Ordinal)) ReadOption(args, ref i, line.Flags);
            else if (line.Subcommand is null) line.Subcommand = a;
            else line.Positionals.Add(a);
        }
        return line;
    }

    /// <summary>Records the option at <paramref name="i"/>; a value given as the next argument moves <paramref name="i"/> on to it.</summary>
    private static void ReadOption(IReadOnlyList<string> args, ref int i, Dictionary<string, string?> flags)
    {
        string a = args[i];
        int eq = a.IndexOf('=');
        string name = eq > 0 ? a[2..eq] : a[2..];
        string? inline = eq > 0 ? a[(eq + 1)..] : null;
        if (ValueFlags.Contains(name))
        {
            if (inline is null)
            {
                if (i + 1 >= args.Count) throw new CliUsageException($"--{name} needs a value");
                inline = args[++i];
            }
            flags[name] = inline;
        }
        else if (BoolFlags.Contains(name))
        {
            if (inline is not null) throw new CliUsageException($"--{name} takes no value");
            flags[name] = null;
        }
        else throw new CliUsageException($"unknown option '{a}'");
    }

    private static void CheckAgainstSpec(string sub, CommandSpec spec, CommandLine line)
    {
        if (line.Flags.Keys.FirstOrDefault(f => f != "socket" && !spec.Flags.Contains(f)) is { } unknown)
            throw new CliUsageException($"'{sub}' doesn't take --{unknown}");
        if (line.Positionals.Count < spec.MinArgs) throw new CliUsageException(MissingArgumentMessage(sub));
        if (line.Positionals.Count > spec.MaxArgs) throw new CliUsageException($"unexpected argument '{line.Positionals[spec.MaxArgs]}' for '{sub}'");
        if (line.Program is not null && !spec.Program) throw new CliUsageException($"'{sub}' doesn't run a program (unexpected '--')");
    }

    private static string MissingArgumentMessage(string sub)
    {
        if (sub.EndsWith("title", StringComparison.Ordinal)) return $"'{sub}' needs a TITLE";
        return sub switch
        {
            "set-tab-color" => $"'{sub}' needs a COLOR ({string.Join(", ", Config.TabColors.Offered)}, #rrggbb or none)",
            "select-layout" => $"'{sub}' needs a layout ({string.Join(", ", Layouts)})",
            _ => $"'{sub}' needs a direction (Left, Right, Up or Down)",
        };
    }

    // ---- What each subcommand adds to the request (the Fill column of Commands) --------------------------------

    private static void FillFormat(CommandLine line, IpcRequest _)
    {
        line.Format = line.Value("format") ?? "table";
        if (line.Format is not ("table" or "json")) throw new CliUsageException($"--format: expected table or json, got '{line.Format}'");
    }

    private static void FillAgentStatus(CommandLine line, IpcRequest r)
    {
        string? state = line.Value("state") ?? (line.Positionals.Count == 1 ? line.Positionals[0] : null);
        if (state is null) throw new CliUsageException("'set-agent-status' needs --state waiting|working|done|idle|clear");
        if (ErikTerm.Ui.AgentStatus.AgentStateText.Parse(state) is null)
            throw new CliUsageException($"--state: expected waiting, working, done, idle or clear, got '{state}'");
        r.State = state.Trim().ToLowerInvariant();
        r.Message = line.Value("message");
        r.Kind = line.Value("kind");
    }

    private static void FillStatusline(CommandLine _, IpcRequest r) => r.Command = "report-usage";

    private static void FillSplit(CommandLine line, IpcRequest r)
    {
        string[] sides = ["left", "right", "top", "bottom", "horizontal"];
        var given = sides.Where(line.Has).ToList();
        if (given.Count > 1) throw new CliUsageException("give only one of --left, --right, --top, --bottom");
        r.Direction = given.FirstOrDefault() switch
        {
            "left" => "Left",
            "right" or "horizontal" => "Right",
            "top" => "Up",
            _ => "Down",
        };
        r.Percent = line.Int("percent");
        if (r.Percent is < 1 or > 99) throw new CliUsageException("--percent must be between 1 and 99");
        r.Cwd = CwdFlag(line.Flags);
        r.Argv = line.Program is { Length: > 0 } ? line.Program : null;
    }

    private static void FillSpawn(CommandLine line, IpcRequest r)
    {
        r.Cwd = CwdFlag(line.Flags);
        r.Argv = line.Program is { Length: > 0 } ? line.Program : null;
        r.NewWindow = line.Has("new-window");
        if (r.NewWindow && r.WindowId is not null) throw new CliUsageException("give only one of --window-id, --new-window");
    }

    private static void FillActivateWindow(CommandLine _, IpcRequest r)
    {
        if (r.WindowId is null) throw new CliUsageException("'activate-window' needs --window-id ID");
    }

    private static void FillSendText(CommandLine line, IpcRequest r)
    {
        r.NoPaste = line.Has("no-paste");
        if (line.Positionals.Count == 1) r.Text = line.Positionals[0];
        else line.ReadStdin = true;
    }

    private static void FillGetText(CommandLine line, IpcRequest r)
    {
        r.StartLine = line.Int("start-line");
        r.EndLine = line.Int("end-line");
        r.Escapes = line.Has("escapes");
    }

    private static void FillDirection(CommandLine line, IpcRequest r) => r.Direction = Direction(line.Positionals[0]);

    private static void FillAdjustSize(CommandLine line, IpcRequest r)
    {
        r.Direction = Direction(line.Positionals[0]);
        r.Amount = line.Int("amount") ?? 1;
        if (r.Amount < 1) throw new CliUsageException("--amount must be at least 1");
    }

    private static void FillZoom(CommandLine line, IpcRequest r)
    {
        string[] modes = ["zoom", "unzoom", "toggle"];
        var given = modes.Where(line.Has).ToList();
        if (given.Count > 1) throw new CliUsageException("give only one of --zoom, --unzoom, --toggle");
        r.Zoom = given.FirstOrDefault() ?? "zoom";
    }

    private static void FillActivateTab(CommandLine line, IpcRequest r)
    {
        r.TabIndex = line.Int("tab-index");
        r.TabRelative = line.Int("tab-relative");
        int ways = (r.TabIndex is null ? 0 : 1) + (r.TabRelative is null ? 0 : 1) + (r.TabId is null ? 0 : 1);
        if (ways != 1) throw new CliUsageException("give exactly one of --tab-index, --tab-relative, --tab-id");
    }

    private static void FillTitle(CommandLine line, IpcRequest r) => r.Title = line.Positionals[0];

    private static void FillTabColor(CommandLine line, IpcRequest r)
    {
        string color = line.Positionals[0];
        if (!Config.TabColors.TryNormalize(color, out _))
            throw new CliUsageException($"'{color}' isn't a tab color (use {string.Join(", ", Config.TabColors.Offered)}, #rrggbb or none)");
        r.Color = color;
    }

    private static void FillSwap(CommandLine line, IpcRequest r)
    {
        r.OtherPaneId = line.Int("with");
        if (r.OtherPaneId is not null && line.Has("previous")) throw new CliUsageException("give only one of --with, --previous");
        r.Direction = line.Has("previous") ? "Previous" : "Next";
    }

    private static void FillRotate(CommandLine line, IpcRequest r) =>
        r.Direction = line.Has("counter-clockwise") ? "CounterClockwise" : "Clockwise";

    private static void FillLayout(CommandLine line, IpcRequest r)
    {
        r.Layout = line.Positionals[0].Trim().ToLowerInvariant();
        if (!Layouts.Contains(r.Layout)) throw new CliUsageException($"unknown layout '{line.Positionals[0]}' (one of: {string.Join(", ", Layouts)})");
    }

    private static void FillNewWorktree(CommandLine line, IpcRequest r)
    {
        if (line.Has("tab") && line.Has("split")) throw new CliUsageException("give only one of --tab, --split");
        if (line.Has("tab")) r.Placement = "tab";
        if (line.Has("split")) r.Placement = "split";
        r.Agent = line.Value("agent");
        r.Prompt = line.Value("prompt");
        r.AgentArguments = line.Value("agent-args");
        if (r.Agent is null && (r.Prompt ?? r.AgentArguments) is not null) throw new CliUsageException("--prompt and --agent-args need --agent");
        r.Branch = line.Value("branch");
        r.Base = line.Value("base");
        r.NoFocus = line.Has("background");
        r.Cwd = CwdFlag(line.Flags) ?? (line.Has("pane-id") ? null : CallerDirectory());
    }

    /// <summary>Where the command was run; null when that directory is gone (the target pane's is used instead).</summary>
    private static string? CallerDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void FillWaitForState(CommandLine line, IpcRequest r)
    {
        string? text = line.Value("state") ?? (line.Positionals.Count == 1 ? line.Positionals[0] : null);
        if (text is null) throw new CliUsageException($"'wait-for-state' needs --state ({WaitStates.Expected})");
        var states = WaitStates.Parse(text) ?? throw new CliUsageException($"--state: expected {WaitStates.Expected}, got '{text}'");
        r.State = string.Join(',', states);
        if (line.Value("timeout") is { } timeout) r.TimeoutMs = TimeoutMs(timeout);
    }

    /// <summary>"30" and "30s" are seconds; also "500ms", "5m", "1h".</summary>
    private static long TimeoutMs(string text)
    {
        string t = text.Trim();
        (double unit, string number) = t switch
        {
            _ when t.EndsWith("ms", StringComparison.Ordinal) => (1d, t[..^2]),
            _ when t.EndsWith('s') => (1000d, t[..^1]),
            _ when t.EndsWith('m') => (60_000d, t[..^1]),
            _ when t.EndsWith('h') => (3_600_000d, t[..^1]),
            _ => (1000d, t),
        };
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || value * unit > int.MaxValue)
            throw new CliUsageException($"--timeout: expected a duration such as 30, 30s, 500ms, 5m or 1h, got '{text}'");
        return (long)Math.Ceiling(value * unit);
    }

    /// <summary>Normalises a direction to "Left" / "Right" / "Up" / "Down" (case-insensitive).</summary>
    public static string Direction(string s) => s.ToLowerInvariant() switch
    {
        "left" => "Left",
        "right" => "Right",
        "up" => "Up",
        "down" => "Down",
        _ => throw new CliUsageException($"invalid direction '{s}' (expected Left, Right, Up or Down)"),
    };

    private static string? CwdFlag(Dictionary<string, string?> flags) =>
        flags.GetValueOrDefault("cwd") is { } cwd ? Path.GetFullPath(Config.SettingsLoader.ExpandHome(cwd)) : null;
}
