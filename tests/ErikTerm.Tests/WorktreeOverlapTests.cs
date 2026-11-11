using ErikTerm.App.Integrations;
using ErikTerm.App.Ui;
using ErikTerm.App.Worktrees;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>The file-overlap warning of the worktree list: the intersection, the parsers, when git runs, and real repositories.</summary>
public class WorktreeOverlapTests
{
    private static WorktreeChanges Files(params string[] names) => new(new HashSet<string>(names, StringComparer.Ordinal), false);

    [Test]
    public async Task Each_worktree_names_the_other_and_the_shared_files()
    {
        // Arrange
        var changes = new Dictionary<string, WorktreeChanges> { ["/w/a"] = Files("b.cs", "a.cs", "only-a.cs"), ["/w/b"] = Files("a.cs", "b.cs", "only-b.cs") };

        // Act
        var overlaps = WorktreeOverlap.Intersect(changes);

        // Assert
        await Assert.That(overlaps["/w/a"].Single().Worktree).IsEqualTo("/w/b");
        await Assert.That(overlaps["/w/b"].Single().Worktree).IsEqualTo("/w/a");
        await Assert.That(overlaps["/w/a"].Single().Files).IsEquivalentTo(new[] { "a.cs", "b.cs" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Worktrees_without_a_shared_file_have_no_overlap()
    {
        // Arrange
        var changes = new Dictionary<string, WorktreeChanges> { ["/w/a"] = Files("a.cs"), ["/w/b"] = Files("b.cs"), ["/w/c"] = WorktreeChanges.None };

        // Act
        var overlaps = WorktreeOverlap.Intersect(changes);

        // Assert
        await Assert.That(overlaps.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_worktree_with_the_most_shared_files_comes_first()
    {
        // Arrange
        var changes = new Dictionary<string, WorktreeChanges> { ["/w/a"] = Files("1", "2"), ["/w/b"] = Files("1"), ["/w/c"] = Files("1", "2") };

        // Act
        var overlaps = WorktreeOverlap.Intersect(changes);

        // Assert
        await Assert.That(overlaps["/w/a"].Select(o => o.Worktree)).IsEquivalentTo(new[] { "/w/c", "/w/b" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_cut_off_list_makes_the_overlap_a_lower_bound()
    {
        // Arrange
        var changes = new Dictionary<string, WorktreeChanges> { ["/w/a"] = Files("x") with { Truncated = true }, ["/w/b"] = Files("x") };

        // Act
        var overlaps = WorktreeOverlap.Intersect(changes);

        // Assert
        await Assert.That(overlaps["/w/b"].Single().AtLeast).IsTrue();
    }

    [Test]
    public async Task Names_keep_spaces_and_a_cut_off_output_loses_its_half_name()
    {
        // Act
        var whole = WorktreeOverlap.ParseNames("a b.cs\0c.cs\0", truncated: false);
        var cut = WorktreeOverlap.ParseNames("a b.cs\0c.c", truncated: true);

        // Assert
        await Assert.That(whole.Files).IsEquivalentTo(new[] { "a b.cs", "c.cs" }, CollectionOrdering.Any);
        await Assert.That(cut.Files).IsEquivalentTo(new[] { "a b.cs" }, CollectionOrdering.Any);
        await Assert.That(cut.Truncated).IsTrue();
    }

    [Test]
    public async Task Status_gives_the_commit_and_the_changed_and_untracked_files()
    {
        // Arrange
        string oid = new('a', 40);
        string text = $"# branch.oid {oid}\0# branch.head main\0" +
                      $"1 .M N... 100644 100644 100644 {oid} {oid} dir/a b.cs\0" +
                      $"u UU N... 100644 100644 100644 100644 {oid} {oid} {oid} conflict.cs\0" +
                      "? new file.txt\0";

        // Act
        var (head, changes) = WorktreeOverlap.ParseStatus(text, truncated: false);

        // Assert
        await Assert.That(head).IsEqualTo(oid);
        await Assert.That(changes.Files).IsEquivalentTo(new[] { "dir/a b.cs", "conflict.cs", "new file.txt" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task A_repository_without_commits_has_no_head()
    {
        // Act
        var (head, _) = WorktreeOverlap.ParseStatus("# branch.oid (initial)\0# branch.head main\0", truncated: false);

        // Assert
        await Assert.That(head).IsNull();
    }

    [Test]
    public async Task The_line_stays_short_and_says_what_it_left_out()
    {
        // Arrange
        FileOverlap[] overlaps =
        [
            new("/w/feat-a", ["src/Login.cs", "src/Auth.cs", "x.cs", "y.cs"], false),
            new("/w/feat-b", ["Db.cs"], true),
            new("/w/feat-c", ["z.cs"], false),
        ];

        // Act
        string line = WorktreeOverlap.Describe(overlaps);

        // Assert
        await Assert.That(line).IsEqualTo("Overlaps feat-a: Login.cs, Auth.cs +2; feat-b: Db.cs or more; +1 more");
    }

    [Test]
    public async Task A_file_name_cannot_break_the_row()
    {
        // Act
        string line = WorktreeOverlap.Describe([new FileOverlap("/w/a", ["evil\n\e[31m.cs"], false)]);

        // Assert
        await Assert.That(line.Any(char.IsControl)).IsFalse();
    }

    // ---- When git runs -------------------------------------------------------------------------

    private static WorktreeEntry Entry(string path) => new(path, new string('a', 40), "b", false, false, null, null, false);

    private static WorktreeRowStatus Status(int changes) => new(changes, 0, 0, false);

    private sealed class Probe
    {
        public readonly List<string> Reads = [];
        public int Changed;
        public readonly WorktreeOverlaps Sut;

        public Probe(WorktreeChanges? changes = null)
        {
            var read = new WorktreeChangesRead(changes ?? Files("same.cs"), null);
            Sut = new WorktreeOverlaps((work, then) => then(work()), () => Changed++, (path, _, _) => { Reads.Add(path); return read; });
        }
    }

    [Test]
    public async Task An_unchanged_worktree_is_not_read_again()
    {
        // Arrange
        var probe = new Probe();
        WorktreeEntry[] entries = [Entry("/w/a")];

        // Act
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 0);
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 1000);

        // Assert
        await Assert.That(probe.Reads.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_changed_row_status_reads_that_worktree_alone()
    {
        // Arrange
        var probe = new Probe();
        WorktreeEntry[] entries = [Entry("/w/a"), Entry("/w/b")];
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 0);
        probe.Reads.Clear();

        // Act
        probe.Sut.Refresh(entries, "target", e => Status(e.Path == "/w/b" ? 2 : 1), now: 1000);

        // Assert
        await Assert.That(probe.Reads).IsEquivalentTo(new[] { "/w/b" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task An_unchanged_worktree_is_still_rechecked_now_and_then()
    {
        // Arrange
        var probe = new Probe();
        WorktreeEntry[] entries = [Entry("/w/a")];
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 0);

        // Act
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 31_000);

        // Assert
        await Assert.That(probe.Reads.Count).IsEqualTo(2);
    }

    [Test]
    public async Task The_refresh_key_reads_everything_again()
    {
        // Arrange
        var probe = new Probe();
        WorktreeEntry[] entries = [Entry("/w/a"), Entry("/w/b")];
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 0);

        // Act
        probe.Sut.Forget();
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 10);

        // Assert
        await Assert.That(probe.Reads.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Nothing_is_read_without_a_row_status_or_for_a_bare_or_missing_worktree()
    {
        // Arrange
        var probe = new Probe();
        WorktreeEntry[] entries = [Entry("/w/a") with { Bare = true }, Entry("/w/b") with { Prunable = "gone" }, Entry("/w/c"), Entry("/w/d")];

        // Act
        probe.Sut.Refresh(entries, "target", e => e.Path switch { "/w/c" => null, "/w/d" => new WorktreeRowStatus(0, 0, 0, TimedOut: true), _ => Status(1) }, now: 0);

        // Assert
        await Assert.That(probe.Reads.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Only_one_read_runs_at_a_time()
    {
        // Arrange
        Action? queued = null;
        int reads = 0;
        var sut = new WorktreeOverlaps((work, then) => queued = () => then(work()), () => { }, (_, _, _) => { reads++; return new WorktreeChangesRead(Files("x"), null); });
        WorktreeEntry[] entries = [Entry("/w/a")];
        sut.Refresh(entries, "target", _ => Status(1), now: 0);

        // Act
        sut.Refresh(entries, "target", _ => Status(2), now: 10);
        queued!();

        // Assert
        await Assert.That(reads).IsEqualTo(1);
    }

    [Test]
    public async Task A_row_with_an_overlap_warns_and_keeps_its_own_details()
    {
        // Arrange
        var probe = new Probe();
        WorktreeEntry[] entries = [Entry("/w/a"), Entry("/w/b")];
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 0);
        var row = new WorktreeRow { Entry = entries[0], Title = "a", Detail = "~/w/a · no tab", Status = "↑1" };

        // Act
        probe.Sut.Annotate([row]);

        // Assert
        await Assert.That(row.Detail).IsEqualTo("Overlaps b: same.cs · ~/w/a · no tab");
        await Assert.That(row.Status).IsEqualTo("overlap ↑1");
        await Assert.That(row.Warn).IsTrue();
        await Assert.That(probe.Changed).IsEqualTo(1);
    }

    [Test]
    public async Task A_row_without_an_overlap_is_left_alone()
    {
        // Arrange
        var probe = new Probe(WorktreeChanges.None);
        WorktreeEntry[] entries = [Entry("/w/a"), Entry("/w/b")];
        probe.Sut.Refresh(entries, "target", _ => Status(1), now: 0);
        var row = new WorktreeRow { Entry = entries[0], Title = "a", Detail = "d", Status = "clean" };

        // Act
        probe.Sut.Annotate([row]);

        // Assert
        await Assert.That(row.Detail).IsEqualTo("d");
        await Assert.That(row.Status).IsEqualTo("clean");
        await Assert.That(row.Warn).IsFalse();
    }

    // ---- Real git ------------------------------------------------------------------------------

    /// <summary>A repository with one commit on main, and linked worktrees next to it.</summary>
    private sealed class Repo : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("erikterm-overlap-");

        public string Main => Path.Combine(_dir.FullName, "main");

        public Repo()
        {
            Directory.CreateDirectory(Main);
            Git(Main, "init", "-q", "-b", "main");
            Write(Main, "shared.txt", "one\n");
            Write(Main, "other.txt", "one\n");
            Write(Main, "moved.txt", "one\n");
            Write(Main, ".gitignore", "*.log\n");
            Commit(Main, "first");
        }

        public string Add(string branch)
        {
            string path = Path.Combine(_dir.FullName, branch);
            Git(Main, "worktree", "add", "-q", "-b", branch, path, "main");
            return path;
        }

        public string Oid(string @ref = "main") => Git(Main, "rev-parse", @ref).Trim();

        public static void Write(string root, string name, string text)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public static void Commit(string root, string message)
        {
            Git(root, "add", "-A");
            Git(root, "commit", "-q", "-m", message);
        }

        public static string Git(string cwd, params string[] arguments)
        {
            var result = ToolRunner.Run(["git", "-C", cwd, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "-c", "commit.gpgsign=false", .. arguments], null, 30_000);
            if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_dir.FullName, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            _dir.Delete(recursive: true);
        }
    }

    private static IReadOnlySet<string> Changed(Repo repo, string worktree) => WorktreeChangedFiles.Read(worktree, repo.Oid(), null)!.Changes.Files;

    [Test]
    public async Task Commits_staged_edits_and_untracked_files_all_count()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.Add("feat");
        Repo.Write(wt, "committed.txt", "x");
        Repo.Commit(wt, "work");
        Repo.Write(wt, "staged.txt", "x");
        Repo.Git(wt, "add", "staged.txt");
        Repo.Write(wt, "shared.txt", "edited\n");
        Repo.Write(wt, "dir/untracked.txt", "x");
        Repo.Write(wt, "ignored.log", "x");

        // Act
        var files = Changed(repo, wt);

        // Assert
        await Assert.That(files).IsEquivalentTo(new[] { "committed.txt", "staged.txt", "shared.txt", "dir/untracked.txt" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task Two_real_worktrees_overlap_on_the_same_file()
    {
        // Arrange
        using var repo = new Repo();
        string a = repo.Add("feat-a"), b = repo.Add("feat-b");
        Repo.Write(a, "shared.txt", "a\n");
        Repo.Commit(a, "a");
        Repo.Write(b, "shared.txt", "b uncommitted\n");
        Repo.Write(b, "other.txt", "b only\n");
        string target = repo.Oid();
        var changes = new Dictionary<string, WorktreeChanges>
        {
            [a] = WorktreeChangedFiles.Read(a, target, null)!.Changes,
            [b] = WorktreeChangedFiles.Read(b, target, null)!.Changes,
            [repo.Main] = WorktreeChangedFiles.Read(repo.Main, target, null)!.Changes,
        };

        // Act
        var overlaps = WorktreeOverlap.Intersect(changes);

        // Assert
        await Assert.That(overlaps[a].Single().Worktree).IsEqualTo(b);
        await Assert.That(overlaps[b].Single().Files).IsEquivalentTo(new[] { "shared.txt" }, CollectionOrdering.Matching);
        await Assert.That(overlaps.ContainsKey(repo.Main)).IsFalse();
    }

    [Test]
    public async Task Changes_on_the_default_branch_after_the_fork_do_not_count()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.Add("feat");
        Repo.Write(wt, "mine.txt", "x");
        Repo.Commit(wt, "work");
        Repo.Write(repo.Main, "shared.txt", "moved on\n");
        Repo.Commit(repo.Main, "main moved");

        // Act
        var files = Changed(repo, wt);

        // Assert
        await Assert.That(files).IsEquivalentTo(new[] { "mine.txt" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task A_rename_counts_for_both_names()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.Add("feat");
        Repo.Git(wt, "mv", "moved.txt", "renamed.txt");
        Repo.Commit(wt, "rename");
        Repo.Git(wt, "mv", "other.txt", "renamed2.txt");

        // Act
        var files = Changed(repo, wt);

        // Assert
        await Assert.That(files).IsEquivalentTo(new[] { "moved.txt", "renamed.txt", "other.txt", "renamed2.txt" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task A_touched_file_is_not_a_change()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.Add("feat");
        File.SetLastWriteTimeUtc(Path.Combine(wt, "shared.txt"), DateTime.UtcNow.AddHours(1));

        // Act
        var files = Changed(repo, wt);

        // Assert
        await Assert.That(files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_committed_part_is_reused_for_the_same_head_and_target()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.Add("feat");
        Repo.Write(wt, "committed.txt", "x");
        Repo.Commit(wt, "work");
        string target = repo.Oid();
        var first = WorktreeChangedFiles.Read(wt, target, null)!;
        var remembered = first.Committed! with { Changes = Files("remembered.txt") };

        // Act
        var second = WorktreeChangedFiles.Read(wt, target, remembered)!;
        Repo.Write(wt, "next.txt", "x");
        Repo.Commit(wt, "more");
        var afterCommit = WorktreeChangedFiles.Read(wt, target, remembered)!;

        // Assert
        await Assert.That(second.Changes.Files).IsEquivalentTo(new[] { "remembered.txt" }, CollectionOrdering.Any);
        await Assert.That(afterCommit.Changes.Files).IsEquivalentTo(new[] { "committed.txt", "next.txt" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task A_diff_past_the_output_limit_is_bounded_and_says_so()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.Add("feat");
        string padding = new('p', 200);
        for (int i = 0; i < 2000; i++) Repo.Write(wt, $"{padding}-{i:D5}.txt", "x");

        // Act
        var changes = WorktreeChangedFiles.Read(wt, repo.Oid(), null)!.Changes;

        // Assert
        await Assert.That(changes.Truncated).IsTrue();
        await Assert.That(changes.Files.Count).IsLessThan(2000);
    }

    [Test]
    public async Task A_directory_that_is_not_a_repository_reads_as_nothing()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("erikterm-overlap-none-");

        // Act
        var read = WorktreeChangedFiles.Read(dir.FullName, new string('a', 40), null);
        dir.Delete();

        // Assert
        await Assert.That(read).IsNull();
    }
}
