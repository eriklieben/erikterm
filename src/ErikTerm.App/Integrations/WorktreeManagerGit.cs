using System.Text.Json;
using System.Text.Json.Serialization;
using ErikTerm.Ui;

namespace ErikTerm.App.Integrations;

/// <summary>A repository's worktrees and its default branch, read once when a dialog opens.</summary>
internal sealed record WorktreeInventory(string MainRoot, string RepoName, IReadOnlyList<WorktreeEntry> Entries, string? Target, string? TargetOid, bool HasWriteTree);

/// <summary>One row's background status: dirty files, ahead/behind the default branch.</summary>
internal sealed record WorktreeRowStatus(int Changes, int Ahead, int Behind, bool TimedOut);

/// <summary><c>gh pr list --json number,headRefOid</c>.</summary>
internal sealed class GhMergedPr
{
    public int Number { get; set; }
    public string? HeadRefOid { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<GhMergedPr>))]
internal sealed partial class GhMergedJsonContext : JsonSerializerContext;

/// <summary>
/// The read-only git half of worktree management: list, status, landed evidence, conflict prediction, merge state.
/// Everything is an argv through <see cref="ToolRunner"/>, time-limited, and commits are passed as resolved object ids.
/// Nothing here changes a repository; changes run in a visible pane (WindowFeatures/WindowRepoPlans.cs).
/// </summary>
internal static class WorktreeManagerGit
{
    private const string RevParse = "rev-parse";
    private const string Verify = "--verify";
    private const string Quiet = "--quiet";
    private const string BranchRefPrefix = "refs/heads/";
    private const string TreeOf = "^{tree}";

    private const int QuickMs = 3000, SlowMs = 8000;

    private static ToolResult Git(string cwd, int timeoutMs, params string[] arguments) =>
        ToolRunner.Run(["git", "-C", cwd, .. arguments], null, timeoutMs);

    private static string? Oid(ToolResult r) => r.Ok && r.Stdout.Trim() is { Length: >= 40 } oid ? oid : null;

    private static string? ResolveCommit(string cwd, string @ref) =>
        Oid(Git(cwd, QuickMs, RevParse, Verify, Quiet, "--end-of-options", @ref + "^{commit}"));

    public static bool HasWriteTree() => ToolRunner.Run(["git", "version"], null, QuickMs) is { Ok: true } v && GitVersion.SupportsWriteTree(v.Stdout);

    /// <summary>Worktrees of the repository at <paramref name="cwd"/> (a linked worktree or a subdirectory is fine).</summary>
    public static WorktreeInventory? Inventory(string cwd, out string? error)
    {
        if (WorktreeGit.Probe(cwd, out error) is not { } repo) return null;
        var list = Git(cwd, QuickMs, "worktree", "list", "--porcelain", "-z");
        if (!list.Ok) list = Git(cwd, QuickMs, "worktree", "list", "--porcelain"); // git older than 2.36
        if (!list.Ok) { error = "git worktree list failed"; return null; }
        var entries = WorktreeList.Parse(list.Stdout);
        if (entries.Count == 0) { error = "git listed no worktrees"; return null; }
        string? target = WorktreeList.LocalBranch(repo.DefaultBase);
        string? targetOid = target is null ? null : ResolveCommit(repo.MainRoot, BranchRefPrefix + target);
        return new WorktreeInventory(repo.MainRoot, repo.Name, entries, targetOid is null ? null : target, targetOid, HasWriteTree());
    }

    /// <summary>Time-limited status of one worktree (for the switcher's row); never blocks longer than two git calls.</summary>
    public static WorktreeRowStatus Status(WorktreeEntry entry, string? targetOid)
    {
        var status = Git(entry.Path, QuickMs, "-c", "core.fsmonitor=", "status", "--porcelain=v2", "--branch");
        if (status.TimedOut) return new WorktreeRowStatus(0, 0, 0, TimedOut: true);
        int changes = status.Ok ? GitStatusParser.Parse(status.Stdout)?.Changes ?? 0 : 0;
        int ahead = 0, behind = 0;
        if (targetOid is not null && entry.Head is { Length: 40 } head && head != targetOid)
        {
            var counts = Git(entry.Path, QuickMs, "rev-list", "--left-right", "--count", $"{targetOid}...{head}");
            if (counts.TimedOut) return new WorktreeRowStatus(changes, 0, 0, TimedOut: true);
            string[] parts = counts.Ok ? counts.Stdout.Trim().Split('\t') : [];
            if (parts.Length == 2 && int.TryParse(parts[0], out int b) && int.TryParse(parts[1], out int a)) (behind, ahead) = (b, a);
        }
        return new WorktreeRowStatus(changes, ahead, behind, TimedOut: false);
    }

    /// <summary>Uncommitted entries of a worktree (status porcelain), or null when git didn't answer.</summary>
    public static int? Changes(string path)
    {
        var status = Git(path, QuickMs, "-c", "core.fsmonitor=", "status", "--porcelain=v2");
        if (!status.Ok) return null;
        int n = 0;
        foreach (var line in status.Stdout.AsSpan().EnumerateLines()) if (!line.IsEmpty) n++;
        return n;
    }

    // ---- Conflict prediction -----------------------------------------------------------------

    /// <summary><c>git merge-tree --write-tree</c>: reads and writes objects only, no working files.</summary>
    public static ConflictPrediction Predict(string root, string targetOid, string headOid, bool hasWriteTree)
    {
        if (!hasWriteTree) return ConflictPrediction.Unknown("Conflict prediction needs git 2.38 or later");
        var r = Git(root, SlowMs, "merge-tree", "--write-tree", "--no-messages", "--name-only", "-z", targetOid, headOid);
        if (r.TimedOut) return ConflictPrediction.Unknown("git merge-tree timed out");
        if (r.StartError is { } start) return ConflictPrediction.Unknown(start);
        return ConflictPrediction.Parse(r.ExitCode, r.Stdout, r.Stderr);
    }

    // ---- Landed ------------------------------------------------------------------------------

    /// <summary>The evidence that <paramref name="headOid"/> is on <paramref name="targetName"/>. Unknown fields stay null.</summary>
    public static LandedEvidence Evidence(string root, string headOid, string targetName, string targetOid, bool hasWriteTree)
    {
        var e = new LandedEvidence { Target = targetName, Head = headOid };
        var ancestor = Git(root, QuickMs, "merge-base", "--is-ancestor", headOid, targetOid);
        if (ancestor.TimedOut) return e;
        e = e with { Ancestor = ancestor.ExitCode == 0 };
        if (e.Ancestor == true) return e;

        var cherry = Git(root, SlowMs, "cherry", targetOid, headOid);
        if (cherry.Ok) e = WithCherryCounts(e, cherry.Stdout);
        return hasWriteTree ? WithMergeResult(e, root, headOid, targetOid) : e;
    }

    private static LandedEvidence WithCherryCounts(LandedEvidence e, string cherryOutput)
    {
        int total = 0, unmatched = 0;
        foreach (var line in cherryOutput.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty) continue;
            total++;
            if (line[0] == '+') unmatched++;
        }
        return e with { CherryTotal = total, CherryUnmatched = unmatched };
    }

    private static LandedEvidence WithMergeResult(LandedEvidence e, string root, string headOid, string targetOid)
    {
        if (Predict(root, targetOid, headOid, hasWriteTree: true) is { Known: true, Clean: true, Tree: { } merged }
            && Git(root, QuickMs, RevParse, Verify, Quiet, targetOid + TreeOf) is { Ok: true } treeOfTarget
            && Git(root, QuickMs, "merge-base", targetOid, headOid) is { Ok: true } baseOut)
        {
            string @base = baseOut.Stdout.Trim().Split('\n')[0];
            var trees = Git(root, QuickMs, RevParse, Verify, Quiet, @base + TreeOf);
            var headTree = Git(root, QuickMs, RevParse, Verify, Quiet, headOid + TreeOf);
            bool net = trees.Ok && headTree.Ok && trees.Stdout.Trim() != headTree.Stdout.Trim();
            e = e with { MergeUnchanged = merged == treeOfTarget.Stdout.Trim(), NetChange = trees.Ok && headTree.Ok ? net : null };
        }
        return e;
    }

    /// <summary>
    /// Whether <paramref name="headOid"/> landed: against the local default branch, then its origin counterpart (a PR
    /// merged on GitHub shows there first), then via a merged PR for exactly this commit.
    /// </summary>
    public static LandedVerdict Landed(string root, string headOid, string? branch, string target, bool hasWriteTree, bool useGh)
    {
        var evidence = new List<LandedEvidence>();
        if (ResolveCommit(root, BranchRefPrefix + target) is { } local)
            evidence.Add(Evidence(root, headOid, target, local, hasWriteTree));
        if (ResolveCommit(root, "refs/remotes/origin/" + target) is { } remote)
            evidence.Add(Evidence(root, headOid, "origin/" + target, remote, hasWriteTree));
        var verdict = Landing.Best(evidence);
        if (verdict.Landed || !useGh || branch is null || !WorktreeList.IsSafeBranch(branch)) return verdict;

        var prs = ToolRunner.Run(["gh", "pr", "list", "--head=" + branch, "--state", "merged", "--limit", "5", "--json", "number,headRefOid"], root, SlowMs);
        if (!prs.Ok) return verdict;
        foreach (var pr in ParseMergedPrs(prs.Stdout))
        {
            var withPr = new LandedEvidence { Target = target, Head = headOid, MergedPrNumber = pr.Number, MergedPrHead = pr.HeadRefOid };
            if (Landing.Decide(withPr) is { Landed: true } landed) return landed;
        }
        return verdict;
    }

    internal static List<GhMergedPr> ParseMergedPrs(string json)
    {
        try { return JsonSerializer.Deserialize(json, GhMergedJsonContext.Default.ListGhMergedPr) ?? []; }
        catch (JsonException) { return []; }
    }

    // ---- Finish ------------------------------------------------------------------------------

    /// <summary>What the Finish dialog shows for <paramref name="wt"/>. Null with <paramref name="error"/> when the repository can't be finished at all.</summary>
    public static FinishFacts? Facts(WorktreeInventory inv, WorktreeEntry wt, bool hasGh, int existingPr, out string? error)
    {
        error = null;
        var main = inv.Entries[0];
        if (main.Bare) { error = "A bare repository has no main worktree to merge into"; return null; }
        if (inv.Target is not { } target || inv.TargetOid is not { } targetOid) { error = "No default branch to finish into (no origin/HEAD, main or master)"; return null; }
        if (wt.Branch is not { } branch) { error = "This worktree is on a detached HEAD: there's no branch to finish"; return null; }
        if (wt.Head is not { Length: 40 } head) { error = "This worktree has no commits yet"; return null; }

        var mainState = StatusOf(main.Path);
        var wtState = StatusOf(wt.Path);
        if (mainState is null || wtState is null) { error = "git status didn't answer in time"; return null; }

        var log = Git(wt.Path, SlowMs, "log", "--no-merges", "--reverse", "-n", "500", "--format=%B%x00", $"{targetOid}..{head}");
        var messages = log.Ok
            ? log.Stdout.Split('\0').Select(m => m.Trim()).Where(m => m.Length > 0).ToList()
            : [];
        var diff = Git(wt.Path, SlowMs, "diff", "--name-only", "-z", $"{targetOid}...{head}");
        var files = diff.Ok ? diff.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
        var prediction = Predict(inv.MainRoot, targetOid, head, inv.HasWriteTree);

        // "Would change anything": the merge's tree differs from the target's (a squash-landed branch doesn't).
        bool hasDiff = files.Count > 0;
        if (prediction is { Known: true, Clean: true, Tree: { } merged }
            && Git(inv.MainRoot, QuickMs, RevParse, Verify, Quiet, targetOid + TreeOf) is { Ok: true } t && t.Stdout.Trim() == merged)
            hasDiff = false;

        var remotes = Git(inv.MainRoot, QuickMs, "remote");
        bool hasOrigin = remotes.Ok && remotes.Stdout.Split('\n', StringSplitOptions.TrimEntries).Contains("origin");
        return new FinishFacts
        {
            RepoName = inv.RepoName, Worktree = wt.Path, MainRoot = inv.MainRoot, Branch = branch, Target = target, HeadOid = head, TargetOid = targetOid,
            MainBranch = mainState.Detached ? null : mainState.Branch, MainChanges = mainState.Changes, WorktreeChanges = wtState.Changes,
            MainOperation = OperationOf(main.Path), Subjects = messages, Commits = messages.Count,
            Files = files, Prediction = prediction, HasDiff = hasDiff, HasOrigin = hasOrigin, HasGh = hasGh, ExistingPr = existingPr,
        };
    }

    private static GitStatus? StatusOf(string path)
    {
        var r = Git(path, QuickMs, "-c", "core.fsmonitor=", "status", "--porcelain=v2", "--branch");
        return r.Ok ? GitStatusParser.Parse(r.Stdout) : null;
    }

    /// <summary>Re-checks what the dialog showed, right before anything runs: null when it all still holds, else why not (nothing ran).</summary>
    public static string? VerifyFinish(FinishFacts f, FinishOption option, bool afterRebase = false)
    {
        if (!afterRebase && ResolveCommit(f.MainRoot, BranchRefPrefix + f.Branch) != f.HeadOid) return $"{f.Branch} moved since this dialog opened: nothing was touched";
        if (option == FinishOption.PullRequest) return null;
        if (ResolveCommit(f.MainRoot, BranchRefPrefix + f.Target) != f.TargetOid) return $"{f.Target} moved since this dialog opened: nothing was touched";
        if (StatusOf(f.MainRoot) is not { } main) return "git status of the main worktree didn't answer: nothing was touched";
        if (main.Detached || main.Branch != f.Target) return $"The main worktree isn't on {f.Target} any more: nothing was touched";
        if (main.Changes > 0) return $"{Counted.Of(main.Changes, "uncommitted change")} appeared in the main worktree: nothing was touched";
        if (OperationOf(f.MainRoot) != GitOperationKind.None) return "The main worktree is in the middle of a merge or rebase: nothing was touched";
        if (afterRebase) return null;
        if (StatusOf(f.Worktree) is not { } wt) return "git status of the worktree didn't answer: nothing was touched";
        return wt.Changes > 0 ? $"{Counted.Of(wt.Changes, "uncommitted change")} appeared in the worktree: nothing was touched" : null;
    }

    /// <summary>Right before removing a worktree: still clean, still landed for this exact commit. Null, or why not.</summary>
    public static string? VerifyRemoval(WorktreeEntry entry, string target, bool hasWriteTree, bool useGh, string mainRoot)
    {
        if (entry.Head is not { Length: 40 } head) return "This worktree has no commit";
        if (Changes(entry.Path) is not { } changes) return "git status of the worktree didn't answer: it wasn't removed";
        if (changes > 0) return $"{Counted.Of(changes, "uncommitted change")} in the worktree: it wasn't removed";
        if (ResolveCommit(entry.Path, "HEAD") != head) return "The worktree moved on since the check: it wasn't removed";
        return Landed(mainRoot, head, entry.Branch, target, hasWriteTree, useGh) is { Landed: true } ? null : "The landed check no longer passes for this commit: it wasn't removed";
    }

    /// <summary>Right before <c>git branch -D</c>: the branch is exactly the commit that was checked, and it still counts as landed. Null, or why not.</summary>
    public static string? VerifyBranchDelete(string mainRoot, string branch, string head, string target, bool hasWriteTree, bool useGh)
    {
        if (ResolveCommit(mainRoot, BranchRefPrefix + branch) != head) return $"{branch} isn't at {head[..7]} any more: it wasn't deleted";
        return Landed(mainRoot, head, branch, target, hasWriteTree, useGh) is { Landed: true } ? null : $"{branch} no longer counts as landed: it wasn't deleted";
    }

    // ---- Merge / rebase state ----------------------------------------------------------------

    /// <summary>Where this worktree keeps its state files (<c>git rev-parse --git-path</c>): per worktree, resolved once.</summary>
    public static Dictionary<string, string>? StatePaths(string root)
    {
        var argv = new List<string> { RevParse };
        foreach (string name in GitOperationDetector.StatePaths) { argv.Add("--git-path"); argv.Add(name); }
        var r = ToolRunner.Run(["git", "-C", root, .. argv], null, QuickMs);
        if (!r.Ok) return null;
        string[] lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != GitOperationDetector.StatePaths.Length) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++) map[GitOperationDetector.StatePaths[i]] = Path.GetFullPath(lines[i].Trim(), root);
        return map;
    }

    public static GitOperationKind OperationOf(string root) =>
        StatePaths(root) is { } paths ? Probe(root, paths).State.Kind : GitOperationKind.None;

    /// <summary>The worktree's merge state and its conflicted files.</summary>
    public static (GitOperationState State, IReadOnlyList<string> Conflicts) Probe(string root, Dictionary<string, string> paths)
    {
        bool Exists(string name) => Path.Exists(Resolve(paths, name));
        string? Read(string name)
        {
            try { return File.ReadAllText(Resolve(paths, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        var state = GitOperationDetector.Detect(Exists, Read, () => Unmerged(root).Count > 0);
        return (state, state.Kind == GitOperationKind.None ? [] : Unmerged(root));
    }

    private static string Resolve(Dictionary<string, string> paths, string name)
    {
        int slash = name.IndexOf('/');
        return slash < 0 ? paths[name] : Path.Combine(paths[name[..slash]], name[(slash + 1)..]);
    }

    private static List<string> Unmerged(string root)
    {
        var r = Git(root, QuickMs, "-c", "diff.autoRefreshIndex=false", "diff", "--name-only", "--diff-filter=U", "-z"); // read-only: no index refresh
        return r.Ok ? r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
    }

    /// <summary>The conflicted file <paramref name="relative"/> under <paramref name="root"/>, and the line of its first marker.</summary>
    public static (string Path, int Line)? OpenAtMarker(string root, string relative)
    {
        string path = Path.GetFullPath(relative, root);
        try
        {
            if (new FileInfo(path).Length > 8 << 20) return (path, 1);
            return (path, GitOperationDetector.FirstMarkerLine(File.ReadAllText(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The default branch's commit and the prediction for <paramref name="head"/> (the tab warning): null when there is nothing to compare.</summary>
    public static (string TargetOid, ConflictPrediction Prediction)? ConflictCheck(string root, string target, string head, string? knownTarget)
    {
        if (ResolveCommit(root, BranchRefPrefix + target) is not { } targetOid) return null;
        if (targetOid == knownTarget) return (targetOid, ConflictPrediction.Unknown("unchanged"));
        return (targetOid, Predict(root, targetOid, head, HasWriteTree()));
    }

    // ---- Paths -------------------------------------------------------------------------------

    public static string? OwnerOf(WorktreeInventory inv, string directory) =>
        WorktreeList.Owner(directory, inv.Entries.Select(e => e.Path), OperatingSystem.IsWindows());
}
