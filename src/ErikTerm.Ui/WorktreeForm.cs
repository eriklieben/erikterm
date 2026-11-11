namespace ErikTerm.Ui;

/// <summary>A text field: text, caret and the usual editing. <paramref name="multiline"/> keeps line breaks.</summary>
public sealed class TextInput(int maxChars, bool multiline = false)
{
    public string Text { get; private set; } = "";
    public int Caret { get; private set; }

    public void Set(string text)
    {
        Text = text.Length > maxChars ? text[..maxChars] : text;
        Caret = Text.Length;
    }

    /// <summary>Types or pastes <paramref name="text"/> at the caret; line breaks become spaces, other control characters go.</summary>
    public void Insert(string text)
    {
        var clean = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (multiline && c == '\n') clean.Append(c);
            else if (multiline && c == '\r') continue;
            else if (c is '\n' or '\r' or '\t') clean.Append(' ');
            else if (!char.IsControl(c)) clean.Append(c);
        }
        int room = maxChars - Text.Length;
        if (clean.Length > room) clean.Length = Math.Max(0, room);
        Text = Text.Insert(Caret, clean.ToString());
        Caret += clean.Length;
    }

    public void Backspace(bool word)
    {
        int from = word ? WordLeft() : Step(-1);
        Text = Text.Remove(from, Caret - from);
        Caret = from;
    }

    public void Delete()
    {
        if (Caret < Text.Length) Text = Text.Remove(Caret, Step(1) - Caret);
    }

    public void Left(bool word) => Caret = word ? WordLeft() : Step(-1);
    public void Right(bool word) => Caret = word ? WordRight() : Step(1);
    public void Home() => Caret = 0;
    public void End() => Caret = Text.Length;
    public void Clear() => Set("");
    public void MoveTo(int caret) => Caret = Math.Clamp(caret, 0, Text.Length);

    /// <summary>Moves the caret up or down a line (multi-line fields), keeping its column where the line allows.</summary>
    public void MoveLine(int delta)
    {
        int start = Caret == 0 ? 0 : Text.LastIndexOf('\n', Caret - 1) + 1;
        int column = Caret - start;
        if (delta < 0)
        {
            if (start == 0) return;
            int previous = start < 2 ? 0 : Text.LastIndexOf('\n', start - 2) + 1;
            Caret = Math.Min(previous + column, start - 1);
        }
        else
        {
            int end = Text.IndexOf('\n', Caret);
            if (end < 0) return;
            int nextEnd = Text.IndexOf('\n', end + 1);
            if (nextEnd < 0) nextEnd = Text.Length;
            Caret = Math.Min(end + 1 + column, nextEnd);
        }
    }

    /// <summary>One character from the caret, keeping a surrogate pair together.</summary>
    private int Step(int direction)
    {
        if (direction < 0)
        {
            if (Caret == 0) return 0;
            return Caret >= 2 && char.IsLowSurrogate(Text[Caret - 1]) && char.IsHighSurrogate(Text[Caret - 2]) ? Caret - 2 : Caret - 1;
        }
        if (Caret >= Text.Length) return Text.Length;
        return Caret + 1 < Text.Length && char.IsHighSurrogate(Text[Caret]) && char.IsLowSurrogate(Text[Caret + 1]) ? Caret + 2 : Caret + 1;
    }

    private int WordLeft()
    {
        int i = Caret;
        while (i > 0 && Text[i - 1] is ' ' or '\n') i--;
        while (i > 0 && Text[i - 1] is not (' ' or '\n')) i--;
        return i;
    }

    private int WordRight()
    {
        int i = Caret;
        while (i < Text.Length && Text[i] is ' ' or '\n') i++;
        while (i < Text.Length && Text[i] is not (' ' or '\n')) i++;
        return i;
    }
}

public enum WorktreeField { Prompt, Arguments, Branch, Base }

/// <summary>What the form knows when it opens.</summary>
public sealed class WorktreeFormOptions
{
    public required string Cwd { get; init; }
    public required string RepoName { get; init; }
    public required string MainRoot { get; init; }
    public required string Home { get; init; }
    public required string DirTemplate { get; init; }
    public required string DefaultBase { get; init; }
    public required IReadOnlyList<WorktreeAgent> Agents { get; init; }
    public required IReadOnlyCollection<string> Branches { get; init; }
    public required Func<string, bool> PathExists { get; init; }
    public string? Setup { get; init; }
    public string? LastAgent { get; init; }
    public IReadOnlyDictionary<string, string>? RememberedArguments { get; init; }
    /// <summary>Generated branch names for launches without a prompt.</summary>
    public int NameSeed { get; init; }
    /// <summary>worktree_open_in: Enter opens the agent here, Alt+Enter in the other place.</summary>
    public WorktreePlacement Placement { get; init; } = WorktreePlacement.Tab;
}

/// <summary>A launch the user confirmed: everything is settled and visible in the overlay.</summary>
public sealed record WorktreeRequest(string Branch, string Directory, string Base, WorktreeAgent Agent, string ArgumentsText, string[] AgentArgv, string? Setup, string Cwd);

/// <summary>
/// The overlay's state and rules, without drawing: which agent, what the fields hold, the suggested branch, the exact
/// command, and what is wrong. Arguments are per agent and only ever come from what the field shows.
/// </summary>
public sealed class WorktreeForm
{
    private const int PreviewPromptChars = 60;

    private readonly WorktreeFormOptions _options;
    private readonly HashSet<string> _taken;
    private readonly Dictionary<string, string> _arguments = [];
    private int _nameSeed;
    private bool _branchEdited;

    public TextInput Prompt { get; } = new(WorktreeLauncher.MaxPromptChars);
    public TextInput Arguments { get; } = new(4096);
    public TextInput Branch { get; } = new(200);
    public TextInput Base { get; } = new(200);

    public WorktreeField Focus { get; set; } = WorktreeField.Prompt;
    public int AgentIndex { get; private set; }
    public bool Busy { get; set; }
    /// <summary>The first problem that blocks launching; a failure from git while <see cref="Busy"/> was set shows here too.</summary>
    public string? Error { get; private set; }
    public string? Warning { get; private set; }
    public string Directory { get; private set; } = "";
    public string Preview { get; private set; } = "";
    public string RepoName => _options.RepoName;
    public string? Setup => _options.Setup;
    public IReadOnlyList<WorktreeAgent> Agents => _options.Agents;
    public WorktreeAgent? Agent => _options.Agents.Count > 0 ? _options.Agents[AgentIndex] : null;
    public WorktreePlacement Placement => _options.Placement;
    /// <summary>Where this launch opens: the configured place, or the other one with Alt+Enter.</summary>
    public WorktreePlacement PlacementFor(bool alternate) => WorktreeLauncher.ChoosePlacement(_options.Placement, alternate);

    public WorktreeForm(WorktreeFormOptions options)
    {
        _options = options;
        _taken = new HashSet<string>(options.Branches, StringComparer.OrdinalIgnoreCase);
        _nameSeed = options.NameSeed;
        int last = options.Agents.ToList().FindIndex(a => a.Key == options.LastAgent);
        AgentIndex = last >= 0 ? last : 0;
        Base.Set(options.DefaultBase);
        LoadArguments();
        Recompute();
    }

    public TextInput Field(WorktreeField field) => field switch
    {
        WorktreeField.Prompt => Prompt,
        WorktreeField.Arguments => Arguments,
        WorktreeField.Branch => Branch,
        _ => Base,
    };

    public void MoveFocus(int delta)
    {
        const int count = 4;
        Focus = (WorktreeField)(((int)Focus + delta + count) % count);
    }

    public void CycleAgent(int delta) => SelectAgent(AgentIndex + delta);

    public void SelectAgent(int index)
    {
        if (Agent is not { } agent) return;
        _arguments[agent.Key] = Arguments.Text;
        AgentIndex = (index % _options.Agents.Count + _options.Agents.Count) % _options.Agents.Count;
        LoadArguments();
        Recompute();
    }

    /// <summary>A field's text changed. Typing in the branch field stops the suggestion from following the prompt.</summary>
    public void Changed(WorktreeField field)
    {
        if (field == WorktreeField.Branch) _branchEdited = Branch.Text.Length > 0;
        Recompute();
    }

    private void LoadArguments()
    {
        if (Agent is not { } agent) return;
        if (_arguments.TryGetValue(agent.Key, out var edited)) Arguments.Set(edited);
        else if (_options.RememberedArguments?.TryGetValue(agent.Key, out var remembered) == true) Arguments.Set(remembered);
        else Arguments.Set(WorktreeLauncher.JoinArguments(agent.Args));
    }

    private string DirectoryFor(string branch) =>
        WorktreeLauncher.ExpandDirectory(_options.DirTemplate, _options.RepoName, branch, _options.MainRoot, _options.Home);

    /// <summary>The memorable name for this launch, or the next free one in the unlikely case it's taken.</summary>
    private string FreeGeneratedName()
    {
        for (int i = 0; i < 100; i++)
        {
            string name = WorktreeLauncher.GeneratedName(_nameSeed + i);
            if (!IsTaken(name)) return name;
        }
        return WorktreeLauncher.GeneratedName(_nameSeed);
    }

    private bool IsTaken(string branch) => _taken.Contains(branch) || _options.PathExists(DirectoryFor(branch));

    private void Recompute()
    {
        string prompt = WorktreeLauncher.SanitizePrompt(Prompt.Text);
        if (!_branchEdited)
        {
            string slug = WorktreeLauncher.Slug(prompt);
            Branch.Set(slug.Length > 0 && !IsTaken(slug) ? slug : FreeGeneratedName());
        }

        string branch = Branch.Text;
        Error = null;
        Warning = null;
        Directory = WorktreeLauncher.BranchProblem(branch) is null ? DirectoryFor(branch) : "";

        if (Agent is not { } agent)
        {
            Preview = "";
            Error = "No coding agent found on PATH (claude, copilot, codex, or [worktree_agents] in the config)";
            return;
        }
        bool argumentsOk = WorktreeLauncher.TryParseArguments(Arguments.Text, out var arguments, out string? argumentsError);
        string shortPrompt = prompt.Length > PreviewPromptChars ? prompt[..PreviewPromptChars] + "…" : prompt;
        Preview = argumentsOk ? WorktreeLauncher.JoinArguments(WorktreeLauncher.AgentArgv(agent, arguments, shortPrompt)) : "";

        Error = argumentsError ?? BranchError(branch) ?? WorktreeLauncher.BaseProblem(Base.Text);
        if (WorktreeLauncher.BypassesPermissions(Arguments.Text)) Warning = "These arguments turn off the agent's permission checks";
        else if (prompt.Length > 0 && !WorktreeLauncher.TakesPrompt(agent)) Warning = $"{agent.Name} takes no first prompt: this text is not passed on";
    }

    private string? BranchError(string branch)
    {
        if (WorktreeLauncher.BranchProblem(branch) is { } problem) return problem;
        if (_taken.Contains(branch)) return $"Branch {branch} already exists: try {WorktreeLauncher.UniqueName(branch, IsTaken)}";
        return _options.PathExists(Directory) ? $"{Directory} already exists" : null;
    }

    /// <summary>The launch, or null when something is wrong (see <see cref="Error"/>).</summary>
    public WorktreeRequest? TrySubmit()
    {
        if (Busy) return null;
        Recompute();
        if (Error is not null || Agent is not { } agent) return null;
        WorktreeLauncher.TryParseArguments(Arguments.Text, out var arguments, out _);
        string prompt = WorktreeLauncher.SanitizePrompt(Prompt.Text);
        return new WorktreeRequest(Branch.Text, Directory, Base.Text, agent, Arguments.Text,
            WorktreeLauncher.AgentArgv(agent, arguments, prompt), _options.Setup, _options.Cwd);
    }

    /// <summary>After a launch in the background: the name is taken now, and the form is ready for the next one (same agent and arguments).</summary>
    public void PrepareNext(string launchedBranch)
    {
        _taken.Add(launchedBranch);
        _branchEdited = false;
        _nameSeed++;
        Prompt.Clear();
        Focus = WorktreeField.Prompt;
        Recompute();
    }

    /// <summary>Shows a failure that came back from git (the form stays, so nothing typed is lost).</summary>
    public void Fail(string message)
    {
        Busy = false;
        Error = message;
    }
}
