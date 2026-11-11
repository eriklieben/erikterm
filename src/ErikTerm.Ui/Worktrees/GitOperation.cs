namespace ErikTerm.Ui;

/// <summary>What a worktree is in the middle of. <see cref="Squash"/>: <c>git merge --squash</c> stopped on a conflict.</summary>
public enum GitOperationKind { None, Merge, Rebase, CherryPick, Revert, Squash }

public readonly record struct GitOperationState(GitOperationKind Kind, int Step, int Total)
{
    public static readonly GitOperationState None = default;
}

/// <summary>
/// The merge/rebase state from git's state files, named as <c>git rev-parse --git-path</c> resolves them (per worktree:
/// a linked worktree has its own). Everything takes the probing as functions, so it runs against a real tree or a test's
/// dictionary. A rebase comes first: an interactive rebase also holds CHERRY_PICK_HEAD while a pick stops.
/// </summary>
public static class GitOperationDetector
{
    /// <summary>The state names to resolve with <c>--git-path</c>.</summary>
    public static readonly string[] StatePaths = ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "SQUASH_MSG"];

    /// <param name="exists">Whether a name from <see cref="StatePaths"/> (or a child of one, like <c>rebase-merge/msgnum</c>) exists.</param>
    /// <param name="read">A file's contents, or null.</param>
    /// <param name="hasConflicts">Whether the index has unmerged paths (asked only for a squash, which leaves no MERGE_HEAD).</param>
    public static GitOperationState Detect(Func<string, bool> exists, Func<string, string?> read, Func<bool> hasConflicts)
    {
        if (exists("rebase-merge"))
            return new GitOperationState(GitOperationKind.Rebase, Number(read("rebase-merge/msgnum")), Number(read("rebase-merge/end")));
        if (exists("rebase-apply") && !exists("rebase-apply/applying")) // applying: a plain `git am`, not a rebase
            return new GitOperationState(GitOperationKind.Rebase, Number(read("rebase-apply/next")), Number(read("rebase-apply/last")));
        if (exists("MERGE_HEAD")) return new GitOperationState(GitOperationKind.Merge, 0, 0);
        if (exists("CHERRY_PICK_HEAD")) return new GitOperationState(GitOperationKind.CherryPick, 0, 0);
        if (exists("REVERT_HEAD")) return new GitOperationState(GitOperationKind.Revert, 0, 0);
        if (exists("SQUASH_MSG") && hasConflicts()) return new GitOperationState(GitOperationKind.Squash, 0, 0);
        return GitOperationState.None;
    }

    private static int Number(string? text) => int.TryParse(text?.Trim(), out int n) && n > 0 ? n : 0;

    /// <summary>"Rebasing 3/7 · 2 conflicts".</summary>
    public static string BarText(GitOperationState state, int conflicts)
    {
        string verb = state.Kind switch
        {
            GitOperationKind.Merge => "Merging",
            GitOperationKind.Rebase => "Rebasing",
            GitOperationKind.CherryPick => "Cherry-picking",
            GitOperationKind.Revert => "Reverting",
            GitOperationKind.Squash => "Squash merging",
            _ => "",
        };
        if (state.Total > 0) verb += $" {state.Step}/{state.Total}";
        return conflicts > 0 ? $"{verb} · {Counted.Of(conflicts, "conflict")}" : verb;
    }

    /// <summary>The <c>git</c> subcommand of an operation, or null (a squash has no --continue or --abort).</summary>
    public static string? Subcommand(GitOperationKind kind) => kind switch
    {
        GitOperationKind.Merge => "merge",
        GitOperationKind.Rebase => "rebase",
        GitOperationKind.CherryPick => "cherry-pick",
        GitOperationKind.Revert => "revert",
        _ => null,
    };

    /// <summary>The next conflicted file after <paramref name="current"/>, wrapping around; null when none are left.</summary>
    public static string? NextConflict(IReadOnlyList<string> conflicts, string? current)
    {
        if (conflicts.Count == 0) return null;
        for (int i = 0; i < conflicts.Count; i++)
            if (conflicts[i] == current) return conflicts[(i + 1) % conflicts.Count];
        return conflicts[0];
    }

    /// <summary>The 1-based line of the first conflict marker in <paramref name="content"/>; 1 when there is none.</summary>
    public static int FirstMarkerLine(string content)
    {
        int line = 1, start = 0;
        while (start <= content.Length)
        {
            int end = content.IndexOf('\n', start);
            if (end < 0) end = content.Length;
            if (end - start >= 7 && string.CompareOrdinal(content, start, "<<<<<<<", 0, 7) == 0 && (end - start == 7 || content[start + 7] is ' ' or '\r')) return line;
            start = end + 1;
            line++;
        }
        return 1;
    }
}
