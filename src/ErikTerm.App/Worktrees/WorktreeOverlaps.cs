using ErikTerm.App.Integrations;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.Worktrees;

/// <summary>The files a worktree's commits changed since its base. Fixed for a given head and target, so it is kept.</summary>
internal sealed record WorktreeCommittedFiles(string Head, string TargetOid, WorktreeChanges Changes);

/// <summary>Everything a worktree changed, and the committed part to reuse next time.</summary>
internal sealed record WorktreeChangesRead(WorktreeChanges Changes, WorktreeCommittedFiles? Committed);

/// <summary>Reads what a worktree changed against the commit it shares with the default branch. Read-only, like WorktreeManagerGit.</summary>
internal static class WorktreeChangedFiles
{
    private const int TimeoutMs = 5000;
    private const int MaxOutputChars = 256 * 1024; // thousands of paths; a bigger diff is reported as "or more"

    private static ToolResult Git(string cwd, params string[] arguments) =>
        ToolRunner.Run(["git", "-C", cwd, .. arguments], null, TimeoutMs, MaxOutputChars);

    private static bool CutOff(ToolResult r) => r.Stdout.Length >= MaxOutputChars;

    /// <summary>
    /// Null when git didn't answer. Status instead of <c>git diff &lt;base&gt;</c> for the uncommitted part: it also
    /// lists untracked files, and a plain diff would rewrite the index or report files that were only touched.
    /// </summary>
    public static WorktreeChangesRead? Read(string path, string targetOid, WorktreeCommittedFiles? known)
    {
        var status = Git(path, "-c", "core.fsmonitor=", "status", "--porcelain=v2", "--branch", "-z", "--no-renames", "--untracked-files=all");
        if (!status.Ok) return null;
        var (head, uncommitted) = WorktreeOverlap.ParseStatus(status.Stdout, CutOff(status));
        if (head is null) return new WorktreeChangesRead(uncommitted, null);
        var committed = known is not null && known.Head == head && known.TargetOid == targetOid ? known : Committed(path, head, targetOid);
        return committed is null ? null : new WorktreeChangesRead(committed.Changes.Plus(uncommitted), committed);
    }

    private static WorktreeCommittedFiles? Committed(string path, string head, string targetOid)
    {
        if (head == targetOid) return new WorktreeCommittedFiles(head, targetOid, WorktreeChanges.None);
        var mergeBase = Git(path, "merge-base", targetOid, head);
        if (mergeBase.TimedOut || mergeBase.StartError is not null) return null;
        if (!mergeBase.Ok) return new WorktreeCommittedFiles(head, targetOid, WorktreeChanges.None); // unrelated histories: no common base
        // --no-renames: a file moved away here still collides with an edit to it elsewhere.
        var diff = Git(path, "diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", mergeBase.Stdout.Trim(), head, "--");
        return diff.Ok ? new WorktreeCommittedFiles(head, targetOid, WorktreeOverlap.ParseNames(diff.Stdout, CutOff(diff))) : null;
    }
}

/// <summary>
/// The file-overlap warning of the worktree switcher, for one open list. One background job at a time reads the
/// worktrees that are due, one after the other, and does the intersection. A worktree is due when its row status changed, and
/// otherwise only every <see cref="RecheckMs"/>: an edit that leaves the number of changed files alone.
/// </summary>
internal sealed class WorktreeOverlaps(Action<Func<WorktreeOverlaps.Batch?>, Action<WorktreeOverlaps.Batch?>> background, Action changed, Func<string, string, WorktreeCommittedFiles?, WorktreeChangesRead?>? reader = null)
{
    private const long RecheckMs = 30_000;

    private readonly Func<string, string, WorktreeCommittedFiles?, WorktreeChangesRead?> _read = reader ?? WorktreeChangedFiles.Read;
    private readonly Dictionary<string, Known> _known = new(StringComparer.Ordinal);
    private Dictionary<string, List<FileOverlap>> _overlaps = new(StringComparer.Ordinal);
    private bool _running;

    private sealed class Known
    {
        public WorktreeChangesRead? Read;
        public WorktreeRowStatus? Seen;
        public long At;
        public bool Forced = true;
    }

    /// <summary>One background job's result.</summary>
    internal sealed record Batch(Dictionary<string, WorktreeChangesRead> Reads, Dictionary<string, List<FileOverlap>> Overlaps);

    /// <summary>The refresh key: everything is read again, without dropping what the rows show until then.</summary>
    public void Forget()
    {
        foreach (var known in _known.Values) known.Forced = true;
    }

    /// <summary>Starts a read of the worktrees that are due, unless one is running. A worktree without a row status yet waits for it.</summary>
    public void Refresh(IReadOnlyList<WorktreeEntry> entries, string? targetOid, Func<WorktreeEntry, WorktreeRowStatus?> statusOf, long now)
    {
        if (_running || targetOid is null) return;
        var due = new List<(string Path, WorktreeCommittedFiles? Committed)>();
        foreach (var entry in entries)
        {
            if (entry.Bare || entry.Prunable is not null || statusOf(entry) is not { TimedOut: false } status) continue;
            if (!_known.TryGetValue(entry.Path, out var known)) _known[entry.Path] = known = new Known();
            if (!known.Forced && known.Seen == status && now - known.At < RecheckMs) continue;
            (known.Forced, known.Seen, known.At) = (false, status, now);
            due.Add((entry.Path, known.Read?.Committed));
        }
        if (due.Count == 0) return;

        var current = _known.Where(k => k.Value.Read is not null).ToDictionary(k => k.Key, k => k.Value.Read!.Changes, StringComparer.Ordinal);
        _running = true;
        background(() => ReadAll(due, targetOid, current), Finished);
    }

    private Batch ReadAll(List<(string Path, WorktreeCommittedFiles? Committed)> due, string targetOid, Dictionary<string, WorktreeChanges> current)
    {
        var reads = new Dictionary<string, WorktreeChangesRead>(StringComparer.Ordinal);
        foreach (var (path, committed) in due)
        {
            if (_read(path, targetOid, committed) is not { } read) continue; // keeps what was known
            reads[path] = read;
            current[path] = read.Changes;
        }
        return new Batch(reads, WorktreeOverlap.Intersect(current));
    }

    private void Finished(Batch? batch)
    {
        _running = false;
        if (batch is null) return;
        foreach (var (path, read) in batch.Reads) _known[path].Read = read;
        _overlaps = batch.Overlaps;
        changed();
    }

    /// <summary>Puts the warning on the rows: first on the detail line, so the ellipsis takes the rest.</summary>
    public void Annotate(List<WorktreeRow> rows)
    {
        foreach (var row in rows)
        {
            if (!_overlaps.TryGetValue(row.Entry.Path, out var overlaps)) continue;
            row.Detail = $"{WorktreeOverlap.Describe(overlaps)} · {row.Detail}";
            row.Status = row.Status is "clean" or "…" ? "overlap" : "overlap " + row.Status;
            row.Warn = true;
        }
    }
}
