using ErikTerm.App.Integrations;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Worktree management: parsing and rules in the pure core, landed/conflict/refusal against real git in temp repositories.</summary>
public class WorktreeManagementTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static WorktreeEntry Entry(string path = "/r/wt", string? branch = "feat", bool isMain = false) => new(path, Sha, branch, false, branch is null, null, null, isMain);

    // ---- Porcelain ----------------------------------------------------------------------------

    [Test]
    public async Task ParseReadsMainAndLinkedWorktrees()
    {
        // Arrange
        string text = "worktree /r\nHEAD aaa\nbranch refs/heads/main\n\nworktree /r wt/x\nHEAD bbb\nbranch refs/heads/feat/x\nlocked because\n\n";

        // Act
        var entries = WorktreeList.Parse(text);

        // Assert
        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0].IsMain).IsTrue();
        await Assert.That(entries[1].Path).IsEqualTo("/r wt/x");
        await Assert.That(entries[1].Branch).IsEqualTo("feat/x");
        await Assert.That(entries[1].Locked).IsEqualTo("because");
    }

    [Test]
    public async Task ParseAcceptsNulSeparatedOutput()
    {
        // Arrange
        string text = "worktree /r\0HEAD aaa\0branch refs/heads/main\0\0worktree /w\0HEAD bbb\0detached\0\0";

        // Act
        var entries = WorktreeList.Parse(text);

        // Assert
        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[1].Detached).IsTrue();
        await Assert.That(entries[1].Branch).IsNull();
    }

    [Test]
    [Arguments("origin/main", "main")]
    [Arguments("main", "main")]
    [Arguments("origin/release/2", "release/2")]
    [Arguments("HEAD", null)]
    public async Task LocalBranchStripsTheRemote(string @base, string? expected)
    {
        // Act
        string? local = WorktreeList.LocalBranch(@base);

        // Assert
        await Assert.That(local).IsEqualTo(expected);
    }

    [Test]
    public async Task OwnerPicksTheDeepestWorktree()
    {
        // Arrange
        string[] roots = ["/r", "/r/.claude/worktrees/x"];

        // Act
        string? owner = WorktreeList.Owner("/r/.claude/worktrees/x/src", roots);

        // Assert
        await Assert.That(owner).IsEqualTo("/r/.claude/worktrees/x");
    }

    [Test]
    public async Task OwnerDoesNotMatchAPrefixOfAName()
    {
        // Arrange
        string[] roots = ["/r"];

        // Act
        string? owner = WorktreeList.Owner("/rx/y", roots);

        // Assert
        await Assert.That(owner).IsNull();
    }

    [Test]
    [Arguments("git version 2.38.0", true)]
    [Arguments("git version 2.40.1.windows.1", true)]
    [Arguments("git version 2.37.3", false)]
    [Arguments("nonsense", false)]
    public async Task WriteTreeNeedsGit238(string version, bool expected)
    {
        // Act
        bool supported = GitVersion.SupportsWriteTree(version);

        // Assert
        await Assert.That(supported).IsEqualTo(expected);
    }

    // ---- Merge-tree output --------------------------------------------------------------------

    [Test]
    public async Task PredictionCleanHasOnlyATree()
    {
        // Act
        var p = ConflictPrediction.Parse(0, "abc123\0", "");

        // Assert
        await Assert.That(p.Clean).IsTrue();
        await Assert.That(p.Tree).IsEqualTo("abc123");
    }

    [Test]
    public async Task PredictionConflictListsFiles()
    {
        // Act
        var p = ConflictPrediction.Parse(1, "abc123\0a.txt\0b c.txt\0", "");

        // Assert
        await Assert.That(p.Clean).IsFalse();
        await Assert.That(p.Files).IsEquivalentTo(new[] { "a.txt", "b c.txt" });
    }

    [Test]
    public async Task PredictionErrorIsUnknown()
    {
        // Act
        var p = ConflictPrediction.Parse(128, "", "fatal: refusing to merge unrelated histories");

        // Assert
        await Assert.That(p.Known).IsFalse();
        await Assert.That(p.Note).Contains("unrelated");
    }

    // ---- Landed decision ----------------------------------------------------------------------

    private static LandedEvidence Ev(bool? ancestor = false, int? total = null, int? unmatched = null, bool? unchanged = null, bool? net = null, int? pr = null, string? prHead = null) =>
        new() { Target = "main", Head = Sha, Ancestor = ancestor, CherryTotal = total, CherryUnmatched = unmatched, MergeUnchanged = unchanged, NetChange = net, MergedPrNumber = pr, MergedPrHead = prHead };

    [Test]
    public async Task AncestorLands()
    {
        // Act
        var v = Landing.Decide(Ev(ancestor: true));

        // Assert
        await Assert.That(v.Landed).IsTrue();
        await Assert.That(v.Reason).IsEqualTo(LandedReason.Ancestor);
    }

    [Test]
    public async Task AllCherryPicksLand()
    {
        // Act
        var v = Landing.Decide(Ev(total: 2, unmatched: 0));

        // Assert
        await Assert.That(v.Reason).IsEqualTo(LandedReason.PatchesUpstream);
    }

    [Test]
    public async Task MergeTreeUnchangedWithNetChangeLands()
    {
        // Act
        var v = Landing.Decide(Ev(total: 3, unmatched: 3, unchanged: true, net: true));

        // Assert
        await Assert.That(v.Reason).IsEqualTo(LandedReason.MergeTreeUnchanged);
    }

    [Test]
    public async Task MergeTreeUnchangedWithoutNetChangeDoesNotLand()
    {
        // Act
        var v = Landing.Decide(Ev(total: 2, unmatched: 2, unchanged: true, net: false));

        // Assert
        await Assert.That(v.Landed).IsFalse();
    }

    [Test]
    public async Task PartlyPickedDoesNotLand()
    {
        // Act
        var v = Landing.Decide(Ev(total: 3, unmatched: 1));

        // Assert
        await Assert.That(v.Landed).IsFalse();
        await Assert.That(v.Why).Contains("1 commit");
    }

    [Test]
    public async Task MergedPrOnlyLandsForTheExactCommit()
    {
        // Act
        var other = Landing.Decide(Ev(pr: 12, prHead: new string('f', 40)));
        var same = Landing.Decide(Ev(pr: 12, prHead: Sha));

        // Assert
        await Assert.That(other.Landed).IsFalse();
        await Assert.That(same.Reason).IsEqualTo(LandedReason.PullRequestMerged);
    }

    [Test]
    public async Task UnknownEvidenceDoesNotLand()
    {
        // Act
        var v = Landing.Decide(Ev(ancestor: null));

        // Assert
        await Assert.That(v.Landed).IsFalse();
    }

    [Test]
    public async Task BestTakesALandedTarget()
    {
        // Arrange
        var first = Ev();
        var second = Ev(ancestor: true) with { Target = "origin/main" };

        // Act
        var v = Landing.Best([first, second]);

        // Assert
        await Assert.That(v.Target).IsEqualTo("origin/main");
    }

    [Test]
    public async Task BlockOrderPutsTheHardReasonsFirst()
    {
        // Arrange
        var landed = new LandedVerdict(true, LandedReason.Ancestor, "x", "main");

        // Act
        var dirty = Landing.Block(Entry(), landed, changes: 2, agentRunning: true, tabOpen: true);
        var agent = Landing.Block(Entry(), landed, changes: 0, agentRunning: true, tabOpen: true);
        var tab = Landing.Block(Entry(), landed, changes: 0, agentRunning: false, tabOpen: true);
        var finish = Landing.Block(Entry(), landed, changes: 0, agentRunning: false, tabOpen: true, ignoreOpenTab: true);
        var main = Landing.Block(Entry(isMain: true), landed, changes: 0, agentRunning: false, tabOpen: false);

        // Assert
        await Assert.That(dirty).IsEqualTo(RemovalBlock.Dirty);
        await Assert.That(agent).IsEqualTo(RemovalBlock.AgentRunning);
        await Assert.That(tab).IsEqualTo(RemovalBlock.TabOpen);
        await Assert.That(finish).IsEqualTo(RemovalBlock.None);
        await Assert.That(main).IsEqualTo(RemovalBlock.MainWorktree);
    }

    [Test]
    public async Task SquashLandedBranchIsDeletedOnlyAfterTheCheck()
    {
        // Arrange
        var squash = new LandedVerdict(true, LandedReason.MergeTreeUnchanged, "x", "main");
        var merged = new LandedVerdict(true, LandedReason.Ancestor, "x", "main");

        // Act
        var forSquash = Landing.Deletion(Entry(), squash, mainOnTarget: true);
        var forMerged = Landing.Deletion(Entry(), merged, mainOnTarget: true);

        // Assert
        await Assert.That(forSquash).IsEqualTo(BranchDeletion.AfterLandedCheck);
        await Assert.That(forMerged).IsEqualTo(BranchDeletion.Merged);
    }

    // ---- State files --------------------------------------------------------------------------

    private static GitOperationState Detect(Dictionary<string, string> files, bool conflicts = false) =>
        GitOperationDetector.Detect(files.ContainsKey, n => files.GetValueOrDefault(n), () => conflicts);

    [Test]
    public async Task DetectReadsRebaseProgress()
    {
        // Arrange
        var files = new Dictionary<string, string> { ["rebase-merge"] = "", ["rebase-merge/msgnum"] = "3\n", ["rebase-merge/end"] = "7\n", ["CHERRY_PICK_HEAD"] = "" };

        // Act
        var state = Detect(files);

        // Assert
        await Assert.That(state).IsEqualTo(new GitOperationState(GitOperationKind.Rebase, 3, 7));
    }

    [Test]
    public async Task DetectReadsApplyBackendProgress()
    {
        // Arrange
        var files = new Dictionary<string, string> { ["rebase-apply"] = "", ["rebase-apply/next"] = "2", ["rebase-apply/last"] = "5" };

        // Act
        var state = Detect(files);

        // Assert
        await Assert.That(state).IsEqualTo(new GitOperationState(GitOperationKind.Rebase, 2, 5));
    }

    [Test]
    public async Task DetectIgnoresPlainGitAm()
    {
        // Arrange
        var files = new Dictionary<string, string> { ["rebase-apply"] = "", ["rebase-apply/applying"] = "" };

        // Act
        var state = Detect(files);

        // Assert
        await Assert.That(state.Kind).IsEqualTo(GitOperationKind.None);
    }

    [Test]
    [Arguments("MERGE_HEAD", GitOperationKind.Merge)]
    [Arguments("CHERRY_PICK_HEAD", GitOperationKind.CherryPick)]
    [Arguments("REVERT_HEAD", GitOperationKind.Revert)]
    public async Task DetectNamesTheOperation(string file, GitOperationKind expected)
    {
        // Arrange
        var files = new Dictionary<string, string> { [file] = "x" };

        // Act
        var state = Detect(files);

        // Assert
        await Assert.That(state.Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task SquashCountsOnlyWithConflicts()
    {
        // Arrange
        var files = new Dictionary<string, string> { ["SQUASH_MSG"] = "x" };

        // Act
        var clean = Detect(files, conflicts: false);
        var conflicted = Detect(files, conflicts: true);

        // Assert
        await Assert.That(clean.Kind).IsEqualTo(GitOperationKind.None);
        await Assert.That(conflicted.Kind).IsEqualTo(GitOperationKind.Squash);
    }

    [Test]
    public async Task BarTextShowsProgressAndConflicts()
    {
        // Act
        string text = GitOperationDetector.BarText(new GitOperationState(GitOperationKind.Rebase, 3, 7), 2);

        // Assert
        await Assert.That(text).IsEqualTo("Rebasing 3/7 · 2 conflicts");
    }

    [Test]
    public async Task NextConflictWrapsAround()
    {
        // Arrange
        string[] files = ["a", "b"];

        // Act
        string? after = GitOperationDetector.NextConflict(files, "b");

        // Assert
        await Assert.That(after).IsEqualTo("a");
    }

    [Test]
    public async Task FirstMarkerFindsTheConflict()
    {
        // Arrange
        string content = "one\ntwo\n<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> b\n";

        // Act
        int line = GitOperationDetector.FirstMarkerLine(content);

        // Assert
        await Assert.That(line).IsEqualTo(3);
    }

    // ---- Commands -----------------------------------------------------------------------------

    [Test]
    public async Task SquashArgvNeverForces()
    {
        // Act
        var argv = WorktreeCommands.SquashMerge("/r x", "feat");

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "git", "-C", "/r x", "merge", "--squash", "--end-of-options", "feat" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RemoveWorktreeNeverPassesForce()
    {
        // Act
        var argv = WorktreeCommands.RemoveWorktree("/r", "/w x");

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "git", "-C", "/r", "worktree", "remove", "--", "/w x" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, "-d")]
    [Arguments(true, "-D")]
    public async Task DeleteBranchUsesCapitalDOnlyAfterTheCheck(bool checkedLanded, string flag)
    {
        // Act
        var argv = WorktreeCommands.DeleteBranch("/r", "feat", checkedLanded);

        // Assert
        await Assert.That(argv).Contains(flag);
    }

    // ---- Finish rules --------------------------------------------------------------------------

    private static FinishFacts Facts(int mainChanges = 0, string? mainBranch = "main", int wtChanges = 0, ConflictPrediction? prediction = null, bool diff = true, bool agent = false) => new()
    {
        RepoName = "r", Worktree = "/w", MainRoot = "/r", Branch = "feat", Target = "main", HeadOid = Sha, TargetOid = Sha,
        MainBranch = mainBranch, MainChanges = mainChanges, WorktreeChanges = wtChanges, Subjects = ["do a thing"], Commits = 1, Files = ["a.txt"],
        Prediction = prediction ?? new ConflictPrediction(true, true, "t", [], null), HasDiff = diff, AgentRunning = agent, HasGh = true, HasOrigin = true,
    };

    [Test]
    public async Task DirtyMainStopsTheMerge()
    {
        // Act
        string? problem = FinishRules.Problem(FinishOption.Squash, Facts(mainChanges: 2), "msg");

        // Assert
        await Assert.That(problem!).Contains("main worktree");
    }

    [Test]
    public async Task MainOnAnotherBranchStops()
    {
        // Act
        string? problem = FinishRules.Problem(FinishOption.Squash, Facts(mainBranch: "other"), "msg");

        // Assert
        await Assert.That(problem!).Contains("other");
    }

    [Test]
    public async Task PredictedConflictBlocksSquashButNotRebase()
    {
        // Arrange
        var f = Facts(prediction: new ConflictPrediction(true, false, "t", ["a"], null));

        // Act
        string? squash = FinishRules.Problem(FinishOption.Squash, f, "msg");
        string? rebase = FinishRules.Problem(FinishOption.Rebase, f, "msg");

        // Assert
        await Assert.That(squash!).Contains("conflict");
        await Assert.That(rebase).IsNull();
    }

    [Test]
    public async Task AgentRunningBlocksEveryOption()
    {
        // Act
        var problems = Enum.GetValues<FinishOption>().Select(o => FinishRules.Problem(o, Facts(agent: true), "msg")).ToList();

        // Assert
        await Assert.That(problems.All(p => p is not null && p.Contains("agent"))).IsTrue();
    }

    [Test]
    public async Task EmptyMessageIsRefusedForSquash()
    {
        // Act
        string? problem = FinishRules.Problem(FinishOption.Squash, Facts(), "   ");

        // Assert
        await Assert.That(problem!).Contains("commit message");
    }

    [Test]
    public async Task SquashStepsMergeThenCommit()
    {
        // Act
        var steps = FinishRules.Steps(FinishOption.Squash, Facts(), "subject\n\nbody");

        // Assert
        await Assert.That(steps.Count).IsEqualTo(2);
        await Assert.That(steps[0].Argv).Contains("--squash");
        await Assert.That(steps[1].Argv).Contains("subject\n\nbody");
    }

    [Test]
    public async Task OptionRoundTripsThroughItsKey()
    {
        // Act
        var parsed = Enum.GetValues<FinishOption>().Select(o => FinishRules.ParseOption(FinishRules.Key(o))).ToList();

        // Assert
        await Assert.That(parsed).IsEquivalentTo(Enum.GetValues<FinishOption>(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task DefaultOptionIsSquash()
    {
        // Act
        var option = FinishRules.ParseOption(null);

        // Assert
        await Assert.That(option).IsEqualTo(FinishOption.Squash);
    }

    [Test]
    public async Task SquashMessageDropsAnAgentsCoAuthorLine()
    {
        // Arrange
        string[] commits = ["Fix the thing\n\nDetails here.\n\nCo-Authored-By: Claude <noreply@anthropic.com>"];

        // Act
        string message = FinishRules.SquashMessage("feat", commits, out int dropped);

        // Assert
        await Assert.That(message).IsEqualTo("Fix the thing\n\nDetails here.");
        await Assert.That(dropped).IsEqualTo(1);
    }

    [Test]
    public async Task SquashMessageKeepsAHumanCoAuthor()
    {
        // Arrange
        string[] commits = ["Fix\n\nCo-authored-by: Ada <ada@example.com>"];

        // Act
        string message = FinishRules.SquashMessage("feat", commits, out int dropped);

        // Assert
        await Assert.That(message).Contains("Ada");
        await Assert.That(dropped).IsEqualTo(0);
    }

    [Test]
    public async Task SquashMessageListsSeveralSubjects()
    {
        // Arrange
        string[] commits = ["one", "two\n\nbody"];

        // Act
        string message = FinishRules.SquashMessage("fix-login-loop", commits, out _);

        // Assert
        await Assert.That(message).IsEqualTo("Fix login loop\n\n- one\n- two");
    }

    [Test]
    public async Task PlanStopsAtAFailedStepAndRetriesThere()
    {
        // Arrange
        var step = new RepoStep(["a"], "/", "a");
        var plan = new RepoPlan("t", [step, new RepoStep(["b"], "/", "b")]);
        plan.StepExited(0);
        plan.Started();

        // Act
        bool more = plan.StepExited(1);
        plan.Retry();

        // Assert
        await Assert.That(more).IsFalse();
        await Assert.That(plan.Index).IsEqualTo(1);
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Waiting);
    }

    [Test]
    public async Task MultilineInputKeepsLineBreaks()
    {
        // Arrange
        var input = new TextInput(100, multiline: true);

        // Act
        input.Insert("a\nb");

        // Assert
        await Assert.That(input.Text).IsEqualTo("a\nb");
    }

    [Test]
    public async Task MoveLineGoesUpAndDown()
    {
        // Arrange
        var input = new TextInput(100, multiline: true);
        input.Insert("abc\nde");

        // Act
        input.MoveLine(-1);
        int up = input.Caret;
        input.MoveLine(1);
        int down = input.Caret;

        // Assert
        await Assert.That(up).IsEqualTo(2);
        await Assert.That(down).IsEqualTo(6);
    }

    // ---- Placement ----------------------------------------------------------------------------

    [Test]
    [Arguments("tab", WorktreePlacement.Tab)]
    [Arguments("split", WorktreePlacement.Split)]
    [Arguments("  SPLIT ", WorktreePlacement.Split)]
    [Arguments(null, WorktreePlacement.Tab)]
    public async Task ParsePlacementReadsTheSetting(string? text, WorktreePlacement expected)
    {
        // Act
        var placement = WorktreeLauncher.ParsePlacement(text);

        // Assert
        await Assert.That(placement).IsEqualTo(expected);
    }

    [Test]
    [Arguments(WorktreePlacement.Tab, false, WorktreePlacement.Tab)]
    [Arguments(WorktreePlacement.Tab, true, WorktreePlacement.Split)]
    [Arguments(WorktreePlacement.Split, false, WorktreePlacement.Split)]
    [Arguments(WorktreePlacement.Split, true, WorktreePlacement.Tab)]
    public async Task AltEnterInvertsThePlacement(WorktreePlacement configured, bool alternate, WorktreePlacement expected)
    {
        // Act
        var placement = WorktreeLauncher.ChoosePlacement(configured, alternate);

        // Assert
        await Assert.That(placement).IsEqualTo(expected);
    }

    [Test]
    public async Task WorktreeOpenInParses()
    {
        // Act
        var settings = SettingsLoader.Parse("worktree_open_in = \"split\"").Settings;

        // Assert
        await Assert.That(settings.WorktreeOpenIn).IsEqualTo("split");
    }

    [Test]
    public async Task WorktreeOpenInRejectsOtherValues()
    {
        // Act
        var result = SettingsLoader.Parse("worktree_open_in = \"window\"");

        // Assert
        await Assert.That(result.Settings.WorktreeOpenIn).IsEqualTo("tab");
        await Assert.That(result.Diagnostics.Any(w => w.Contains("worktree_open_in"))).IsTrue();
    }

    [Test]
    public async Task GitConflictCheckParses()
    {
        // Act
        var settings = SettingsLoader.Parse("git_conflict_check = false").Settings;

        // Assert
        await Assert.That(settings.GitConflictCheck).IsFalse();
    }

    [Test]
    public async Task WorktreeActionsParse()
    {
        // Act
        var parsed = new[] { "ShowWorktrees", "FinishWorktree", "CleanUpWorktrees" }.Select(a => ActionSpec.TryParse(a, out _, out _)).ToList();

        // Assert
        await Assert.That(parsed.All(p => p)).IsTrue();
    }

    // ---- Real git ------------------------------------------------------------------------------

    private sealed class Repo : IDisposable
    {
        public string Parent { get; }
        public string Root { get; }

        public Repo()
        {
            Parent = Directory.CreateTempSubdirectory("etwm repo").FullName;
            string root = Path.Combine(Parent, "my repo");
            Directory.CreateDirectory(root);
            Git(root, "init", "-q", "-b", "main");
            Write(root, "a.txt", "one\ntwo\nthree\n");
            Commit(root, "first");
            Root = Git(root, "rev-parse", "--show-toplevel").Trim();
        }

        public static string Git(string cwd, params string[] arguments)
        {
            var result = ToolRunner.Run(["git", "-C", cwd, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "-c", "commit.gpgsign=false", .. arguments], null, 30_000);
            if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.Stderr}");
            return result.Stdout;
        }

        public static void Write(string dir, string file, string text) => File.WriteAllText(Path.Combine(dir, file), text);

        public static void Commit(string dir, string message)
        {
            Git(dir, "add", "-A");
            Git(dir, "commit", "-q", "-m", message);
        }

        public string AddWorktree(string branch)
        {
            string path = Path.Combine(Parent, "wt " + branch);
            Git(Root, "worktree", "add", "-q", "--no-track", "-b", branch, "--", path, "main");
            return Git(path, "rev-parse", "--show-toplevel").Trim();
        }

        public void Dispose() => Directory.Delete(Parent, recursive: true);
    }

    private static string HeadOf(string cwd) => Repo.Git(cwd, "rev-parse", "HEAD").Trim();

    private static bool WriteTree => WorktreeManagerGit.HasWriteTree();

    private static LandedVerdict Landed(Repo repo, string wt, string branch) =>
        WorktreeManagerGit.Landed(repo.Root, HeadOf(wt), branch, "main", WriteTree, useGh: false);

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task SquashMergedBranchIsLanded()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");
        Repo.Write(wt, "b.txt", "new\nmore\n");
        Repo.Commit(wt, "extend b");
        Repo.Git(repo.Root, "merge", "--squash", "--end-of-options", "feat");
        Repo.Git(repo.Root, "commit", "-q", "-m", "squashed");
        Repo.Write(repo.Root, "c.txt", "unrelated\n");
        Repo.Commit(repo.Root, "later work on main");

        // Act
        var verdict = Landed(repo, wt, "feat");

        // Assert
        await Assert.That(verdict.Landed).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo(LandedReason.MergeTreeUnchanged);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task UnmergedBranchIsNotLanded()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");

        // Act
        var verdict = Landed(repo, wt, "feat");

        // Assert
        await Assert.That(verdict.Landed).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task ACommitThatRevertsItselfIsNotLanded()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");
        File.Delete(Path.Combine(wt, "b.txt"));
        Repo.Commit(wt, "remove b");

        // Act
        var verdict = Landed(repo, wt, "feat");

        // Assert
        await Assert.That(verdict.Landed).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task CherryPickedBranchIsLanded()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");
        Repo.Write(repo.Root, "c.txt", "main moved first\n");
        Repo.Commit(repo.Root, "other work on main");
        Repo.Git(repo.Root, "cherry-pick", "feat");

        // Act
        var verdict = Landed(repo, wt, "feat");

        // Assert
        await Assert.That(verdict.Reason).IsEqualTo(LandedReason.PatchesUpstream);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task PredictionSeesAConflict()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "a.txt", "one\nbranch\nthree\n");
        Repo.Commit(wt, "edit on branch");
        Repo.Write(repo.Root, "a.txt", "one\nmain\nthree\n");
        Repo.Commit(repo.Root, "edit on main");

        // Act
        var prediction = WorktreeManagerGit.Predict(repo.Root, HeadOf(repo.Root), HeadOf(wt), WriteTree);

        // Assert
        await Assert.That(prediction.Known).IsTrue();
        await Assert.That(prediction.Clean).IsFalse();
        await Assert.That(prediction.Files).Contains("a.txt");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task PredictionTouchesNoWorkingFiles()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");

        // Act
        var prediction = WorktreeManagerGit.Predict(repo.Root, HeadOf(repo.Root), HeadOf(wt), WriteTree);

        // Assert
        await Assert.That(prediction.Clean).IsTrue();
        await Assert.That(File.Exists(Path.Combine(repo.Root, "b.txt"))).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task FinishFactsRefuseADirtyMainWorktree()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");
        Repo.Write(repo.Root, "a.txt", "changed\n");
        var inv = WorktreeManagerGit.Inventory(repo.Root, out _)!;
        var entry = inv.Entries.Single(e => e.Path == wt);

        // Act
        var facts = WorktreeManagerGit.Facts(inv, entry, hasGh: false, existingPr: 0, out _)!;
        string? problem = FinishRules.Problem(FinishOption.Squash, facts, "msg");

        // Assert
        await Assert.That(facts.MainChanges).IsEqualTo(1);
        await Assert.That(problem!).Contains("main worktree");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task VerifyStopsWhenTargetMoves()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");
        var inv = WorktreeManagerGit.Inventory(repo.Root, out _)!;
        var facts = WorktreeManagerGit.Facts(inv, inv.Entries.Single(e => e.Path == wt), false, 0, out _)!;
        Repo.Write(repo.Root, "c.txt", "x\n");
        Repo.Commit(repo.Root, "moved");

        // Act
        string? problem = WorktreeManagerGit.VerifyFinish(facts, FinishOption.Squash);

        // Assert
        await Assert.That(problem!).Contains("moved");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task VerifyRemovalRefusesADirtyWorktree()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");
        Repo.Git(repo.Root, "merge", "-q", "--no-ff", "--no-edit", "feat");
        Repo.Write(wt, "dirt.txt", "uncommitted\n");
        var entry = WorktreeManagerGit.Inventory(repo.Root, out _)!.Entries.Single(e => e.Path == wt);

        // Act
        string? problem = WorktreeManagerGit.VerifyRemoval(entry, "main", WriteTree, useGh: false, repo.Root);

        // Assert
        await Assert.That(problem!).Contains("uncommitted");
        await Assert.That(Directory.Exists(wt)).IsTrue();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task VerifyBranchDeleteStopsOnANewCommit()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "b.txt", "new\n");
        Repo.Commit(wt, "add b");
        string head = HeadOf(wt);
        Repo.Git(repo.Root, "merge", "-q", "--no-ff", "--no-edit", "feat");
        Repo.Write(wt, "b.txt", "newer\n");
        Repo.Commit(wt, "more work");

        // Act
        string? problem = WorktreeManagerGit.VerifyBranchDelete(repo.Root, "feat", head, "main", WriteTree, useGh: false);

        // Assert
        await Assert.That(problem!).Contains("any more");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task MergeStateIsDetectedPerWorktree()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "a.txt", "one\nbranch\nthree\n");
        Repo.Commit(wt, "edit on branch");
        Repo.Write(repo.Root, "a.txt", "one\nmain\nthree\n");
        Repo.Commit(repo.Root, "edit on main");
        var merge = ToolRunner.Run(["git", "-C", wt, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "merge", "--no-edit", "main"], null, 30_000); // CI has no git identity

        // Act
        var inWorktree = WorktreeManagerGit.Probe(wt, WorktreeManagerGit.StatePaths(wt)!);
        var inMain = WorktreeManagerGit.Probe(repo.Root, WorktreeManagerGit.StatePaths(repo.Root)!);

        // Assert
        await Assert.That(merge.ExitCode).IsNotEqualTo(0);
        await Assert.That(inWorktree.State.Kind).IsEqualTo(GitOperationKind.Merge);
        await Assert.That(inWorktree.Conflicts).Contains("a.txt");
        await Assert.That(inMain.State.Kind).IsEqualTo(GitOperationKind.None);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task RebaseProgressIsDetected()
    {
        // Arrange
        using var repo = new Repo();
        string wt = repo.AddWorktree("feat");
        Repo.Write(wt, "a.txt", "one\nbranch\nthree\n");
        Repo.Commit(wt, "edit on branch");
        Repo.Write(repo.Root, "a.txt", "one\nmain\nthree\n");
        Repo.Commit(repo.Root, "edit on main");
        ToolRunner.Run(["git", "-C", wt, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "-c", "commit.gpgsign=false", "rebase", "main"], null, 30_000);

        // Act
        var probe = WorktreeManagerGit.Probe(wt, WorktreeManagerGit.StatePaths(wt)!);

        // Assert
        await Assert.That(probe.State.Kind).IsEqualTo(GitOperationKind.Rebase);
        await Assert.That(probe.State.Total).IsEqualTo(1);
        await Assert.That(probe.Conflicts.Count).IsEqualTo(1);
    }
}
