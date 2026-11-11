using System.Net.Sockets;
using System.Text;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.Ui;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.Compare;

/// <summary><c>erikterm review [--base BRANCH]</c>: a local PR review of this branch in the terminal.</summary>
internal static class ReviewTool
{
    public const string Usage = """
        usage: erikterm review [--base BRANCH]
          review this branch against its base, merge-base diff, like a pull request
        """;

    public static int Start(string[] args)
    {
        if (args is ["-h" or "--help"]) { Console.WriteLine(Usage); return 0; }
        string? @base = null;
        bool pickBase = false;
        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i++];
            if (arg == "--base" && i < args.Length) @base = args[i++];
            else if (arg.StartsWith("--base=", StringComparison.Ordinal)) @base = arg["--base=".Length..];
            else if (arg == "--pick-base") pickBase = true;
            else
            {
                Console.Error.WriteLine($"erikterm review: unknown argument '{arg}'\n{Usage}");
                return 2;
            }
        }
        string cwd = Directory.GetCurrentDirectory();
        if (GitTool.TopLevel(cwd) is null)
        {
            Console.Error.WriteLine($"erikterm review: not in a git repository: {cwd}");
            return 2;
        }
        return TuiHost.Run(console => Run(console, @base, cwd, console, pickBase), "review");
    }

    /// <summary>The program behind a terminal, testable with a fake one.</summary>
    public static int Run(ITuiTerminal terminal, string? @base, string cwd, TuiConsole? console = null, bool pickBase = false)
    {
        var loop = new TuiLoop(terminal);
        console?.CatchSignals(loop.Stop);
        var source = new GitReviewSource(cwd);
        var screen = new ReviewScreen(loop, source, @base, pickBase, new ReviewCommentHost());
        screen.Start();
        loop.Attach(screen);
        string? root = GitTool.TopLevel(cwd);
        if (root is not null) SetReviewVariable(terminal, root);
        try { loop.Run(); }
        finally
        {
            terminal.LeaveFullScreen();
            if (root is not null) SetReviewVariable(terminal, "");
        }
        int signal = console?.StopSignal ?? 0;
        return signal != 0 ? signal : screen.ExitCode;
    }

    /// <summary>Tells the terminal which worktree this pane reviews, so "Compare with main" finds the tab; empty when it ends.</summary>
    private static void SetReviewVariable(ITuiTerminal terminal, string value)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        terminal.Write(Encoding.ASCII.GetBytes($"\e]1337;SetUserVar={ReviewTabs.UserVariable}={encoded}\a"));
    }
}

/// <summary>
/// The review's git side. Every command runs at the top level of the worktree, as an argv, with optional locks off and
/// the index refresh turned off, so nothing here writes to the repository: <see cref="Predict"/> keeps merge-tree's
/// objects in a temporary directory, and only <see cref="Fetch"/>, on request, changes anything.
/// </summary>
internal sealed class GitReviewSource(string cwd) : IReviewSource
{
    private const string RevParse = "rev-parse";
    private const string Quiet = "--quiet";
    private const string EndOfOptions = "--end-of-options";
    private const string NoColor = "--no-color";

    private const int FetchTimeoutMs = 120_000, MergeTreeTimeoutMs = 20_000, MaxCommits = 2000;
    /// <summary>Untracked files are counted by reading them: at most this many and only this big.</summary>
    private const int MaxCountedUntracked = 4000, MaxCountedBytes = 1 << 20;

    private static GitResult Git(string root, params string[] arguments)
    {
        IReadOnlyList<string> all = ["-c", "core.fsmonitor=", "-c", "diff.autoRefreshIndex=false", .. arguments];
        return GitTool.Run(root, all);
    }

    private static string Text(GitResult r) => Encoding.UTF8.GetString(r.Stdout);

    private static string? FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        string firstLine = newline < 0 ? text : text[..newline];
        return firstLine.Trim() is { Length: > 0 } line ? line : null;
    }

    public ReviewSnapshot Load(string? baseOverride)
    {
        if (GitTool.TopLevel(cwd) is not { } root) return ReviewSnapshot.Failed(ReviewState.NotARepository, $"Not in a git repository: {cwd}");

        var head = Git(root, RevParse, "--verify", Quiet, "HEAD^{commit}");
        if (!head.Ok) return ReviewSnapshot.Failed(ReviewState.NoCommits, "This repository has no commits yet.");
        string headOid = head.Text;

        var symbolic = Git(root, "symbolic-ref", Quiet, "--short", "HEAD");
        string branch = symbolic.Ok ? symbolic.Text : $"HEAD at {headOid[..7]}";

        bool Exists(string name) => Git(root, RevParse, "--verify", Quiet, EndOfOptions, name + "^{commit}").Ok;

        string defaultBase = WorktreeGit.Probe(root, out _)?.DefaultBase ?? "HEAD";
        if (ReviewRules.ChooseBase(baseOverride, defaultBase, Exists) is not { } @base)
            return ReviewSnapshot.Failed(ReviewState.NoBase, "This repository has no main or master branch to compare with.");
        if (!Exists(@base)) return ReviewSnapshot.Failed(ReviewState.UnknownBase, $"No such branch: {@base}");

        string baseOid = Git(root, RevParse, "--verify", Quiet, EndOfOptions, @base + "^{commit}").Text;
        var mergeBase = Git(root, "merge-base", baseOid, headOid);
        if (!mergeBase.Ok) return ReviewSnapshot.Failed(ReviewState.Unrelated, $"{branch} and {@base} share no history.");
        string baseCommon = mergeBase.Text.Split('\n')[0];

        var committed = Diff(root, baseCommon, headOid);
        var (operation, unmerged) = Operation(root);
        var tracked = Diff(root, baseCommon).Select(f => unmerged.Contains(f.Path) ? f with { Change = ReviewChange.Unmerged } : f).ToList();
        var untracked = Untracked(root);
        var uncommittedPaths = UncommittedPaths(root, untracked);
        var withUncommitted = ReviewRules.MergeUncommitted(tracked, untracked, uncommittedPaths);

        string? remoteBase = ReviewRules.RemoteBaseOf(@base, Exists);
        int behind = remoteBase is null ? 0 : BehindCount(root, @base, remoteBase);

        return new ReviewSnapshot
        {
            State = ReviewState.Ok,
            Root = root,
            Branch = branch,
            OnBase = branch == @base,
            HeadOid = headOid,
            Base = @base,
            BaseOid = baseOid,
            MergeBase = baseCommon,
            Committed = committed,
            WithUncommitted = withUncommitted,
            UncommittedCount = withUncommitted.Count(f => f.Uncommitted),
            Commits = Commits(root, baseOid, headOid),
            BehindCount = behind,
            RemoteBase = remoteBase,
            HasOrigin = Git(root, "remote", "get-url", "origin").Ok,
            LinkedWorktree = IsLinked(root),
            Operation = operation,
        };
    }

    private static List<ReviewFile> Diff(string root, params string[] revisions)
    {
        string[] common = ["diff", "--no-ext-diff", "--no-textconv", NoColor, "-M", "-z"];
        var status = Git(root, [.. common, "--name-status", .. revisions, "--"]);
        var counts = Git(root, [.. common, "--numstat", .. revisions, "--"]);
        return ReviewParser.WithCounts(ReviewParser.NameStatus(Text(status)), ReviewParser.Numstat(Text(counts)));
    }

    private static List<ReviewFile> Untracked(string root)
    {
        var r = Git(root, "ls-files", "--others", "--exclude-standard", "-z");
        var files = new List<ReviewFile>();
        if (!r.Ok) return files;
        int counted = 0;
        foreach (string path in Text(r).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith('/')) continue; // a nested repository
            var (added, binary) = counted++ < MaxCountedUntracked ? CountLines(Path.Combine(root, path)) : (-1, false);
            files.Add(new ReviewFile(path, null, ReviewChange.Added, Math.Max(0, added), 0, binary));
        }
        return files;
    }

    /// <summary>Lines of a file too new for git to have counted; (-1, false) when it's big or unreadable.</summary>
    private static (int Lines, bool Binary) CountLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) return (1, false);
            if (!info.Exists || info.Length > MaxCountedBytes) return (-1, false);
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0)) return (-1, true);
            int lines = bytes.AsSpan().Count((byte)'\n');
            return (lines + (bytes.Length > 0 && bytes[^1] != (byte)'\n' ? 1 : 0), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-1, false); }
    }

    private static HashSet<string> UncommittedPaths(string root, List<ReviewFile> untracked)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var r = Git(root, "diff", "--no-ext-diff", NoColor, "-M", "--name-only", "-z", "HEAD", "--");
        if (r.Ok) foreach (string path in Text(r).Split('\0', StringSplitOptions.RemoveEmptyEntries)) set.Add(path);
        foreach (var f in untracked) set.Add(f.Path);
        return set;
    }

    private static int BehindCount(string root, string @base, string remoteBase)
    {
        var r = Git(root, "rev-list", "--count", EndOfOptions, $"{@base}..{remoteBase}");
        return r.Ok && int.TryParse(r.Text, out int n) ? n : 0;
    }

    private static List<ReviewCommit> Commits(string root, string baseOid, string headOid)
    {
        var r = Git(root, "-c", "i18n.logOutputEncoding=UTF-8", "log", "-z", NoColor, "--no-show-signature", $"-n{MaxCommits}",
            "--format=%H%x00%P%x00%h%x00%an%x00%ar%x00%s", $"{baseOid}..{headOid}", "--");
        return r.Ok ? ReviewParser.Commits(Text(r)) : [];
    }

    private static bool IsLinked(string root)
    {
        var r = Git(root, RevParse, "--git-dir", "--git-common-dir");
        var lines = Text(r).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return r.Ok && lines.Length == 2 && Path.GetFullPath(lines[0], root) != Path.GetFullPath(lines[1], root);
    }

    /// <summary>A merge or rebase in progress in words, and its unmerged files (against a commit git reports those as modified).</summary>
    private static (string? Text, HashSet<string> Unmerged) Operation(string root)
    {
        if (WorktreeManagerGit.StatePaths(root) is not { } paths) return (null, []);
        var (state, conflicts) = WorktreeManagerGit.Probe(root, paths);
        return state.Kind == GitOperationKind.None ? (null, []) : (GitOperationDetector.BarText(state, conflicts.Count), [.. conflicts]);
    }

    // ---- Sides ----------------------------------------------------------------------------------

    public (CompareSide Left, CompareSide Right) Sides(ReviewSnapshot snapshot, ReviewFile file, ReviewScope scope)
    {
        string leftPath = file.OldPath ?? file.Path;
        string leftRev, leftLabel;
        if (scope.Kind == ReviewScopeKind.Commit)
        {
            leftRev = scope.Commit!.Parent ?? "";
            leftLabel = scope.Commit.Parent is { } parent ? parent[..7] : "(none)";
        }
        else
        {
            leftRev = snapshot.MergeBase;
            leftLabel = snapshot.Base;
        }

        CompareSide left = file.Change == ReviewChange.Added || leftRev.Length == 0
            ? new CompareSide(leftPath, leftLabel, TextDocument.Empty, Absent: true)
            : ReadRevision(snapshot.Root, leftRev, leftPath, leftLabel, filters: scope.Kind == ReviewScopeKind.Uncommitted);

        CompareSide right;
        if (file.Change == ReviewChange.Deleted)
            right = new CompareSide(file.Path, snapshot.Branch, TextDocument.Empty, Absent: true);
        else if (scope.Kind == ReviewScopeKind.Uncommitted)
            right = ReadWorkTree(snapshot.Root, file.Path);
        else if (scope.Kind == ReviewScopeKind.Commit)
            right = ReadRevision(snapshot.Root, scope.Commit!.Sha, file.Path, scope.Commit.ShortSha, filters: false);
        else
            right = ReadRevision(snapshot.Root, snapshot.HeadOid, file.Path, snapshot.Branch, filters: false);

        return (left, right);
    }

    /// <summary>A blob as text. <paramref name="filters"/> applies the work tree's conversions, so a comparison with it isn't all line endings.</summary>
    private static CompareSide ReadRevision(string root, string rev, string path, string label, bool filters)
    {
        string spec = $"{rev}:{path}";
        var r = filters ? GitTool.Run(root, "cat-file", "--filters", spec) : GitTool.Show(root, spec);
        if (r.ExitCode == -2) return new CompareSide(path, label, TextDocument.TooLarge(TextDocument.MaxBytes + 1));
        if (r.Ok) return new CompareSide(path, label, TextDocument.Decode(r.Stdout));
        if (r.ExitCode == -1) return CompareSide.Failed(path, "git is not available");
        // A path only on the other side of an unmerged file, or a submodule, which has no blob to show.
        if (r.Stderr.Contains("does not exist", StringComparison.Ordinal) || r.Stderr.Contains("exists on disk", StringComparison.Ordinal))
            return new CompareSide(path, label, TextDocument.Empty, Absent: true);
        return CompareSide.Failed(path, FirstLine(r.Stderr) ?? "couldn't read it from git");
    }

    private static CompareSide ReadWorkTree(string root, string path)
    {
        const string Label = "working tree";
        string full = Path.Combine(root, path);
        try
        {
            var info = new FileInfo(full);
            if (info.LinkTarget is { } link) return new CompareSide(path, Label, TextDocument.FromText(link)); // what git stores for a symlink
            if (Directory.Exists(full)) return CompareSide.Failed(path, "is a folder in the working tree");
            if (!info.Exists) return new CompareSide(path, Label, TextDocument.Empty, Absent: true);
            if (info.Length > TextDocument.MaxBytes) return new CompareSide(path, Label, TextDocument.TooLarge(info.Length));
            return new CompareSide(path, Label, TextDocument.Decode(File.ReadAllBytes(full)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CompareSide.Failed(path, ex.Message);
        }
    }

    public IReadOnlyList<ReviewFile> CommitFiles(ReviewSnapshot snapshot, ReviewCommit commit)
    {
        string[] options = ["--no-ext-diff", "--no-textconv", NoColor, "-M", "-z"];
        string[] Args(string kind) => commit.Parent is { } parent
            ? ["diff", .. options, kind, parent, commit.Sha, "--"]
            : ["diff-tree", "--root", "-r", "--no-commit-id", .. options, kind, commit.Sha, "--"];
        var status = Git(snapshot.Root, Args("--name-status"));
        var counts = Git(snapshot.Root, Args("--numstat"));
        return ReviewParser.WithCounts(ReviewParser.NameStatus(Text(status)), ReviewParser.Numstat(Text(counts)));
    }

    // ---- Prediction, branches, fetch, finish --------------------------------------------------

    public ConflictPrediction Predict(ReviewSnapshot snapshot)
    {
        if (snapshot.MergeBase == snapshot.BaseOid || snapshot.MergeBase == snapshot.HeadOid) return new ConflictPrediction(true, true, null, [], null); // one contains the other
        var version = GitTool.Run(snapshot.Root, "version");
        if (!version.Ok || !GitVersion.SupportsWriteTree(version.Text)) return ConflictPrediction.Unknown("Conflict prediction needs git 2.38 or later");

        string objects = Path.GetFullPath(Text(Git(snapshot.Root, RevParse, "--git-path", "objects")).Trim(), snapshot.Root);
        var quarantine = Directory.CreateTempSubdirectory("etreview");
        try
        {
            // merge-tree writes the merged trees as objects: send them to a throwaway directory so the repository stays untouched.
            var environment = new Dictionary<string, string>
            {
                ["GIT_OBJECT_DIRECTORY"] = quarantine.FullName,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = AlternatePath(objects),
            };
            var r = GitTool.Run(snapshot.Root, ["merge-tree", "--write-tree", "--no-messages", "--name-only", "-z", snapshot.BaseOid, snapshot.HeadOid], environment, MergeTreeTimeoutMs);
            if (r.ExitCode == GitTool.TimedOut) return ConflictPrediction.Unknown("git merge-tree timed out");
            if (r.ExitCode == -1) return ConflictPrediction.Unknown("git is not available");
            return ConflictPrediction.Parse(r.ExitCode, Text(r), r.Stderr);
        }
        finally
        {
            try { quarantine.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a stray directory in the temp folder does no harm */ }
        }
    }

    /// <summary>An entry for git's alternates list: quoted when the path has the list separator in it.</summary>
    private static string AlternatePath(string path)
    {
        if (!path.Contains(Path.PathSeparator) && !path.StartsWith('"')) return path;
        return "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    public IReadOnlyList<string> Branches(ReviewSnapshot snapshot)
    {
        string root = snapshot.Root.Length > 0 ? snapshot.Root : GitTool.TopLevel(cwd) ?? cwd;
        var r = Git(root, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes/origin");
        if (!r.Ok) return [];
        var names = Text(r).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name is not ("origin/HEAD" or "origin"));
        // Local branches first; origin's after them.
        return [.. names.OrderBy(name => name.StartsWith("origin/", StringComparison.Ordinal) ? 1 : 0).ThenBy(name => name, StringComparer.Ordinal)];
    }

    public string? Fetch(ReviewSnapshot snapshot)
    {
        string branch = snapshot.Base.StartsWith("origin/", StringComparison.Ordinal) ? snapshot.Base["origin/".Length..] : snapshot.Base;
        var environment = new Dictionary<string, string>();
        // ssh must not prompt on the terminal this screen is drawing on: it fails and says so instead.
        if (Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") is null && !Git(snapshot.Root, "config", "core.sshCommand").Ok)
            environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        var r = GitTool.Run(snapshot.Root, ["fetch", "origin", EndOfOptions, branch], environment, FetchTimeoutMs);
        if (r.Ok) return null;
        if (r.ExitCode == GitTool.TimedOut) return $"git fetch origin {branch} timed out";
        return FirstLine(r.Stderr) ?? $"git fetch origin {branch} failed";
    }

    public string? RequestFinish(ReviewSnapshot snapshot)
    {
        string? socket = Environment.GetEnvironmentVariable(IpcPaths.SocketVariable);
        if (string.IsNullOrEmpty(socket) || !File.Exists(socket))
            return "Finish needs an erikterm pane: use Git: Finish Worktree in the palette";
        int? pane = int.TryParse(Environment.GetEnvironmentVariable(IpcPaths.PaneVariable), out int id) ? id : null;
        try
        {
            var response = IpcClient.Send(socket, new IpcRequest { Command = "finish-worktree", PaneId = pane, Cwd = snapshot.Root }, TimeSpan.FromSeconds(5));
            return response.Ok ? null : response.Error ?? "erikterm couldn't open Finish Worktree";
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            return $"Couldn't reach erikterm: {ex.Message}";
        }
    }
}
