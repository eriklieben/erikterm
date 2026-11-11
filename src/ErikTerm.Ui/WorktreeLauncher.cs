using System.Text;

namespace ErikTerm.Ui;

/// <summary>
/// A coding agent the launcher can start. <see cref="PromptArgs"/> is how it takes a first prompt: the arguments added
/// after the user's own, where an element holding <c>{prompt}</c> gets the text. Empty: it can't take one.
/// </summary>
public sealed record WorktreeAgent(string Key, string Name, string Command, IReadOnlyList<string> Args, IReadOnlyList<string> PromptArgs);

/// <summary>Where a launched agent opens: a new tab, or a split next to the focused pane.</summary>
public enum WorktreePlacement { Tab, Split }

/// <summary>What the config says about one agent (<c>[worktree_agents.NAME]</c>); null keeps the built-in value.</summary>
public sealed record WorktreeAgentOverride(string Key, string? Name, string? Command, IReadOnlyList<string>? Args, IReadOnlyList<string>? PromptArgs);

/// <summary>
/// The pure part of "New Agent Worktree": agents, argv construction, branch naming and the directory template. No git,
/// no window, so later worktree features can share it.
/// </summary>
public static class WorktreeLauncher
{
    public const string PromptPlaceholder = "{prompt}";
    public const string DefaultDirTemplate = "../{repo}.worktrees/{name}";
    /// <summary>Well below Linux's 128 KiB per argument and Windows' 32 KiB command line.</summary>
    public const int MaxPromptChars = 16 * 1024;
    private const int MaxSlugChars = 48;

    /// <summary>
    /// How the built-in agents take a first prompt, checked against each CLI:
    /// <list type="bullet">
    /// <item>Claude Code 2.1 (<c>claude --help</c>: <c>[options] [command] [prompt]</c>, interactive by default). Its
    /// parser is commander, which ends options at <c>--</c>.</item>
    /// <item>Codex 0.167 (<c>codex --help</c>: <c>[OPTIONS] [PROMPT]</c>). Its parser is clap, whose own error message
    /// suggests <c>-- --value</c>.</item>
    /// <item>GitHub Copilot CLI (not installed here; from the docs,
    /// https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference):
    /// <c>-i PROMPT, --interactive=PROMPT</c> "Start an interactive session and automatically execute this prompt".
    /// The <c>=</c> form is one argument, so a prompt starting with <c>-</c> can't be read as a flag.</item>
    /// </list>
    /// Flags such as <c>--yolo</c> are never translated between agents: Codex accepts it, Claude Code doesn't.
    /// </summary>
    public static readonly IReadOnlyList<WorktreeAgent> BuiltIn =
    [
        new("claude", "Claude Code", "claude", [], ["--", PromptPlaceholder]),
        new("copilot", "GitHub Copilot", "copilot", [], ["--interactive=" + PromptPlaceholder]),
        new("codex", "Codex", "codex", [], ["--", PromptPlaceholder]),
    ];

    // ---- Agents --------------------------------------------------------------------------------

    /// <summary>The built-in agents with the config on top; new names are added after them, an empty command removes one.</summary>
    public static List<WorktreeAgent> MergeAgents(IEnumerable<WorktreeAgentOverride> overrides)
    {
        var agents = new List<WorktreeAgent>(BuiltIn);
        foreach (var o in overrides)
        {
            int at = agents.FindIndex(a => a.Key == o.Key);
            var old = at >= 0 ? agents[at] : new WorktreeAgent(o.Key, o.Key, o.Key, [], ["--", PromptPlaceholder]);
            var merged = new WorktreeAgent(o.Key, o.Name ?? old.Name, o.Command ?? old.Command, o.Args ?? old.Args, o.PromptArgs ?? old.PromptArgs);
            if (at >= 0) agents[at] = merged; else agents.Add(merged);
        }
        agents.RemoveAll(a => a.Command.Length == 0);
        return agents;
    }

    /// <summary>
    /// The agents that can be started here. On Windows a .cmd/.bat shim is never offered: cmd.exe would parse the
    /// prompt again as a command line.
    /// </summary>
    public static List<WorktreeAgent> Available(IEnumerable<WorktreeAgent> agents, Func<string, string?> find, bool windows) =>
        agents.Where(a => find(a.Command) is { } path && !(windows && IsBatchFile(path))).ToList();

    private static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>The argv that starts <paramref name="agent"/>: its command, exactly the arguments shown, then the prompt (none when empty).</summary>
    public static string[] AgentArgv(WorktreeAgent agent, IReadOnlyList<string> arguments, string prompt)
    {
        var argv = new List<string>(arguments.Count + agent.PromptArgs.Count + 1) { agent.Command };
        argv.AddRange(arguments);
        if (prompt.Length == 0) return [.. argv];
        foreach (string template in agent.PromptArgs)
            argv.Add(template == PromptPlaceholder ? GuardBareWord(prompt) : template.Replace(PromptPlaceholder, prompt, StringComparison.Ordinal));
        return [.. argv];
    }

    /// <summary>
    /// A one-word prompt gets a trailing space. commander (Claude Code) looks up its first operand as a subcommand even
    /// after <c>--</c>, so a prompt of just "update" or "doctor" would run that command instead of starting a session.
    /// </summary>
    private static string GuardBareWord(string prompt) => prompt.Any(char.IsWhiteSpace) ? prompt : prompt + " ";

    public static bool TakesPrompt(WorktreeAgent agent) => agent.PromptArgs.Any(a => a.Contains(PromptPlaceholder, StringComparison.Ordinal));

    /// <summary>True for flags that turn off an agent's permission checks (the overlay warns while they are in the field).</summary>
    public static bool BypassesPermissions(string arguments) =>
        arguments.Contains("dangerously", StringComparison.OrdinalIgnoreCase) || arguments.Contains("--yolo", StringComparison.OrdinalIgnoreCase)
        || arguments.Contains("--allow-all", StringComparison.OrdinalIgnoreCase);

    /// <summary>worktree_open_in: "split" opens next to the focused pane; anything else a new tab.</summary>
    public static WorktreePlacement ParsePlacement(string? text) =>
        string.Equals(text?.Trim(), "split", StringComparison.OrdinalIgnoreCase) ? WorktreePlacement.Split : WorktreePlacement.Tab;

    /// <summary>The configured place, or (Alt+Enter) the other one for this one launch.</summary>
    public static WorktreePlacement ChoosePlacement(WorktreePlacement configured, bool alternate)
    {
        if (!alternate) return configured;
        return configured == WorktreePlacement.Tab ? WorktreePlacement.Split : WorktreePlacement.Tab;
    }

    /// <summary>The setup command for a repository: its own entry, else the <c>*</c> entry, else none.</summary>
    public static string? SetupFor(IReadOnlyDictionary<string, string> setup, string repo) =>
        setup.TryGetValue(repo, out var own) ? own : setup.GetValueOrDefault("*");

    /// <summary>A setup command runs through the platform's shell: it is the user's own command line from the config.</summary>
    public static string[] SetupArgv(string setup, bool windows) => windows ? ["cmd.exe", "/c", setup] : ["/bin/sh", "-c", setup];

    // ---- Prompt and arguments ----------------------------------------------------------------------

    /// <summary>
    /// The prompt on one line: line breaks and tabs become spaces; control characters and bidirectional overrides (which
    /// could make the preview lie about what runs) are dropped; capped at <see cref="MaxPromptChars"/>.
    /// </summary>
    public static string SanitizePrompt(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\n' or '\r' or '\t' or '\u2028' or '\u2029') sb.Append(' ');
            else if (!char.IsControl(c) && c is not (>= '\u202A' and <= '\u202E') and not (>= '\u2066' and <= '\u2069')) sb.Append(c);
        }
        string prompt = sb.ToString().Trim();
        if (prompt.Length <= MaxPromptChars) return prompt;
        int end = char.IsHighSurrogate(prompt[MaxPromptChars - 1]) ? MaxPromptChars - 1 : MaxPromptChars;
        return prompt[..end].TrimEnd();
    }

    /// <summary>
    /// Splits the arguments field into argv like a shell would (never through one): whitespace separates, '…' is literal,
    /// "…" and bare text take a backslash before a quote, a backslash or (bare) whitespace. Any other backslash is kept,
    /// so <c>C:\dir</c> works. False with <paramref name="error"/> on an unclosed quote or a control character.
    /// </summary>
    public static bool TryParseArguments(string text, out List<string> argv, out string? error)
    {
        argv = [];
        error = null;
        var words = new ArgumentWords(argv);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i++];
            if (char.IsControl(c)) { error = "Arguments can't hold control characters"; return false; }
            if (words.Quote != '\'' && c == '\\' && i < text.Length && IsEscapable(text[i], words.Quote == '"')) words.Append(text[i++]);
            else words.Take(c);
        }
        if (words.Quote != '\0') { error = $"Unclosed {words.Quote} in the arguments"; return false; }
        words.EndWord();
        return true;
    }

    /// <summary>The word being read and the quote it is in ('\0' outside quotes).</summary>
    private sealed class ArgumentWords(List<string> argv)
    {
        private readonly StringBuilder _word = new();
        private bool _inWord;

        public char Quote { get; private set; }

        public void Append(char c)
        {
            _word.Append(c);
            _inWord = true;
        }

        public void Take(char c)
        {
            if (Quote != '\0')
            {
                if (c == Quote) Quote = '\0'; else _word.Append(c);
            }
            else if (c is '\'' or '"') { Quote = c; _inWord = true; }
            else if (char.IsWhiteSpace(c)) EndWord();
            else Append(c);
        }

        public void EndWord()
        {
            if (_inWord) argv.Add(_word.ToString());
            _word.Clear();
            _inWord = false;
        }
    }

    private static bool IsEscapable(char c, bool inDoubleQuotes) => c is '"' or '\\' || (!inDoubleQuotes && (c == '\'' || char.IsWhiteSpace(c)));

    /// <summary>One word for the preview and the arguments field; <see cref="TryParseArguments"/> reads it back unchanged.</summary>
    public static string QuoteArgument(string s)
    {
        if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or '@' or '%' or '=' or ':')) return s;
        return "'" + s.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
    }

    public static string JoinArguments(IEnumerable<string> argv) => string.Join(' ', argv.Select(QuoteArgument));

    // ---- Names ---------------------------------------------------------------------------------

    /// <summary>A branch name from a prompt: lower-case ASCII words joined by '-', cut at a word; "" when nothing is left.</summary>
    public static string Slug(string prompt)
    {
        var sb = new StringBuilder(MaxSlugChars);
        foreach (char c in prompt)
        {
            if (sb.Length > MaxSlugChars) break;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string slug = sb.ToString();
        if (slug.Length > MaxSlugChars)
        {
            int cut = slug.LastIndexOf('-', MaxSlugChars);
            slug = cut > 0 ? slug[..cut] : slug[..MaxSlugChars];
        }
        return slug.TrimEnd('-');
    }

    private static readonly string[] NameFirst =
    [
        "amber", "autumn", "brave", "bright", "calm", "clever", "cosmic", "crisp", "curious", "dancing", "dapper", "eager",
        "floating", "flying", "frosty", "gentle", "golden", "happy", "humble", "jolly", "lively", "lucky", "mellow", "misty",
        "nimble", "patient", "plucky", "quiet", "rapid", "rustic", "silver", "sleepy", "snowy", "spry", "steady", "sunny",
        "swift", "tidy", "velvet", "wandering",
    ];

    private static readonly string[] NameSecond =
    [
        "anchor", "badger", "beacon", "boat", "bridge", "canyon", "cedar", "comet", "compass", "falcon", "fern", "forest",
        "fox", "garden", "harbor", "heron", "island", "kettle", "lantern", "maple", "meadow", "moon", "otter", "owl",
        "panda", "pebble", "pine", "planet", "raven", "river", "rocket", "sparrow", "summit", "tiger", "tulip", "valley",
        "walnut", "willow", "wolf", "zephyr",
    ];

    private static readonly string[] NameThird =
    [
        "arc", "bloom", "climb", "dash", "dive", "drift", "flip", "flow", "glide", "glow", "hop", "jump", "kick", "lift",
        "loop", "march", "orbit", "pivot", "quest", "race", "reach", "ride", "rise", "roam", "roll", "run", "sail", "skip",
        "slide", "soar", "spin", "sprint", "surf", "swing", "trek", "turn", "twist", "wander", "wave", "zoom",
    ];

    /// <summary>
    /// A name that's easy to remember and say, like <c>floating-boat-lift-a23fd</c>, for when the prompt can't name the
    /// branch. The same seed always gives the same name, so it holds still while the form is being filled in.
    /// </summary>
    public static string GeneratedName(int seed)
    {
#pragma warning disable S2245 // a friendly branch name, nothing secret; the fixed seed is the point, so the name holds still while the form is filled in
        var random = new Random(seed);
#pragma warning restore S2245
        string first = NameFirst[random.Next(NameFirst.Length)];
        string second = NameSecond[random.Next(NameSecond.Length)];
        string third = NameThird[random.Next(NameThird.Length)];
        return $"{first}-{second}-{third}-{random.Next(0x100000):x5}";
    }

    /// <summary><paramref name="name"/>, or the first of name-2, name-3, … that isn't taken.</summary>
    public static string UniqueName(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{name}-{i}";
            if (!taken(candidate)) return candidate;
        }
        return name;
    }

    /// <summary>
    /// Why a name can't be a branch (git's check-ref-format rules, for feedback while typing), or null. git itself has
    /// the last word when the worktree is created. A leading '-' and @{…} are refused here so they never reach git.
    /// </summary>
    public static string? BranchProblem(string name)
    {
        if (name.Length == 0) return "Enter a branch name";
        if (name[0] == '-') return "A branch name can't start with -";
        if (name == "HEAD" || name == "@") return $"\"{name}\" can't be a branch name";
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal))
            return "A branch name can't contain .., // or @{";
        if (IllegalBranchChar(name) is { } problem) return problem;
        if (name[^1] is '/' or '.' || name[0] == '/') return "A branch name can't start or end with /, or end with .";
        if (name.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))
            return "A part of a branch name can't start with . or end with .lock";
        return null;
    }

    private static string? IllegalBranchChar(string name)
    {
        int at = Array.FindIndex(name.ToCharArray(), IsIllegalInBranch);
        if (at < 0) return null;
        char shown = char.IsControl(name[at]) ? '?' : name[at];
        return $"A branch name can't contain '{shown}'";
    }

    private static bool IsIllegalInBranch(char c) =>
        char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\';

    /// <summary>Why a base can't be used, or null. Only a shape check: git resolves it when the worktree is created.</summary>
    public static string? BaseProblem(string @base)
    {
        if (@base.Length == 0) return "Enter a base branch or commit";
        if (@base[0] == '-') return "A base can't start with -";
        return @base.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? "A base can't contain spaces or control characters" : null;
    }

    /// <summary>The repository's default branch: origin/HEAD's target, else main, else master, else HEAD.</summary>
    public static string ChooseBase(string? originHead, bool hasMain, bool hasMaster)
    {
        const string prefix = "refs/remotes/";
        string head = originHead?.Trim() ?? "";
        if (head.StartsWith(prefix, StringComparison.Ordinal) && head.Length > prefix.Length) return head[prefix.Length..];
        if (hasMain) return "main";
        return hasMaster ? "master" : "HEAD";
    }

    /// <summary>A tab color no open tab uses, starting at one derived from the name; any color when all are in use.</summary>
    public static string ChooseTabColor(IEnumerable<string?> inUse, IReadOnlyList<string> offered, string name)
    {
        uint hash = 2166136261;
        foreach (char c in name) hash = (hash ^ c) * 16777619;
        var used = new HashSet<string?>(inUse);
        int start = (int)(hash % (uint)offered.Count);
        for (int i = 0; i < offered.Count; i++)
        {
            string candidate = offered[(start + i) % offered.Count];
            if (!used.Contains(candidate)) return candidate;
        }
        return offered[start];
    }

    // ---- Directories ---------------------------------------------------------------------------

    /// <summary>
    /// The main worktree's root from <c>git rev-parse --git-common-dir</c> (absolute), the same from every linked worktree
    /// and subdirectory. A submodule keeps its git directory under the superproject's: there the work tree is the root.
    /// A bare repository has no work tree, so its directory is the root.
    /// </summary>
    public static string MainRoot(string commonDir, string? topLevel)
    {
        string common = commonDir.Replace('\\', '/').TrimEnd('/');
        if (common.EndsWith("/.git", StringComparison.Ordinal)) return common[..^5];
        if (common.Contains("/.git/modules/", StringComparison.Ordinal) && topLevel is { Length: > 0 }) return topLevel.TrimEnd('/', '\\');
        return common;
    }

    /// <summary>The repository's name: the root directory's, without a bare repository's .git.</summary>
    public static string RepoName(string mainRoot)
    {
        string name = Path.GetFileName(mainRoot.Replace('\\', '/').TrimEnd('/'));
        return name.EndsWith(".git", StringComparison.Ordinal) && name.Length > 4 ? name[..^4] : name;
    }

    /// <summary>A branch as one directory name: "feature/x" is "feature-x", and characters Windows refuses go too.</summary>
    public static string DirectoryName(string branch)
    {
        var sb = new StringBuilder(branch.Length);
        foreach (char c in branch) sb.Append(c is '/' or '\\' or ':' or '"' or '<' or '>' or '|' or '?' or '*' ? '-' : c);
        return sb.ToString();
    }

    /// <summary>
    /// The new worktree's directory: <c>{repo}</c> and <c>{name}</c> filled in (in one pass, so a repository called
    /// "{name}" stays literal), <c>~/</c> expanded, and a relative template taken from the main worktree's root.
    /// </summary>
    public static string ExpandDirectory(string template, string repo, string branch, string mainRoot, string home)
    {
        var sb = new StringBuilder(template.Length + 32);
        int i = 0;
        while (i < template.Length)
        {
            if (string.CompareOrdinal(template, i, "{repo}", 0, 6) == 0) { sb.Append(repo); i += 6; }
            else if (string.CompareOrdinal(template, i, "{name}", 0, 6) == 0) { sb.Append(DirectoryName(branch)); i += 6; }
            else sb.Append(template[i++]);
        }
        string path = sb.ToString();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)) path = home + path[1..];
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(mainRoot, path));
    }
}

/// <summary>What one step of a launch starts in the pane; <see cref="Message"/> is printed there first.</summary>
public sealed record WorktreeLaunchStep(IReadOnlyList<string> Argv, string? Message, bool IsLast);

/// <summary>
/// The programs a worktree tab runs, one after the other in the same pane: the setup command, the agent, then a shell.
/// erikterm starts each as the pane's own process when the previous one exits (no shell string, no typing into a shell
/// that may still be starting, the same on every platform), so the agent is never quoted for a shell and the tab can't
/// close when it exits. A failed setup skips the agent. Without an agent the shell comes right after the setup and is the
/// last thing the pane runs.
/// </summary>
public sealed class WorktreeLaunchPlan(IReadOnlyList<string>? setup, IReadOnlyList<string>? agent, IReadOnlyList<string> shell, string place = "this worktree")
{
    private enum Stage { Setup, Agent, Shell, Done }

    private Stage _stage = StartStage(setup is not null, agent is not null);

    private static Stage StartStage(bool hasSetup, bool hasAgent)
    {
        if (hasSetup) return Stage.Setup;
        return hasAgent ? Stage.Agent : Stage.Shell;
    }

    /// <summary>What the pane starts with.</summary>
    public IReadOnlyList<string> First => _stage == Stage.Setup ? setup! : agent ?? shell;

    /// <summary>What comes after the current program exited with <paramref name="exitCode"/>; null when the launch is over.</summary>
    public WorktreeLaunchStep? Next(int? exitCode)
    {
        switch (_stage)
        {
            case Stage.Setup when exitCode == 0 && agent is null:
                _stage = Stage.Shell;
                return new WorktreeLaunchStep(shell, null, true);
            case Stage.Setup when exitCode == 0:
                _stage = Stage.Agent;
                return new WorktreeLaunchStep(agent!, null, false);
            case Stage.Setup:
                _stage = Stage.Shell;
                return new WorktreeLaunchStep(shell, $"\r\n\e[31m[setup failed ({Describe(exitCode)}){(agent is null ? "" : ": the agent was not started")}]\e[0m\r\n", true);
            case Stage.Agent:
                _stage = Stage.Shell;
                return new WorktreeLaunchStep(shell, $"\r\n\e[2m[agent exited ({Describe(exitCode)}): a shell in {place}]\e[0m\r\n", true);
            default:
                _stage = Stage.Done;
                return null;
        }
    }

    private static string Describe(int? exitCode) => exitCode is { } code ? $"code {code}" : "unknown status";
}
