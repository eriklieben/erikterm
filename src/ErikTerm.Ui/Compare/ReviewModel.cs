namespace ErikTerm.Ui.Compare;

/// <summary>How a file changed, as <c>git diff --name-status</c> says it.</summary>
public enum ReviewChange { Modified, Added, Deleted, Renamed, Copied, TypeChanged, Unmerged }

/// <summary>A changed file of a review; <see cref="Added"/> and <see cref="Deleted"/> are line counts, 0 when unknown or binary.</summary>
public sealed record ReviewFile(string Path, string? OldPath, ReviewChange Change, int Added = 0, int Deleted = 0, bool Binary = false, bool Uncommitted = false)
{
    public string Letter => Change switch
    {
        ReviewChange.Added => "A",
        ReviewChange.Deleted => "D",
        ReviewChange.Renamed => "R",
        ReviewChange.Copied => "C",
        ReviewChange.TypeChanged => "T",
        ReviewChange.Unmerged => "U",
        _ => "M",
    };
}

/// <summary>One commit on the branch; <see cref="Parent"/> is null for a root commit.</summary>
public sealed record ReviewCommit(string Sha, string? Parent, string ShortSha, string Author, string When, string Subject);

/// <summary>Whether a review has something to compare.</summary>
public enum ReviewState { Ok, NotARepository, NoCommits, NoBase, UnknownBase, Unrelated }

/// <summary>What is reviewed: the branch, the branch plus the work tree, or a single commit.</summary>
public enum ReviewScopeKind { Branch, Uncommitted, Commit }

public readonly record struct ReviewScope(ReviewScopeKind Kind, ReviewCommit? Commit = null);

/// <summary>Everything the screen knows about a repository, read once in the background.</summary>
public sealed record ReviewSnapshot
{
    public required ReviewState State { get; init; }
    public string Root { get; init; } = "";
    /// <summary>The branch name, or "HEAD at abc1234" when detached.</summary>
    public string Branch { get; init; } = "";
    public bool OnBase { get; init; }
    public string HeadOid { get; init; } = "";
    public string Base { get; init; } = "";
    public string BaseOid { get; init; } = "";
    public string MergeBase { get; init; } = "";
    /// <summary><c>merge-base..HEAD</c>.</summary>
    public IReadOnlyList<ReviewFile> Committed { get; init; } = [];
    /// <summary><c>merge-base..work tree</c>, untracked files included.</summary>
    public IReadOnlyList<ReviewFile> WithUncommitted { get; init; } = [];
    public int UncommittedCount { get; init; }
    public IReadOnlyList<ReviewCommit> Commits { get; init; } = [];
    /// <summary>How many commits the local base lacks from origin; 0 when up to date or there's no origin counterpart.</summary>
    public int BehindCount { get; init; }
    /// <summary>The remote-tracking counterpart of the base (origin/main), or null.</summary>
    public string? RemoteBase { get; init; }
    public bool HasOrigin { get; init; }
    public bool LinkedWorktree { get; init; }
    /// <summary>A merge or rebase in progress, in words, or null.</summary>
    public string? Operation { get; init; }
    /// <summary>Why <see cref="State"/> isn't Ok, in words.</summary>
    public string Problem { get; init; } = "";

    public static ReviewSnapshot Failed(ReviewState state, string problem) => new() { State = state, Problem = problem };
}

/// <summary>What the screen asks of the outside world; every call runs on a background thread.</summary>
public interface IReviewSource
{
    /// <param name="baseOverride">A branch to compare against, or null for the repository's default.</param>
    ReviewSnapshot Load(string? baseOverride);

    (CompareSide Left, CompareSide Right) Sides(ReviewSnapshot snapshot, ReviewFile file, ReviewScope scope);

    IReadOnlyList<ReviewFile> CommitFiles(ReviewSnapshot snapshot, ReviewCommit commit);

    /// <summary>Whether merging into the base would conflict (read-only).</summary>
    ConflictPrediction Predict(ReviewSnapshot snapshot);

    /// <summary>Branches that can be the base.</summary>
    IReadOnlyList<string> Branches(ReviewSnapshot snapshot);

    /// <summary>Runs <c>git fetch origin BASE</c>; null on success, else what went wrong.</summary>
    string? Fetch(ReviewSnapshot snapshot);

    /// <summary>Opens the Finish Worktree dialog of the surrounding erikterm; null on success, else why it couldn't.</summary>
    string? RequestFinish(ReviewSnapshot snapshot);
}

/// <summary>Reading git's machine output: NUL-separated, so any file name passes through unharmed.</summary>
public static class ReviewParser
{
    /// <summary><c>git diff --name-status -z -M</c>: one entry per file; an unmerged entry wins over the second entry git adds for the same path.</summary>
    public static List<ReviewFile> NameStatus(string output)
    {
        var parts = output.Split('\0');
        var files = new List<ReviewFile>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Length; i++)
        {
            string status = parts[i];
            if (status.Length == 0) continue;
            char letter = status[0];
            if (!TryReadPaths(parts, ref i, letter, out string? oldPath, out string path)) break;
            if (path.Length == 0 || ChangeOf(letter) is not { } change) continue;
            AddOrReplace(files, index, new ReviewFile(path, oldPath, change));
        }
        return files;
    }

    /// <summary>Takes the path fields after a status (two for a rename or copy); false when the output ends too early.</summary>
    private static bool TryReadPaths(string[] parts, ref int i, char letter, out string? oldPath, out string path)
    {
        oldPath = null;
        path = "";
        int fields = letter is 'R' or 'C' ? 2 : 1;
        if (i + fields >= parts.Length) return false;
        if (fields == 2) oldPath = parts[++i];
        path = parts[++i];
        return true;
    }

    private static ReviewChange? ChangeOf(char letter) => letter switch
    {
        'M' => ReviewChange.Modified,
        'A' => ReviewChange.Added,
        'D' => ReviewChange.Deleted,
        'R' => ReviewChange.Renamed,
        'C' => ReviewChange.Copied,
        'T' => ReviewChange.TypeChanged,
        'U' => ReviewChange.Unmerged,
        _ => null,
    };

    private static void AddOrReplace(List<ReviewFile> files, Dictionary<string, int> index, ReviewFile file)
    {
        if (index.TryGetValue(file.Path, out int at))
        {
            if (file.Change == ReviewChange.Unmerged) files[at] = file;
            return;
        }
        index[file.Path] = files.Count;
        files.Add(file);
    }

    /// <summary>Line counts of <c>git diff --numstat -z -M</c>, by new path. Binary files are (-1, -1); a renamed path arrives as three fields.</summary>
    public static Dictionary<string, (int Added, int Deleted)> Numstat(string output)
    {
        var map = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var parts = output.Split('\0');
        int i = 0;
        while (i < parts.Length)
        {
            var fields = parts[i++].Split('\t', 3);
            if (fields.Length != 3) continue;
            string path = fields[2];
            if (path.Length == 0) // a rename: old and new path follow
            {
                if (i + 1 >= parts.Length) break;
                path = parts[i + 1];
                i += 2;
            }
            map[path] = (Count(fields[0]), Count(fields[1]));
        }
        return map;
    }

    private static int Count(string field) => int.TryParse(field, out int n) ? n : -1;

    /// <summary>Applies line counts to the entries; a (-1, -1) count marks the file binary.</summary>
    public static List<ReviewFile> WithCounts(IEnumerable<ReviewFile> files, Dictionary<string, (int Added, int Deleted)> counts)
    {
        var result = new List<ReviewFile>();
        foreach (var f in files)
        {
            if (counts.TryGetValue(f.Path, out var c))
                result.Add(f with { Added = Math.Max(0, c.Added), Deleted = Math.Max(0, c.Deleted), Binary = c.Added < 0 && c.Deleted < 0 });
            else result.Add(f);
        }
        return result;
    }

    /// <summary>
    /// <c>git log -z --format=%H%x00%P%x00%h%x00%an%x00%ar%x00%s</c>: six NUL-separated fields per commit, newest first.
    /// A merge's first parent is its "before".
    /// </summary>
    public static List<ReviewCommit> Commits(string output)
    {
        var parts = output.Split('\0');
        var commits = new List<ReviewCommit>();
        for (int i = 0; i + 5 < parts.Length; i += 6)
        {
            string sha = parts[i].Trim('\n', '\r');
            if (sha.Length == 0) continue;
            string parents = parts[i + 1].Trim();
            string? parent = parents.Length == 0 ? null : parents.Split(' ')[0];
            commits.Add(new ReviewCommit(sha, parent, parts[i + 2], parts[i + 3], parts[i + 4], parts[i + 5].TrimEnd('\n', '\r')));
        }
        return commits;
    }
}

/// <summary>The rules of a review that don't need git to test.</summary>
public static class ReviewRules
{
    /// <summary>
    /// Which ref to compare against: an explicit request as given; else the repository's default branch, as a local
    /// branch when it exists (that's what a PR would target here), else its remote-tracking ref. Null when there is
    /// no default branch to compare with.
    /// </summary>
    /// <param name="defaultBase">From the worktree launcher: origin/main, main, master or HEAD.</param>
    /// <param name="refExists">Whether a short ref name resolves.</param>
    public static string? ChooseBase(string? requested, string defaultBase, Func<string, bool> refExists)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        if (defaultBase is "" or "HEAD") return null;
        int slash = defaultBase.IndexOf('/');
        string local = slash >= 0 ? defaultBase[(slash + 1)..] : defaultBase;
        if (refExists(local)) return local;
        return refExists(defaultBase) ? defaultBase : null;
    }

    /// <summary>The remote-tracking counterpart of a base, or null when it has none (it already is one, or origin lacks it).</summary>
    public static string? RemoteBaseOf(string @base, Func<string, bool> refExists)
    {
        if (@base.StartsWith("origin/", StringComparison.Ordinal)) return null;
        string remote = "origin/" + @base;
        return refExists(remote) ? remote : null;
    }

    /// <summary>The note for a local base that's behind origin; null when it isn't.</summary>
    public static string? BehindNote(string @base, int behind) =>
        behind > 0 ? $"{@base} is {Counted.Of(behind, "commit")} behind origin" : null;

    /// <summary>The files with uncommitted changes (tracked and untracked) folded into <c>merge-base..work tree</c>.</summary>
    public static List<ReviewFile> MergeUncommitted(IReadOnlyList<ReviewFile> worktree, IReadOnlyList<ReviewFile> untracked, IReadOnlySet<string> uncommittedPaths)
    {
        var byPath = new Dictionary<string, ReviewFile>(worktree.Count + untracked.Count, StringComparer.Ordinal);
        foreach (var f in worktree) byPath[f.Path] = f with { Uncommitted = uncommittedPaths.Contains(f.Path) };
        foreach (var f in untracked)
        {
            if (byPath.TryGetValue(f.Path, out var tracked) && tracked.Change == ReviewChange.Deleted)
                byPath[f.Path] = f with { Change = ReviewChange.Modified, Uncommitted = true }; // git rm --cached: still on disk
            else byPath[f.Path] = f with { Uncommitted = true };
        }
        return [.. byPath.Values.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>"nothing to compare" in words, for a snapshot with no files.</summary>
    public static string EmptyMessage(ReviewSnapshot s)
    {
        if (s.State != ReviewState.Ok) return s.Problem;
        return s.OnBase
            ? $"You're on {s.Base}: nothing to compare."
            : $"{s.Branch} has no changes against {s.Base}.";
    }
}

/// <summary>A row of the file list: a folder header, or a file (index into the file array).</summary>
public readonly record struct ReviewRow(bool IsFolder, string Text, int File);

/// <summary>
/// The file list: files sorted and grouped under their folder, folders first and files at the root last (the paths in
/// a branch are long and share prefixes, so a header per folder reads better than a tree or repeated paths).
/// </summary>
public sealed class ReviewFileList
{
    public ReviewFileList(IEnumerable<ReviewFile> files)
    {
        var ordered = files.OrderBy(f => DirOf(f.Path).Length == 0 ? 1 : 0)
            .ThenBy(f => DirOf(f.Path), StringComparer.Ordinal)
            .ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
        Files = ordered;
        var rows = new List<ReviewRow>(ordered.Length + 8);
        string? dir = null;
        for (int i = 0; i < ordered.Length; i++)
        {
            string d = DirOf(ordered[i].Path);
            if (d != dir)
            {
                dir = d;
                if (d.Length > 0) rows.Add(new ReviewRow(true, d + "/", -1));
            }
            rows.Add(new ReviewRow(false, NameOf(ordered[i].Path), i));
        }
        Rows = rows;
    }

    public IReadOnlyList<ReviewFile> Files { get; }
    public IReadOnlyList<ReviewRow> Rows { get; }
    public int Count => Files.Count;

    public int RowOfFile(int file)
    {
        for (int i = 0; i < Rows.Count; i++) if (Rows[i].File == file) return i;
        return -1;
    }

    public int IndexOfPath(string path)
    {
        for (int i = 0; i < Files.Count; i++) if (Files[i].Path == path) return i;
        return -1;
    }

    /// <summary>The selection a step moves to, or -1 at the end of the list.</summary>
    public int Step(int current, int direction) =>
        Count == 0 || current + direction < 0 || current + direction >= Count ? -1 : current + direction;

    private static string DirOf(string path) { int s = path.LastIndexOf('/'); return s < 0 ? "" : path[..s]; }
    private static string NameOf(string path) { int s = path.LastIndexOf('/'); return s < 0 ? path : path[(s + 1)..]; }
}

/// <summary>Which arrangement a review screen uses; <see cref="ReviewLayout.ListHeight"/> rows of files in Stacked.</summary>
public enum ReviewLayoutMode { Wide, Stacked, Compact }

public readonly record struct ReviewLayout(ReviewLayoutMode Mode, int ListWidth, int ListHeight, bool Toolbar, bool Note)
{
    public const int WideColumns = 100;

    /// <param name="width">Columns.</param>
    /// <param name="height">Rows.</param>
    /// <param name="hasNote">Whether there's a note to show.</param>
    public static ReviewLayout Compute(int width, int height, bool hasNote)
    {
        bool toolbar = height >= 14;
        bool note = hasNote && height >= 12;
        int bodyRows = height - 2 - (toolbar ? 1 : 0) - (note ? 1 : 0); // header, footer
        if (width >= WideColumns && bodyRows >= 8)
            return new ReviewLayout(ReviewLayoutMode.Wide, Math.Clamp(width / 3, 28, 44), bodyRows, toolbar, note);
        if (bodyRows >= 16)
            return new ReviewLayout(ReviewLayoutMode.Stacked, width, Math.Clamp(bodyRows / 3, 4, 8), toolbar, note);
        return new ReviewLayout(ReviewLayoutMode.Compact, width, 0, toolbar, note);
    }
}

/// <summary>A pane already showing a review (from its <c>erikterm_review</c> user variable).</summary>
public readonly record struct ReviewPaneRef(int PaneId, string Root);

/// <summary>Whether "Compare with main" jumps to an existing tab or opens one.</summary>
public static class ReviewTabs
{
    /// <summary>The user variable a running review sets on its pane: the worktree root (empty when it ends).</summary>
    public const string UserVariable = "erikterm_review";

    /// <summary>The pane already reviewing <paramref name="root"/>, or null to open a new tab.</summary>
    public static int? Find(IEnumerable<ReviewPaneRef> panes, string root, bool ignoreCase = false)
    {
        string wanted = Normalize(root);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var pane in panes)
            if (pane.Root.Length > 0 && string.Equals(Normalize(pane.Root), wanted, comparison)) return pane.PaneId;
        return null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
