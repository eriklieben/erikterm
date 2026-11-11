namespace ErikTerm.Ui;

/// <summary>Why a branch counts as landed.</summary>
public enum LandedReason { None, Ancestor, PatchesUpstream, MergeTreeUnchanged, PullRequestMerged }

/// <summary>
/// What git and gh said about one commit and one target, read in the background. A null means "couldn't find out"
/// (a timeout, or a git without merge-tree): unknown evidence never makes something landed.
/// </summary>
public sealed record LandedEvidence
{
    public required string Target { get; init; }
    public required string Head { get; init; }
    /// <summary>The head commit is an ancestor of the target.</summary>
    public bool? Ancestor { get; init; }
    public int? CherryTotal { get; init; }
    /// <summary>Commits <c>git cherry</c> marks <c>+</c> (not upstream as a patch).</summary>
    public int? CherryUnmatched { get; init; }
    /// <summary>Merging the head into the target leaves the target's tree as it is.</summary>
    public bool? MergeUnchanged { get; init; }
    /// <summary>The head's tree differs from its merge base's; false: its commits net to nothing.</summary>
    public bool? NetChange { get; init; }
    public int? MergedPrNumber { get; init; }
    /// <summary>headRefOid of a merged PR for the branch.</summary>
    public string? MergedPrHead { get; init; }
}

public readonly record struct LandedVerdict(bool Landed, LandedReason Reason, string Why, string? Target)
{
    public static readonly LandedVerdict Unknown = new(false, LandedReason.None, "Not checked yet", null);
}

/// <summary>What keeps a worktree from being removed, in the order the user should hear about it.</summary>
public enum RemovalBlock { None, MainWorktree, Bare, Locked, Missing, NotLanded, Dirty, AgentRunning, TabOpen }

/// <summary>How the branch of a landed worktree is deleted.</summary>
public enum BranchDeletion { None, Merged, AfterLandedCheck }

/// <summary>
/// The rules for "has this work landed?". <c>git branch --merged</c> alone misses squash merges and cherry-picks, so:
/// the commit is an ancestor; or every commit is upstream as a patch (<c>git cherry</c>); or merging it into the target
/// changes nothing (<c>merge-tree</c>: the squash case) and it had a net change to begin with (a branch that changed
/// and reverted something netted nothing but still holds commits); or a PR for exactly this commit was merged.
/// </summary>
public static class Landing
{
    public static LandedVerdict Decide(LandedEvidence e)
    {
        if (e.Ancestor == true)
            return new LandedVerdict(true, LandedReason.Ancestor, $"{Short(e.Head)} is already part of {e.Target}", e.Target);
        if (e.CherryTotal > 0 && e.CherryUnmatched == 0)
            return new LandedVerdict(true, LandedReason.PatchesUpstream, e.CherryTotal == 1 ? $"Its commit is on {e.Target} as a cherry-pick" : $"All {e.CherryTotal} commits are on {e.Target} as cherry-picks", e.Target);
        if (e.MergeUnchanged == true && e.NetChange == true)
            return new LandedVerdict(true, LandedReason.MergeTreeUnchanged, $"Merging it into {e.Target} changes nothing (squash-merged)", e.Target);
        if (e.MergedPrNumber is { } pr && e.MergedPrHead is { } head && string.Equals(head, e.Head, StringComparison.OrdinalIgnoreCase))
            return new LandedVerdict(true, LandedReason.PullRequestMerged, $"PR #{pr} was merged at {Short(e.Head)}", e.Target);

        if (e.CherryUnmatched is > 0)
            return new LandedVerdict(false, LandedReason.None, $"{Counted.Of(e.CherryUnmatched.Value, "commit")} not on {e.Target}", e.Target);
        if (e.NetChange == false)
            return new LandedVerdict(false, LandedReason.None, $"Its commits net to no change, and they aren't on {e.Target}", e.Target);
        return new LandedVerdict(false, LandedReason.None, $"Not on {e.Target}", e.Target);
    }

    /// <summary>The first landed verdict across the targets (the local default branch and its remote one), else the first.</summary>
    public static LandedVerdict Best(IEnumerable<LandedEvidence> evidence)
    {
        LandedVerdict? first = null;
        foreach (var e in evidence)
        {
            var verdict = Decide(e);
            if (verdict.Landed) return verdict;
            first ??= verdict;
        }
        return first ?? LandedVerdict.Unknown;
    }

    /// <param name="ignoreOpenTab">The Finish flow: the pane that started it may sit in the worktree.</param>
    public static RemovalBlock Block(WorktreeEntry entry, LandedVerdict verdict, int changes, bool agentRunning, bool tabOpen, bool ignoreOpenTab = false)
    {
        if (entry.IsMain) return RemovalBlock.MainWorktree;
        if (entry.Bare) return RemovalBlock.Bare;
        if (entry.Locked is not null) return RemovalBlock.Locked;
        if (entry.Prunable is not null) return RemovalBlock.Missing;
        if (!verdict.Landed) return RemovalBlock.NotLanded;
        if (changes > 0) return RemovalBlock.Dirty;
        if (agentRunning) return RemovalBlock.AgentRunning;
        if (tabOpen && !ignoreOpenTab) return RemovalBlock.TabOpen;
        return RemovalBlock.None;
    }

    public static string Explain(RemovalBlock block, WorktreeEntry entry, LandedVerdict verdict, int changes) => block switch
    {
        RemovalBlock.None => verdict.Why,
        RemovalBlock.MainWorktree => "This is the main worktree",
        RemovalBlock.Bare => "A bare repository has no worktree to remove",
        RemovalBlock.Locked => $"Locked: {entry.Locked}",
        RemovalBlock.Missing => "Its directory is gone: git worktree prune cleans that up",
        RemovalBlock.NotLanded => verdict.Why,
        RemovalBlock.Dirty => $"{Counted.Of(changes, "uncommitted change")} in the worktree",
        RemovalBlock.AgentRunning => "An agent is still running in it: stop it first",
        RemovalBlock.TabOpen => "A tab is open in it",
        _ => "",
    };

    /// <summary><c>-d</c> when git can see the merge itself; otherwise <c>-D</c>, and only after a fresh landed check of the exact commit.</summary>
    public static BranchDeletion Deletion(WorktreeEntry entry, LandedVerdict verdict, bool mainOnTarget)
    {
        if (entry.Branch is null) return BranchDeletion.None;
        return verdict.Reason == LandedReason.Ancestor && mainOnTarget ? BranchDeletion.Merged : BranchDeletion.AfterLandedCheck;
    }

    private static string Short(string oid) => oid.Length > 7 ? oid[..7] : oid;
}
