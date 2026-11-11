using System.Text;

namespace ErikTerm.Ui;

/// <summary>One entry of <c>git worktree list --porcelain</c>. <see cref="IsMain"/>: git always lists the main worktree first.</summary>
public sealed record WorktreeEntry(string Path, string? Head, string? Branch, bool Bare, bool Detached, string? Locked, string? Prunable, bool IsMain)
{
    /// <summary>The worktree's directory name.</summary>
    public string Name => WorktreeLauncher.RepoName(Path);
    /// <summary>The branch, or the short commit of a detached HEAD.</summary>
    public string BranchLabel => Branch ?? (Head is { Length: >= 7 } h ? h[..7] : "(detached)");
}

/// <summary>Reading <c>git worktree list</c> and matching directories to worktrees. No git here.</summary>
public static class WorktreeList
{
    /// <summary>Parses <c>--porcelain</c> output, NUL separated (<c>-z</c>) or line separated. Tolerates unknown attributes.</summary>
    public static List<WorktreeEntry> Parse(string text)
    {
        var entries = new List<WorktreeEntry>();
        string[] lines = text.Contains('\0') ? text.Split('\0') : text.Split('\n');
        var current = new PorcelainEntry();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) { current.FlushTo(entries); continue; }
            int space = line.IndexOf(' ');
            string key = space < 0 ? line : line[..space], value = space < 0 ? "" : line[(space + 1)..];
            if (key == "worktree") current.FlushTo(entries);
            current.Set(key, value);
        }
        current.FlushTo(entries);
        return entries;
    }

    /// <summary>The attributes of the entry being read.</summary>
    private sealed class PorcelainEntry
    {
        private string? _path, _head, _branch, _locked, _prunable;
        private bool _bare, _detached;

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "worktree": _path = value; break;
                case "HEAD": _head = value; break;
                case "branch": _branch = value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value; break;
                case "bare": _bare = true; break;
                case "detached": _detached = true; break;
                case "locked": _locked = value.Length > 0 ? value : "locked"; break;
                case "prunable": _prunable = value.Length > 0 ? value : "prunable"; break;
            }
        }

        public void FlushTo(List<WorktreeEntry> entries)
        {
            if (_path is null) return;
            entries.Add(new WorktreeEntry(_path, _head, _branch, _bare, _detached, _locked, _prunable, entries.Count == 0));
            (_path, _head, _branch, _locked, _prunable, _bare, _detached) = (null, null, null, null, null, false, false);
        }
    }

    /// <summary>The local branch behind a default base: <c>origin/main</c> is <c>main</c>; <c>main</c> stays; <c>HEAD</c> means none.</summary>
    public static string? LocalBranch(string @base)
    {
        if (@base.Length == 0 || @base == "HEAD") return null;
        int slash = @base.IndexOf('/');
        return slash >= 0 ? @base[(slash + 1)..] : @base;
    }

    /// <summary>A path compared the way git prints it: forward slashes, no trailing slash, no case on Windows.</summary>
    public static string Normalize(string path, bool ignoreCase = false)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        return ignoreCase ? p.ToUpperInvariant() : p;
    }

    /// <summary>The worktree that holds <paramref name="directory"/>: the deepest root that contains it (a worktree can live inside another), or null.</summary>
    public static string? Owner(string directory, IEnumerable<string> roots, bool ignoreCase = false)
    {
        string dir = Normalize(directory, ignoreCase);
        string? best = null;
        int bestLength = -1;
        foreach (string root in roots)
        {
            string r = Normalize(root, ignoreCase);
            bool inside = dir.Length == r.Length ? dir == r : dir.Length > r.Length && dir[r.Length] == '/' && dir.StartsWith(r, StringComparison.Ordinal);
            if (inside && r.Length > bestLength) (best, bestLength) = (root, r.Length);
        }
        return best;
    }

    /// <summary>A ref name argv can carry safely: a branch name <see cref="WorktreeLauncher.BranchProblem"/> accepts.</summary>
    public static bool IsSafeBranch(string name) => WorktreeLauncher.BranchProblem(name) is null;
}

/// <summary>Whether the git in use has <c>merge-tree --write-tree</c> (2.38 and later).</summary>
public static class GitVersion
{
    public static bool SupportsWriteTree(string versionOutput)
    {
        // "git version 2.56.0" / "git version 2.40.1.windows.1"
        string[] words = versionOutput.Trim().Split(' ');
        string number = words.Length >= 3 ? words[2] : "";
        string[] parts = number.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor) && (major > 2 || (major == 2 && minor >= 38));
    }
}

/// <summary>What <c>git merge-tree --write-tree</c> says about merging two commits.</summary>
public sealed record ConflictPrediction(bool Known, bool Clean, string? Tree, IReadOnlyList<string> Files, string? Note)
{
    public static ConflictPrediction Unknown(string note) => new(false, false, null, [], note);

    /// <summary>
    /// Parses <c>merge-tree --write-tree --name-only --no-messages -z</c>: the tree, then (on exit 1) the conflicted
    /// files, NUL terminated. Anything but exit 0 or 1 is an error, reported as unknown.
    /// </summary>
    public static ConflictPrediction Parse(int exitCode, string stdout, string stderr)
    {
        if (exitCode is not (0 or 1))
            return Unknown(stderr.Trim() is { Length: > 0 } s ? s.Split('\n')[0] : $"git merge-tree failed (exit {exitCode})");
        var parts = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Unknown("git merge-tree printed nothing");
        string tree = parts[0].Trim();
        if (exitCode == 0) return new ConflictPrediction(true, true, tree, [], null);
        var files = parts.Skip(1).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return new ConflictPrediction(true, false, tree, files, null);
    }

    public string Summary
    {
        get
        {
            if (!Known) return Note ?? "No conflict prediction";
            if (Clean) return "No conflicts expected";
            return Files.Count == 1 ? $"Would conflict in {Files[0]}" : $"Would conflict in {Files.Count} files";
        }
    }
}

/// <summary>A small helper for human counts.</summary>
public static class Counted
{
    public static string Of(int count, string one, string? many = null) => $"{count} {(count == 1 ? one : many ?? one + "s")}";
}

/// <summary>Keeps user-visible single-line strings free of control characters.</summary>
public static class OneLine
{
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Where(c => !char.IsControl(c))) sb.Append(c);
        return sb.ToString();
    }
}
