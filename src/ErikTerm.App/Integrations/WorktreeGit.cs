using ErikTerm.Ui;

namespace ErikTerm.App.Integrations;

/// <summary>What git says about the focused pane's repository when the worktree overlay opens.</summary>
internal sealed record WorktreeRepo(string MainRoot, string Name, string DefaultBase, IReadOnlyList<string> Branches);

/// <summary>
/// The git half of "New Agent Worktree": reading the repository and creating the worktree. Everything is an argv
/// (<see cref="ToolRunner"/>), branch and base are validated before they reach git, and nothing is ever forced.
/// </summary>
internal static class WorktreeGit
{
    private const int ProbeTimeoutMs = 3000;
    private const int CreateTimeoutMs = 300_000; // a large checkout, or a post-checkout hook

    /// <summary>The repository at <paramref name="cwd"/> (a subdirectory or linked worktree is fine); null with <paramref name="error"/> when there is none.</summary>
    public static WorktreeRepo? Probe(string cwd, out string? error)
    {
        error = null;
        if (ToolRunner.Search("git", Environment.GetEnvironmentVariable("PATH")) is null) { error = "git isn't on PATH"; return null; }
        var common = Git(cwd, ProbeTimeoutMs, "rev-parse", "--git-common-dir");
        if (!common.Ok || common.Stdout.Trim().Length == 0) { error = $"Not in a git repository: {cwd}"; return null; }
        var top = Git(cwd, ProbeTimeoutMs, "rev-parse", "--show-toplevel"); // fails in a bare repository: that's fine
        string root = WorktreeLauncher.MainRoot(Path.GetFullPath(common.Stdout.Trim(), cwd), top.Ok ? top.Stdout.Trim() : null);

        var refs = Git(cwd, ProbeTimeoutMs, "for-each-ref", "--format=%(refname)", "refs/heads");
        var branches = refs.Ok
            ? refs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(r => r.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(r => r["refs/heads/".Length..]).ToList()
            : [];
        var head = Git(cwd, ProbeTimeoutMs, "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD");
        string @base = WorktreeLauncher.ChooseBase(head.Ok ? head.Stdout : null, branches.Contains("main"), branches.Contains("master"));
        return new WorktreeRepo(root, WorktreeLauncher.RepoName(root), @base, branches);
    }

    /// <summary>
    /// Creates the branch and its worktree; null on success, else what went wrong (git's own words when it was git).
    /// The checks come first so a mistake leaves nothing behind.
    /// </summary>
    public static string? Create(WorktreeRequest request)
    {
        string cwd = request.Cwd;
        if (WorktreeLauncher.BranchProblem(request.Branch) is { } problem) return problem;
        if (WorktreeLauncher.BaseProblem(request.Base) is { } baseProblem) return baseProblem;

        var format = Git(cwd, ProbeTimeoutMs, "check-ref-format", "--branch", request.Branch);
        if (!format.Ok || format.Stdout.Trim() != request.Branch) return Failure(format, $"\"{request.Branch}\" isn't a valid branch name");
        if (!Git(cwd, ProbeTimeoutMs, "rev-parse", "--verify", "--quiet", request.Base + "^{commit}").Ok) return $"Unknown base: {request.Base}";
        if (Git(cwd, ProbeTimeoutMs, "show-ref", "--verify", "--quiet", "refs/heads/" + request.Branch).Ok) return $"Branch {request.Branch} already exists";
        if (Directory.Exists(request.Directory) || File.Exists(request.Directory)) return $"{request.Directory} already exists";

        try
        {
            if (Path.GetDirectoryName(request.Directory) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Couldn't create the directory: {ex.Message}";
        }

        // --no-track: a base like origin/main must not become the new branch's upstream (a plain push would go there).
        var add = Git(cwd, CreateTimeoutMs, "worktree", "add", "--no-track", "-b", request.Branch, "--", request.Directory, request.Base);
        return add.Ok ? null : Failure(add, "git worktree add failed");
    }

    private static string Failure(ToolResult result, string fallback)
    {
        if (result.TimedOut) return $"{fallback} (timed out)";
        if (result.StartError is { } start) return $"{fallback}: {start}";
        string detail = result.Stderr.Trim();
        return detail.Length > 0 ? detail : fallback;
    }

    private static ToolResult Git(string cwd, int timeoutMs, params string[] arguments) =>
        ToolRunner.Run(["git", "-C", cwd, .. arguments], null, timeoutMs);
}
