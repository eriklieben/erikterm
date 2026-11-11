namespace ErikTerm.App.Integrations;

/// <summary>
/// What a repo badge shows, with its strings built once (drawing allocates nothing). Value equality, so a refresh
/// that changes nothing visible doesn't cause a frame.
/// </summary>
internal sealed record RepoBadgeModel
{
    public const int MaxBranchChars = 22;

    public required string Branch { get; init; }
    /// <summary>Branch shortened for the tab (…).</summary>
    public required string BranchShort { get; init; }
    /// <summary>The repository's name (its GitHub repo, else its root directory's); empty until the root is known.</summary>
    public string RepoName { get; init; } = "";
    public bool Detached { get; init; }
    public string? AheadText { get; init; }      // ↑2
    public string? BehindText { get; init; }     // ↓1
    public string? StagedText { get; init; }     // +3
    public string? ModifiedText { get; init; }   // 2 (unstaged changes, drawn after a dot)
    public string? UntrackedText { get; init; }  // ?1
    public string? ConflictText { get; init; }   // !1
    /// <summary>All changes as one count for the compact tab badge (drawn after a dot), null when clean.</summary>
    public string? DirtyText { get; init; }
    public bool Conflicted { get; init; }
    /// <summary>merge-tree predicts a conflict with the default branch (git_conflict_check).</summary>
    public bool WouldConflict { get; init; }
    public string? PrText { get; init; }         // #42
    public bool Draft { get; init; }
    public CheckState Checks { get; init; }
    /// <summary>"2 failing", "3 pending", "checks passed" — for the pill and the palette.</summary>
    public string? ChecksText { get; init; }

    public static RepoBadgeModel? From(GitStatus? git, GitHubStatus? gh)
    {
        if (git is null) return null;
        int changes = git.Changes;
        return new RepoBadgeModel
        {
            Branch = git.Branch,
            BranchShort = Shorten(git.Branch, MaxBranchChars),
            Detached = git.Detached,
            AheadText = git.Ahead > 0 ? "↑" + Count(git.Ahead) : null,
            BehindText = git.Behind > 0 ? "↓" + Count(git.Behind) : null,
            StagedText = git.Staged > 0 ? "+" + Count(git.Staged) : null,
            ModifiedText = git.Modified > 0 ? Count(git.Modified) : null,
            UntrackedText = git.Untracked > 0 ? "?" + Count(git.Untracked) : null,
            ConflictText = git.Conflicts > 0 ? "!" + Count(git.Conflicts) : null,
            DirtyText = changes > 0 ? Count(changes) : null,
            Conflicted = git.Conflicts > 0,
            PrText = gh is { HasPr: true } ? "#" + gh.PrNumber : null,
            Draft = gh?.Draft ?? false,
            Checks = gh?.Checks ?? CheckState.None,
            ChecksText = gh is null ? null : DescribeChecks(gh),
        };
    }

    public static string DescribeChecks(GitHubStatus gh) => gh.Checks switch
    {
        CheckState.Fail when gh.ChecksFailed > 0 => $"{gh.ChecksFailed} failing",
        CheckState.Fail => "failed",
        CheckState.Pending when gh.ChecksPending > 0 => $"{gh.ChecksPending} pending",
        CheckState.Pending => "running",
        CheckState.Pass => gh.HasPr ? "checks passed" : "passed",
        _ => "",
    };

    private static string Count(int n) => n > 999 ? "999+" : n.ToString();

    public static string Shorten(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
}
