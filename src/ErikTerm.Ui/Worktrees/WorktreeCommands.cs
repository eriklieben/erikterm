using System.Text;

namespace ErikTerm.Ui;

/// <summary>Every command that changes a repository, as argv. Branch names are validated first; none of these force anything.</summary>
public static class WorktreeCommands
{
    private const string EndOfOptions = "--end-of-options";

    public static string[] SquashMerge(string mainRoot, string branch) => ["git", "-C", mainRoot, "merge", "--squash", EndOfOptions, branch];
    public static string[] Commit(string mainRoot, string message) => ["git", "-C", mainRoot, "commit", "-m", message];
    public static string[] Merge(string mainRoot, string branch) => ["git", "-C", mainRoot, "merge", "--no-ff", "--no-edit", EndOfOptions, branch];
    public static string[] Rebase(string worktree, string target) => ["git", "-C", worktree, "rebase", EndOfOptions, target];
    public static string[] FastForward(string mainRoot, string branch) => ["git", "-C", mainRoot, "merge", "--ff-only", EndOfOptions, branch];
    public static string[] Push(string worktree, string remote, string branch) => ["git", "-C", worktree, "push", "--set-upstream", remote, "refs/heads/" + branch + ":refs/heads/" + branch];
    public static string[] PullRequest(string target, string branch, string title, string body) => ["gh", "pr", "create", "--base", target, "--head", branch, "--title", title, "--body", body];
    public static string[] RemoveWorktree(string mainRoot, string path) => ["git", "-C", mainRoot, "worktree", "remove", "--", path];
    public static string[] DeleteBranch(string mainRoot, string branch, bool landedCheckPassed) => ["git", "-C", mainRoot, "branch", landedCheckPassed ? "-D" : "-d", EndOfOptions, branch];

    /// <summary><c>git OPERATION --continue</c> or <c>--abort</c>, in the worktree.</summary>
    public static string[] OperationStep(string worktree, GitOperationKind kind, bool abort)
    {
        if (GitOperationDetector.Subcommand(kind) is not { } sub) throw new ArgumentException("Nothing to continue or abort", nameof(kind));
        return ["git", "-C", worktree, sub, abort ? "--abort" : "--continue"];
    }

    /// <summary>A command as a dim line in a pane or the confirmation: long messages shortened, one line.</summary>
    public static string Display(IReadOnlyList<string> argv)
    {
        var words = argv.Select(a => OneLine.Clean(a.Replace('\n', ' '))).Select(a => a.Length > 48 ? a[..47] + "…" : a);
        return WorktreeLauncher.JoinArguments(words);
    }
}

/// <summary>One command of a <see cref="RepoPlan"/>. <paramref name="Guard"/> runs just before it, off the main thread; a returned string stops the plan with that reason.</summary>
public sealed record RepoStep(IReadOnlyList<string> Argv, string Cwd, string Display, Func<string?>? Guard = null);

public enum RepoPlanState { Running, Waiting, Failed, Done }

/// <summary>
/// A visible, ordered list of repository changes, run one at a time in a single pane: a step that fails stops the
/// plan where it is, so Retry (a FIDO touch, a hook) continues from there and nothing after it has run.
/// </summary>
public sealed class RepoPlan(string title, IReadOnlyList<RepoStep> steps)
{
    public string Title { get; } = title;
    public IReadOnlyList<RepoStep> Steps { get; } = steps;
    public int Index { get; private set; }
    public RepoPlanState State { get; private set; } = RepoPlanState.Running;
    public string? Failure { get; private set; }
    public RepoStep Current => Steps[Index];
    public bool HasNext => Index + 1 < Steps.Count;
    public Action? OnSuccess { get; init; }

    /// <summary>The current step exited. True when there is a next step to run (in <see cref="RepoPlanState.Waiting"/>); it stops on failure.</summary>
    public bool StepExited(int? exitCode)
    {
        if (State is not RepoPlanState.Running) return false;
        if (exitCode != 0)
        {
            Fail($"{Current.Display} exited with {(exitCode is { } code ? code.ToString() : "a signal")}");
            return false;
        }
        if (!HasNext)
        {
            State = RepoPlanState.Done;
            return false;
        }
        Index++;
        State = RepoPlanState.Waiting;
        return true;
    }

    /// <summary>A guard said no, or the step couldn't start.</summary>
    public void Fail(string reason)
    {
        State = RepoPlanState.Failed;
        Failure = reason;
    }

    /// <summary>The step's guard passed and its program started.</summary>
    public void Started() => State = RepoPlanState.Running;

    /// <summary>Back to the failed step (a fresh guard runs).</summary>
    public void Retry()
    {
        if (State != RepoPlanState.Failed) return;
        State = RepoPlanState.Waiting;
        Failure = null;
    }
}

/// <summary>The ways to finish a worktree.</summary>
public enum FinishOption { Squash, Merge, Rebase, PullRequest }

/// <summary>Everything the Finish dialog needs, gathered read-only in the background.</summary>
public sealed record FinishFacts
{
    public required string RepoName { get; init; }
    public required string Worktree { get; init; }
    public required string MainRoot { get; init; }
    public required string Branch { get; init; }
    public required string Target { get; init; }
    public required string HeadOid { get; init; }
    public required string TargetOid { get; init; }
    public required string? MainBranch { get; init; }
    public required int MainChanges { get; init; }
    public required int WorktreeChanges { get; init; }
    public GitOperationKind MainOperation { get; init; }
    public required IReadOnlyList<string> Subjects { get; init; }
    public required int Commits { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required ConflictPrediction Prediction { get; init; }
    public required bool HasDiff { get; init; }
    public bool HasOrigin { get; init; }
    public bool HasGh { get; init; }
    public int ExistingPr { get; init; }
    public bool AgentRunning { get; init; }
    public bool AgentInMain { get; init; }
}

/// <summary>The rules of Finish Worktree, the commands it runs and the message it starts from.</summary>
public static class FinishRules
{
    public static string Key(FinishOption option) => option switch
    {
        FinishOption.Merge => "merge",
        FinishOption.Rebase => "rebase",
        FinishOption.PullRequest => "pr",
        _ => "squash",
    };

    public static FinishOption ParseOption(string? text) => text switch
    {
        "merge" => FinishOption.Merge,
        "rebase" => FinishOption.Rebase,
        "pr" => FinishOption.PullRequest,
        _ => FinishOption.Squash,
    };

    public static string Label(FinishOption option) => option switch
    {
        FinishOption.Merge => "Merge",
        FinishOption.Rebase => "Rebase",
        FinishOption.PullRequest => "Open PR",
        _ => "Squash merge",
    };

    /// <summary>Why this option can't run right now, or null. The dialog shows the reason and refuses Enter.</summary>
    public static string? Problem(FinishOption option, FinishFacts f, string message)
    {
        if (f.AgentRunning) return "An agent is still running in this worktree: stop it first";
        if (!WorktreeList.IsSafeBranch(f.Branch)) return $"Branch name not usable: {f.Branch}";
        if (f.Branch == f.Target) return $"This is {f.Target} itself: nothing to finish";
        if (f.Commits == 0) return $"{f.Branch} has no commits that aren't on {f.Target}";
        return option == FinishOption.PullRequest ? PullRequestProblem(f, message) : LocalMergeProblem(option, f, message);
    }

    private static string? PullRequestProblem(FinishFacts f, string message)
    {
        if (!f.HasGh) return "gh isn't installed or logged in";
        if (!f.HasOrigin) return "No origin remote to push to";
        if (f.ExistingPr > 0) return $"PR #{f.ExistingPr} already exists for this branch";
        return message.Trim().Length == 0 ? "Enter a title for the PR" : null;
    }

    private static string? LocalMergeProblem(FinishOption option, FinishFacts f, string message)
    {
        if (f.AgentInMain) return "An agent is running in the main worktree: stop it first";
        if (f.WorktreeChanges > 0) return $"{Counted.Of(f.WorktreeChanges, "uncommitted change")} in {f.Branch}: commit or stash first";
        if (!f.HasDiff) return $"The work is already on {f.Target}: use Clean Up Worktrees";
        if (f.MainBranch != f.Target) return $"The main worktree is on {f.MainBranch ?? "a detached HEAD"}, not {f.Target}: nothing was touched";
        if (f.MainChanges > 0) return $"{Counted.Of(f.MainChanges, "uncommitted change")} in the main worktree: nothing was touched";
        if (f.MainOperation != GitOperationKind.None) return "The main worktree is in the middle of a merge or rebase";
        if (option != FinishOption.Rebase && f.Prediction is { Known: true, Clean: false })
            return $"{f.Prediction.Summary}: rebase first (the Rebase option), or open a PR";
        if (option == FinishOption.Squash && message.Trim().Length == 0) return "Enter a commit message";
        return null;
    }

    /// <summary>Exactly what will run, in order. <paramref name="message"/>: the edited text (Squash: the commit; Open PR: title, blank line, body).</summary>
    public static List<RepoStep> Steps(FinishOption option, FinishFacts f, string message)
    {
        string msg = CleanMessage(message);
        return option switch
        {
            FinishOption.Merge => [Step(WorktreeCommands.Merge(f.MainRoot, f.Branch), f.MainRoot)],
            FinishOption.Rebase =>
            [
                Step(WorktreeCommands.Rebase(f.Worktree, f.Target), f.Worktree),
                Step(WorktreeCommands.FastForward(f.MainRoot, f.Branch), f.MainRoot),
            ],
            FinishOption.PullRequest =>
            [
                Step(WorktreeCommands.Push(f.Worktree, "origin", f.Branch), f.Worktree),
                Step(WorktreeCommands.PullRequest(f.Target, f.Branch, Title(msg), Body(msg)), f.Worktree),
            ],
            _ =>
            [
                Step(WorktreeCommands.SquashMerge(f.MainRoot, f.Branch), f.MainRoot),
                Step(WorktreeCommands.Commit(f.MainRoot, msg), f.MainRoot),
            ],
        };
    }

    private static RepoStep Step(string[] argv, string cwd) => new(argv, cwd, WorktreeCommands.Display(argv));

    /// <summary>Keeps line breaks, drops every other control character, trims.</summary>
    public static string CleanMessage(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Where(c => c == '\n' || !char.IsControl(c)))
            sb.Append(c);
        return sb.ToString().Trim();
    }

    public static string Title(string message) => message.Split('\n')[0].Trim();
    public static string Body(string message)
    {
        int first = message.IndexOf('\n');
        return first < 0 ? "" : message[(first + 1)..].Trim();
    }

    private static readonly string[] AttributionMarkers =
        ["claude", "copilot", "codex", "openai", "anthropic", "cursor agent", "generated with", "generated by", "\U0001F916"];

    /// <summary>A line of an agent's own byline: such a line is never carried into the message erikterm suggests.</summary>
    public static bool IsAttribution(string line)
    {
        string l = line.Trim();
        bool trailer = l.StartsWith("co-authored-by:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("generated", StringComparison.OrdinalIgnoreCase);
        return (trailer && AttributionMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase))) || l.StartsWith("\U0001F916", StringComparison.Ordinal);
    }

    /// <summary>
    /// The commit message a squash starts from: one commit's message as it is; several become the branch name as a
    /// title with one line per subject. A co-author line naming an agent is left out and counted in
    /// <paramref name="dropped"/>; erikterm adds nothing of its own.
    /// </summary>
    public static string SquashMessage(string branch, IReadOnlyList<string> commitMessages, out int dropped)
    {
        dropped = 0;
        if (commitMessages.Count == 1)
        {
            var kept = new List<string>();
            foreach (string line in commitMessages[0].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                if (IsAttribution(line)) dropped++;
                else kept.Add(line);
            }
            return string.Join('\n', kept).Trim();
        }
        var sb = new StringBuilder();
        sb.Append(Humanize(branch)).Append("\n\n");
        foreach (string message in commitMessages)
        {
            string subject = message.Split('\n')[0].Trim();
            if (subject.Length > 0) sb.Append("- ").Append(subject).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Humanize(string branch)
    {
        string name = branch[(branch.LastIndexOf('/') + 1)..].Replace('-', ' ').Replace('_', ' ').Trim();
        return name.Length == 0 ? branch : char.ToUpperInvariant(name[0]) + name[1..];
    }
}

/// <summary>The Finish dialog's state: which option, the message being edited, and what will run.</summary>
public sealed class FinishForm
{
    public FinishFacts Facts { get; }
    public FinishOption Option { get; private set; }
    public TextInput Message { get; } = new(16 * 1024, multiline: true);
    public bool MessageFocused { get; set; }
    public bool Busy { get; set; }
    /// <summary>A failed last-moment check (nothing ran).</summary>
    public string? Error { get; private set; }
    public int DroppedAttribution { get; }

    public FinishForm(FinishFacts facts, FinishOption remembered, IReadOnlyList<string> commitMessages)
    {
        Facts = facts;
        Option = remembered;
        Message.Set(FinishRules.SquashMessage(facts.Branch, commitMessages, out int dropped));
        DroppedAttribution = dropped;
    }

    public void SelectOption(FinishOption option)
    {
        Option = option;
        Error = null;
    }

    public void CycleOption(int delta) => SelectOption((FinishOption)(((int)Option + delta + 4) % 4));

    public bool UsesMessage => Option is FinishOption.Squash or FinishOption.PullRequest;
    public string? Problem => FinishRules.Problem(Option, Facts, Message.Text);
    public IReadOnlyList<RepoStep> Steps => FinishRules.Steps(Option, Facts, Message.Text);
    public void Fail(string error) { Busy = false; Error = error; }
}
