namespace ErikTerm.Ui;

/// <summary>The files a worktree changed against its base. <see cref="Truncated"/>: git printed more than was read, so there may be more.</summary>
public sealed record WorktreeChanges(IReadOnlySet<string> Files, bool Truncated)
{
    public static readonly WorktreeChanges None = new(new HashSet<string>(StringComparer.Ordinal), false);

    public WorktreeChanges Plus(WorktreeChanges other) =>
        new(new HashSet<string>(Files.Concat(other.Files), StringComparer.Ordinal), Truncated || other.Truncated);
}

/// <summary>The files this worktree shares with <see cref="Worktree"/> (a path). <see cref="AtLeast"/>: one of the two lists was cut off.</summary>
public sealed record FileOverlap(string Worktree, IReadOnlyList<string> Files, bool AtLeast);

/// <summary>Which worktrees of a repository changed the same files. No git here.</summary>
public static class WorktreeOverlap
{
    private const int ShownWorktrees = 2, ShownFiles = 2;

    /// <summary>NUL separated names (<c>git diff --name-only -z</c>).</summary>
    public static WorktreeChanges ParseNames(string stdout, bool truncated) =>
        new(new HashSet<string>(Records(stdout, truncated), StringComparer.Ordinal), truncated);

    /// <summary>
    /// <c>git status --porcelain=v2 --branch -z --no-renames --untracked-files=all</c>: the commit (null before the first
    /// one) and every uncommitted or untracked file.
    /// </summary>
    public static (string? Head, WorktreeChanges Changes) ParseStatus(string stdout, bool truncated)
    {
        const string oidHeader = "# branch.oid ";
        string? head = null;
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (string record in Records(stdout, truncated))
        {
            if (record.StartsWith(oidHeader, StringComparison.Ordinal))
            {
                string oid = record[oidHeader.Length..];
                head = oid.Length >= 40 ? oid : null; // "(initial)"
                continue;
            }
            string? path = record[0] switch
            {
                '1' => AfterFields(record, 8),
                'u' => AfterFields(record, 10),
                '?' => AfterFields(record, 1),
                _ => null,
            };
            if (path is { Length: > 0 }) files.Add(path);
        }
        return (head, new WorktreeChanges(files, truncated));
    }

    /// <summary>A cut-off output ends in half a record, which is dropped.</summary>
    private static IEnumerable<string> Records(string stdout, bool truncated)
    {
        string[] records = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return truncated ? records.Take(records.Length - 1) : records;
    }

    private static string? AfterFields(string record, int fields)
    {
        int at = 0;
        for (int i = 0; i < fields; i++)
        {
            at = record.IndexOf(' ', at) + 1;
            if (at == 0) return null;
        }
        return record[at..];
    }

    /// <summary>For every worktree (a path) that shares files with another: who and which, most files first.</summary>
    public static Dictionary<string, List<FileOverlap>> Intersect(IReadOnlyDictionary<string, WorktreeChanges> changes)
    {
        var overlaps = new Dictionary<string, List<FileOverlap>>(StringComparer.Ordinal);
        var worktrees = changes.ToList();
        for (int i = 0; i < worktrees.Count; i++)
        {
            for (int j = i + 1; j < worktrees.Count; j++)
            {
                var (a, b) = (worktrees[i], worktrees[j]);
                var shared = Shared(a.Value.Files, b.Value.Files);
                if (shared.Count == 0) continue;
                bool atLeast = a.Value.Truncated || b.Value.Truncated;
                AddTo(overlaps, a.Key, new FileOverlap(b.Key, shared, atLeast));
                AddTo(overlaps, b.Key, new FileOverlap(a.Key, shared, atLeast));
            }
        }
        foreach (var list in overlaps.Values)
            list.Sort((x, y) => x.Files.Count != y.Files.Count ? y.Files.Count.CompareTo(x.Files.Count) : string.CompareOrdinal(x.Worktree, y.Worktree));
        return overlaps;
    }

    private static List<string> Shared(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var shared = small.Where(large.Contains).ToList();
        shared.Sort(StringComparer.Ordinal);
        return shared;
    }

    private static void AddTo(Dictionary<string, List<FileOverlap>> overlaps, string worktree, FileOverlap overlap)
    {
        if (!overlaps.TryGetValue(worktree, out var list)) overlaps[worktree] = list = [];
        list.Add(overlap);
    }

    /// <summary>One line for a row: <c>Overlaps feat-a: Login.cs, Auth.cs +2; feat-b: Db.cs; +1 more</c>.</summary>
    public static string Describe(IReadOnlyList<FileOverlap> overlaps)
    {
        var parts = overlaps.Take(ShownWorktrees).Select(o => $"{WorktreeLauncher.RepoName(o.Worktree)}: {FileNames(o)}").ToList();
        if (overlaps.Count > ShownWorktrees) parts.Add($"+{overlaps.Count - ShownWorktrees} more");
        return OneLine.Clean("Overlaps " + string.Join("; ", parts));
    }

    private static string FileNames(FileOverlap overlap)
    {
        string names = string.Join(", ", overlap.Files.Take(ShownFiles).Select(BaseName));
        int hidden = overlap.Files.Count - ShownFiles;
        if (hidden > 0) names += $" +{hidden}";
        return overlap.AtLeast ? names + " or more" : names;
    }

    private static string BaseName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
