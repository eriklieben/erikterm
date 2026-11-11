using ErikTerm.App.Integrations;
using ErikTerm.App.Notifications;
using ErikTerm.App.Session;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>New Agent Worktree: the pure launcher core, the form, restore safety, and real worktrees in temp repositories.</summary>
public class AgentWorktreeTests
{
    private static WorktreeAgent Agent(string key) => WorktreeLauncher.BuiltIn.Single(a => a.Key == key);

    // ---- Slug and collisions ------------------------------------------------------------------------

    [Test]
    [Arguments("Fix the login bug", "fix-the-login-bug")]
    [Arguments("  Update src/foo.cs: handle NULL!  ", "update-src-foo-cs-handle-null")]
    [Arguments("--- leading dashes", "leading-dashes")]
    [Arguments("日本語のみ", "")]
    [Arguments("", "")]
    public async Task SlugTurnsAPromptIntoLowerCaseWords(string prompt, string expected)
    {
        // Act
        string slug = WorktreeLauncher.Slug(prompt);

        // Assert
        await Assert.That(slug).IsEqualTo(expected);
    }

    [Test]
    public async Task SlugIsCutAtAWordBoundary()
    {
        // Arrange
        string prompt = string.Join(' ', Enumerable.Repeat("refactor", 20));

        // Act
        string slug = WorktreeLauncher.Slug(prompt);

        // Assert
        await Assert.That(slug.Length).IsLessThanOrEqualTo(48);
        await Assert.That(slug.EndsWith("refactor", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SlugIsAlwaysAValidBranchName()
    {
        // Arrange
        string[] prompts = ["a.lock", "x..y", "feature/x", "-rf /", "@{-1}", "HEAD~1", "foo.", ".hidden"];

        // Act
        var problems = prompts.Select(WorktreeLauncher.Slug).Select(WorktreeLauncher.BranchProblem).ToList();

        // Assert
        await Assert.That(problems.All(p => p is null)).IsTrue();
    }

    [Test]
    public async Task UniqueNameKeepsAFreeName()
    {
        // Act
        string name = WorktreeLauncher.UniqueName("fix", _ => false);

        // Assert
        await Assert.That(name).IsEqualTo("fix");
    }

    [Test]
    public async Task AGeneratedNameIsThreeWordsAndAShortCode()
    {
        // Act
        string name = WorktreeLauncher.GeneratedName(42);

        // Assert
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z]+-[a-z]+-[a-z]+-[0-9a-f]{5}$")).IsTrue();
    }

    [Test]
    public async Task AGeneratedNameIsStableForItsSeed()
    {
        // Act
        string first = WorktreeLauncher.GeneratedName(7), second = WorktreeLauncher.GeneratedName(7);

        // Assert
        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task GeneratedNamesDifferBetweenSeeds()
    {
        // Act
        int distinct = Enumerable.Range(0, 200).Select(WorktreeLauncher.GeneratedName).Distinct().Count();

        // Assert
        await Assert.That(distinct).IsEqualTo(200);
    }

    [Test]
    public async Task GeneratedNamesAreValidBranchNames()
    {
        // Act
        var problems = Enumerable.Range(0, 500).Select(WorktreeLauncher.GeneratedName).Select(WorktreeLauncher.BranchProblem).ToList();

        // Assert
        await Assert.That(problems.All(p => p is null)).IsTrue();
    }

    [Test]
    public async Task UniqueNameSuggestsTheFirstFreeSuffix()
    {
        // Arrange
        var taken = new HashSet<string> { "fix", "fix-2", "fix-3" };

        // Act
        string name = WorktreeLauncher.UniqueName("fix", taken.Contains);

        // Assert
        await Assert.That(name).IsEqualTo("fix-4");
    }

    [Test]
    [Arguments("-delete")]
    [Arguments("--force")]
    [Arguments("@{-1}")]
    [Arguments("a b")]
    [Arguments("a..b")]
    [Arguments("a.lock")]
    [Arguments("a/.b")]
    [Arguments("end.")]
    [Arguments("x\u001b[31m")]
    [Arguments("HEAD")]
    [Arguments("")]
    public async Task BadBranchNamesAreRefusedBeforeGit(string name)
    {
        // Act
        string? problem = WorktreeLauncher.BranchProblem(name);

        // Assert
        await Assert.That(problem).IsNotNull();
    }

    [Test]
    [Arguments("feature/x")]
    [Arguments("fix-login-2")]
    [Arguments("v1.2")]
    public async Task OrdinaryBranchNamesAreAccepted(string name)
    {
        // Act
        string? problem = WorktreeLauncher.BranchProblem(name);

        // Assert
        await Assert.That(problem).IsNull();
    }

    // ---- Directory template ---------------------------------------------------------------------------

    [Test]
    public async Task DefaultTemplateLandsNextToTheRepository()
    {
        // Act
        string dir = WorktreeLauncher.ExpandDirectory(WorktreeLauncher.DefaultDirTemplate, "app", "fix", "/src/app", "/home/u");

        // Assert
        await Assert.That(dir).IsEqualTo(Path.GetFullPath("/src/app.worktrees/fix"));
    }

    [Test]
    public async Task BranchSlashesBecomeOneDirectory()
    {
        // Act
        string dir = WorktreeLauncher.ExpandDirectory("wt/{name}", "app", "feature/x", "/src/app", "/home/u");

        // Assert
        await Assert.That(dir).IsEqualTo(Path.GetFullPath("/src/app/wt/feature-x"));
    }

    [Test]
    public async Task AbsoluteTemplateIgnoresTheRepositoryRoot()
    {
        // Act
        string dir = WorktreeLauncher.ExpandDirectory("/tmp/wt/{repo}-{name}", "app", "fix", "/src/app", "/home/u");

        // Assert
        await Assert.That(dir).IsEqualTo(Path.GetFullPath("/tmp/wt/app-fix"));
    }

    [Test]
    public async Task HomeIsExpanded()
    {
        // Act
        string dir = WorktreeLauncher.ExpandDirectory("~/wt/{name}", "app", "fix", "/src/app", "/home/u");

        // Assert
        await Assert.That(dir).IsEqualTo(Path.GetFullPath("/home/u/wt/fix"));
    }

    [Test]
    public async Task ARepositoryNamedLikeAPlaceholderStaysLiteral()
    {
        // Act
        string dir = WorktreeLauncher.ExpandDirectory("/w/{repo}/{name}", "{name}", "fix", "/src/x", "/home/u");

        // Assert
        await Assert.That(dir).IsEqualTo(Path.GetFullPath("/w/{name}/fix"));
    }

    [Test]
    [Arguments("/src/app/.git", null, "/src/app")]
    [Arguments("/src/my repo/.git/", null, "/src/my repo")]
    [Arguments("/src/app.git", null, "/src/app.git")]
    [Arguments("/super/.git/modules/sub", "/super/sub", "/super/sub")]
    public async Task MainRootComesFromTheCommonDirectory(string common, string? top, string expected)
    {
        // Act
        string root = WorktreeLauncher.MainRoot(common, top);

        // Assert
        await Assert.That(root).IsEqualTo(expected);
    }

    [Test]
    public async Task BareRepositoryNameDropsDotGit()
    {
        // Act
        string name = WorktreeLauncher.RepoName("/srv/app.git");

        // Assert
        await Assert.That(name).IsEqualTo("app");
    }

    // ---- Arguments ---------------------------------------------------------------------------------

    [Test]
    [Arguments("--model haiku", new[] { "--model", "haiku" })]
    [Arguments("--model \"x y\"", new[] { "--model", "x y" })]
    [Arguments("--model 'x y'", new[] { "--model", "x y" })]
    [Arguments("a\\ b", new[] { "a b" })]
    [Arguments("--x=\"a \\\" b\"", new[] { "--x=a \" b" })]
    [Arguments("'it'\\''s'", new[] { "it's" })]
    [Arguments("''", new[] { "" })]
    [Arguments("C:\\dir\\x", new[] { "C:\\dir\\x" })]
    [Arguments("$HOME `id` $(id) ; rm -rf /", new[] { "$HOME", "`id`", "$(id)", ";", "rm", "-rf", "/" })]
    [Arguments("   ", new string[0])]
    public async Task ArgumentsParseLikeAShellWithoutOne(string text, string[] expected)
    {
        // Act
        bool ok = WorktreeLauncher.TryParseArguments(text, out var argv, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(argv).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("--x 'open")]
    [Arguments("--x \"open")]
    [Arguments("--x \u001b[31m")]
    public async Task MalformedArgumentsAreReported(string text)
    {
        // Act
        bool ok = WorktreeLauncher.TryParseArguments(text, out _, out string? error);

        // Assert
        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task QuotedArgumentsReadBackUnchanged()
    {
        // Arrange
        string[] argv = ["--model", "x y", "it's", "", "$(id)", "a\"b", "-rf", "back\\slash"];

        // Act
        string joined = WorktreeLauncher.JoinArguments(argv);
        WorktreeLauncher.TryParseArguments(joined, out var parsed, out _);

        // Assert
        await Assert.That(parsed).IsEquivalentTo(argv, CollectionOrdering.Matching);
    }

    // ---- argv per agent ------------------------------------------------------------------------------

    [Test]
    public async Task ClaudeTakesThePromptAfterDoubleDash()
    {
        // Act
        var argv = WorktreeLauncher.AgentArgv(Agent("claude"), ["--model", "haiku"], "fix the bug");

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "claude", "--model", "haiku", "--", "fix the bug" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CodexTakesThePromptAfterDoubleDash()
    {
        // Act
        var argv = WorktreeLauncher.AgentArgv(Agent("codex"), ["--yolo"], "fix the bug");

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "codex", "--yolo", "--", "fix the bug" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CopilotTakesThePromptAsOneInteractiveArgument()
    {
        // Act
        var argv = WorktreeLauncher.AgentArgv(Agent("copilot"), [], "fix the bug");

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "copilot", "--interactive=fix the bug" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("claude", new[] { "claude", "--", "--dangerously-skip-permissions now" })]
    [Arguments("codex", new[] { "codex", "--", "--dangerously-skip-permissions now" })]
    [Arguments("copilot", new[] { "copilot", "--interactive=--dangerously-skip-permissions now" })]
    public async Task APromptStartingWithADashCannotBecomeAFlag(string key, string[] expected)
    {
        // Act
        var argv = WorktreeLauncher.AgentArgv(Agent(key), [], "--dangerously-skip-permissions now");

        // Assert
        await Assert.That(argv).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("update")]
    [Arguments("doctor")]
    [Arguments("-p")]
    public async Task AOneWordPromptCannotSelectASubcommand(string word)
    {
        // Act
        var argv = WorktreeLauncher.AgentArgv(Agent("claude"), [], word);

        // Assert
        await Assert.That(argv[^1]).IsEqualTo(word + " ");
    }

    [Test]
    public async Task AnEmptyPromptJustOpensTheAgent()
    {
        // Act
        var argv = WorktreeLauncher.AgentArgv(Agent("copilot"), ["--banner"], "");

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "copilot", "--banner" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task APromptHoldingThePlaceholderIsNotExpandedAgain()
    {
        // Act
        var argv = WorktreeLauncher.AgentArgv(Agent("copilot"), [], "say {prompt} twice");

        // Assert
        await Assert.That(argv[^1]).IsEqualTo("--interactive=say {prompt} twice");
    }

    [Test]
    public async Task ConfiguredPromptArgsAreUsed()
    {
        // Arrange
        var agents = WorktreeLauncher.MergeAgents([new WorktreeAgentOverride("aider", "Aider", "aider", ["--yes"], ["--message", "{prompt}"])]);

        // Act
        var argv = WorktreeLauncher.AgentArgv(agents.Single(a => a.Key == "aider"), ["--yes"], "hello there");

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "aider", "--yes", "--message", "hello there" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnEmptyCommandRemovesABuiltInAgent()
    {
        // Act
        var agents = WorktreeLauncher.MergeAgents([new WorktreeAgentOverride("codex", null, "", null, null)]);

        // Assert
        await Assert.That(agents.Any(a => a.Key == "codex")).IsFalse();
    }

    // ---- Control characters --------------------------------------------------------------------------

    [Test]
    public async Task PromptControlCharactersAreRemoved()
    {
        // Act
        string prompt = WorktreeLauncher.SanitizePrompt("hello\u001b[31m\u0007 \u0015world\u009b");

        // Assert
        await Assert.That(prompt).IsEqualTo("hello[31m world");
    }

    [Test]
    public async Task PromptLineBreaksBecomeSpaces()
    {
        // Act
        string prompt = WorktreeLauncher.SanitizePrompt("one\r\ntwo\tthree\u2028four");

        // Assert
        await Assert.That(prompt).IsEqualTo("one  two three four");
    }

    [Test]
    public async Task PromptBidiOverridesAreRemoved()
    {
        // Act
        string prompt = WorktreeLauncher.SanitizePrompt("a\u202Eb\u2066c");

        // Assert
        await Assert.That(prompt).IsEqualTo("abc");
    }

    [Test]
    public async Task PromptLengthIsCapped()
    {
        // Act
        string prompt = WorktreeLauncher.SanitizePrompt(new string('x', WorktreeLauncher.MaxPromptChars + 50));

        // Assert
        await Assert.That(prompt.Length).IsEqualTo(WorktreeLauncher.MaxPromptChars);
    }

    // ---- Base branch -------------------------------------------------------------------------------

    [Test]
    [Arguments("refs/remotes/origin/trunk\n", true, true, "origin/trunk")]
    [Arguments(null, true, true, "main")]
    [Arguments(null, false, true, "master")]
    [Arguments(null, false, false, "HEAD")]
    [Arguments("garbage", false, true, "master")]
    public async Task BaseBranchPrefersOriginHead(string? originHead, bool hasMain, bool hasMaster, string expected)
    {
        // Act
        string @base = WorktreeLauncher.ChooseBase(originHead, hasMain, hasMaster);

        // Assert
        await Assert.That(@base).IsEqualTo(expected);
    }

    [Test]
    [Arguments("--orphan")]
    [Arguments("a b")]
    [Arguments("")]
    public async Task BadBasesAreRefused(string @base)
    {
        // Act
        string? problem = WorktreeLauncher.BaseProblem(@base);

        // Assert
        await Assert.That(problem).IsNotNull();
    }

    // ---- Picker -------------------------------------------------------------------------------------

    [Test]
    public async Task OnlyAgentsOnPathAreOffered()
    {
        // Act
        var agents = WorktreeLauncher.Available(WorktreeLauncher.BuiltIn, c => c == "codex" ? "/usr/bin/codex" : null, windows: false);

        // Assert
        await Assert.That(agents.Select(a => a.Key)).IsEquivalentTo(new[] { "codex" });
    }

    [Test]
    public async Task WindowsNeverOffersBatchShims()
    {
        // Act
        var agents = WorktreeLauncher.Available(WorktreeLauncher.BuiltIn, c => @"C:\npm\" + c + ".cmd", windows: true);

        // Assert
        await Assert.That(agents).IsEmpty();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task PickerFollowsARealPath()
    {
        // Arrange
        string bin = Directory.CreateTempSubdirectory("etwt-bin").FullName;
        try
        {
            WriteFakeAgent(Path.Combine(bin, "codex"), Path.Combine(bin, "unused"));

            // Act
            var agents = WorktreeLauncher.Available(WorktreeLauncher.BuiltIn, c => ToolRunner.Search(c, bin), windows: false);

            // Assert
            await Assert.That(agents.Select(a => a.Key)).IsEquivalentTo(new[] { "codex" });
        }
        finally { Directory.Delete(bin, recursive: true); }
    }

    [Test]
    public async Task LastUsedAgentIsPreselected()
    {
        // Act
        var form = Form(lastAgent: "codex");

        // Assert
        await Assert.That(form.Agent!.Key).IsEqualTo("codex");
    }

    [Test]
    public async Task TabCyclesThroughTheAgents()
    {
        // Arrange
        var form = Form();

        // Act
        form.CycleAgent(1);

        // Assert
        await Assert.That(form.Agent!.Key).IsEqualTo("copilot");
    }

    // ---- The form ------------------------------------------------------------------------------------

    private static WorktreeForm Form(string? lastAgent = null, IReadOnlyCollection<string>? branches = null, IReadOnlyList<WorktreeAgent>? agents = null,
        IReadOnlyDictionary<string, string>? remembered = null, Func<string, bool>? exists = null, string? setup = null) =>
        new(new WorktreeFormOptions
        {
            Cwd = "/src/app/sub",
            RepoName = "app",
            MainRoot = "/src/app",
            Home = "/home/u",
            DirTemplate = WorktreeLauncher.DefaultDirTemplate,
            DefaultBase = "main",
            Agents = agents ?? WorktreeLauncher.BuiltIn,
            Branches = branches ?? [],
            PathExists = exists ?? (_ => false),
            LastAgent = lastAgent,
            RememberedArguments = remembered,
            Setup = setup,
            NameSeed = 0x1234,
        });

    private static void Type(WorktreeForm form, WorktreeField field, string text)
    {
        form.Focus = field;
        form.Field(field).Insert(text);
        form.Changed(field);
    }

    [Test]
    public async Task BranchFollowsThePrompt()
    {
        // Arrange
        var form = Form();

        // Act
        Type(form, WorktreeField.Prompt, "Fix the login bug");

        // Assert
        await Assert.That(form.Branch.Text).IsEqualTo("fix-the-login-bug");
    }

    [Test]
    public async Task AnEmptyPromptGetsAGeneratedBranch()
    {
        // Act
        var form = Form();

        // Assert
        await Assert.That(form.Branch.Text).IsEqualTo(WorktreeLauncher.GeneratedName(0x1234));
    }

    [Test]
    public async Task ASuggestedBranchAvoidsExistingOnes()
    {
        // Arrange
        var form = Form(branches: ["fix-it"]);

        // Act
        Type(form, WorktreeField.Prompt, "fix it");

        // Assert
        await Assert.That(form.Branch.Text).IsEqualTo(WorktreeLauncher.GeneratedName(0x1234));
    }

    [Test]
    public async Task ASuggestedBranchAvoidsAnExistingDirectory()
    {
        // Arrange
        var form = Form(exists: p => p == Path.GetFullPath("/src/app.worktrees/fix-it"));

        // Act
        Type(form, WorktreeField.Prompt, "fix it");

        // Assert
        await Assert.That(form.Branch.Text).IsEqualTo(WorktreeLauncher.GeneratedName(0x1234));
    }

    [Test]
    public async Task TypingABranchStopsTheSuggestion()
    {
        // Arrange
        var form = Form();
        Type(form, WorktreeField.Branch, "mine");

        // Act
        Type(form, WorktreeField.Prompt, "something else");

        // Assert
        await Assert.That(form.Branch.Text.EndsWith("mine", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ATakenBranchTypedByHandSaysWhatToTry()
    {
        // Arrange
        var form = Form(branches: ["taken"]);
        form.Branch.Clear();

        // Act
        Type(form, WorktreeField.Branch, "taken");

        // Assert
        await Assert.That(form.Error).Contains("taken-2");
        await Assert.That(form.TrySubmit()).IsNull();
    }

    [Test]
    public async Task AnExistingDirectoryBlocksTheLaunch()
    {
        // Arrange
        var form = Form(exists: p => p == Path.GetFullPath("/src/app.worktrees/mine"));
        form.Branch.Clear();

        // Act
        Type(form, WorktreeField.Branch, "mine");

        // Assert
        await Assert.That(form.Error).Contains("already exists");
    }

    [Test]
    public async Task ArgumentsArePrefilledFromTheConfig()
    {
        // Arrange
        WorktreeAgent[] agents = [new("claude", "Claude Code", "claude", ["--model", "sonnet 4"], ["--", "{prompt}"])];

        // Act
        var form = Form(agents: agents);

        // Assert
        await Assert.That(form.Arguments.Text).IsEqualTo("--model 'sonnet 4'");
    }

    [Test]
    public async Task RememberedArgumentsAreShownInTheField()
    {
        // Act
        var form = Form(remembered: new Dictionary<string, string> { ["claude"] = "--dangerously-skip-permissions" });

        // Assert
        await Assert.That(form.Arguments.Text).IsEqualTo("--dangerously-skip-permissions");
        await Assert.That(form.Preview).Contains("--dangerously-skip-permissions");
    }

    [Test]
    public async Task PermissionBypassingArgumentsAreCalledOut()
    {
        // Act
        var form = Form(remembered: new Dictionary<string, string> { ["claude"] = "--dangerously-skip-permissions" });

        // Assert
        await Assert.That(form.Warning).IsNotNull();
    }

    [Test]
    public async Task OnlyTheVisibleArgumentsAreLaunched()
    {
        // Arrange
        var form = Form(remembered: new Dictionary<string, string> { ["claude"] = "--dangerously-skip-permissions" });
        form.Focus = WorktreeField.Arguments;
        form.Arguments.Clear();
        form.Changed(WorktreeField.Arguments);
        Type(form, WorktreeField.Prompt, "go now");

        // Act
        var request = form.TrySubmit();

        // Assert
        await Assert.That(request!.AgentArgv).IsEquivalentTo(new[] { "claude", "--", "go now" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ArgumentsArePerAgentWhenCycling()
    {
        // Arrange
        var form = Form();
        Type(form, WorktreeField.Arguments, "--model haiku");

        // Act
        form.CycleAgent(1);
        string other = form.Arguments.Text;
        form.CycleAgent(-1);

        // Assert
        await Assert.That(other).IsEqualTo("");
        await Assert.That(form.Arguments.Text).IsEqualTo("--model haiku");
    }

    [Test]
    public async Task PreviewShowsTheExactCommand()
    {
        // Arrange
        var form = Form();
        Type(form, WorktreeField.Arguments, "--model \"x y\"");

        // Act
        Type(form, WorktreeField.Prompt, "fix it");

        // Assert
        await Assert.That(form.Preview).IsEqualTo("claude --model 'x y' -- 'fix it'");
    }

    [Test]
    public async Task NoAgentsBlocksTheLaunch()
    {
        // Act
        var form = Form(agents: []);

        // Assert
        await Assert.That(form.Error).Contains("PATH");
        await Assert.That(form.TrySubmit()).IsNull();
    }

    [Test]
    public async Task ABackgroundLaunchReservesItsNameAndClearsThePrompt()
    {
        // Arrange
        var form = Form();
        Type(form, WorktreeField.Prompt, "fix it");

        // Act
        form.PrepareNext("fix-it");
        Type(form, WorktreeField.Prompt, "fix it");

        // Assert
        await Assert.That(form.Branch.Text).IsEqualTo(WorktreeLauncher.GeneratedName(0x1235));
    }

    [Test]
    public async Task ControlCharactersNeverReachTheAgent()
    {
        // Arrange
        var form = Form();
        form.Prompt.Set("a\u001b]0;x\u0007 b");
        form.Changed(WorktreeField.Prompt);

        // Act
        var request = form.TrySubmit();

        // Assert
        await Assert.That(request!.AgentArgv[^1]).IsEqualTo("a]0;x b");
    }

    [Test]
    public async Task PastedLineBreaksStayOnOneLine()
    {
        // Arrange
        var input = new TextInput(100);

        // Act
        input.Insert("a\nb\tc\u001bd");

        // Assert
        await Assert.That(input.Text).IsEqualTo("a b cd");
    }

    [Test]
    public async Task BackspaceKeepsSurrogatePairsTogether()
    {
        // Arrange
        var input = new TextInput(100);
        input.Insert("a😀");

        // Act
        input.Backspace(word: false);

        // Assert
        await Assert.That(input.Text).IsEqualTo("a");
    }

    [Test]
    public async Task WordBackspaceRemovesTheLastWord()
    {
        // Arrange
        var input = new TextInput(100);
        input.Insert("one two  ");

        // Act
        input.Backspace(word: true);

        // Assert
        await Assert.That(input.Text).IsEqualTo("one ");
    }

    // ---- Stages: setup, agent, shell -----------------------------------------------------------------

    private static readonly string[] Setup = ["/bin/sh", "-c", "make"], AgentCmd = ["claude", "--", "go now"], Shell = ["zsh"];

    [Test]
    public async Task WithoutSetupTheAgentStartsFirst()
    {
        // Act
        var plan = new WorktreeLaunchPlan(null, AgentCmd, Shell);

        // Assert
        await Assert.That(plan.First).IsEquivalentTo(AgentCmd, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SuccessfulSetupStartsTheAgent()
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(Setup, AgentCmd, Shell);

        // Act
        var step = plan.Next(0);

        // Assert
        await Assert.That(step!.Argv).IsEquivalentTo(AgentCmd, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FailedSetupGoesStraightToAShell()
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(Setup, AgentCmd, Shell);

        // Act
        var step = plan.Next(3);

        // Assert
        await Assert.That(step!.Argv).IsEquivalentTo(Shell, CollectionOrdering.Matching);
        await Assert.That(step.Message).Contains("not started");
    }

    [Test]
    public async Task AnUnknownSetupStatusCountsAsFailed()
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(Setup, AgentCmd, Shell);

        // Act
        var step = plan.Next(null);

        // Assert
        await Assert.That(step!.Argv).IsEquivalentTo(Shell, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task AfterTheAgentExitsAShellFollows(int exitCode)
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(null, AgentCmd, Shell);

        // Act
        var step = plan.Next(exitCode);

        // Assert
        await Assert.That(step!.Argv).IsEquivalentTo(Shell, CollectionOrdering.Matching);
        await Assert.That(step.IsLast).IsTrue();
    }

    [Test]
    public async Task AfterTheShellThePlanIsOver()
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(null, AgentCmd, Shell);
        plan.Next(0);

        // Act
        var step = plan.Next(0);

        // Assert
        await Assert.That(step).IsNull();
    }

    [Test]
    public async Task SetupRunsInTheSystemShell()
    {
        // Act
        var argv = WorktreeLauncher.SetupArgv("pnpm i && make", windows: false);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "/bin/sh", "-c", "pnpm i && make" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("app", "own")]
    [Arguments("other", "any")]
    public async Task SetupIsPerRepositoryWithADefault(string repo, string expected)
    {
        // Arrange
        var setup = new Dictionary<string, string> { ["app"] = "own", ["*"] = "any" };

        // Act
        string? command = WorktreeLauncher.SetupFor(setup, repo);

        // Assert
        await Assert.That(command).IsEqualTo(expected);
    }

    // ---- Restore never re-runs a launch ------------------------------------------------------------------

    private static readonly string[] Launched = ["claude", "--dangerously-skip-permissions", "--", "delete it all"];

    [Test]
    public async Task ALaunchedAgentIsNeverSavedEvenWhenListed()
    {
        // Act
        var program = RestorePrograms.Capture("claude", Launched, ["claude"], Launched);

        // Assert
        await Assert.That(program).IsNull();
    }

    [Test]
    public async Task ALaunchedAgentUnderAnInterpreterIsNeverSaved()
    {
        // Arrange
        string[] argv = ["node", "/usr/lib/copilot/index.js", "--dangerously-skip-permissions", "--", "delete it all"];

        // Act
        var program = RestorePrograms.Capture("node", argv, ["node"], Launched);

        // Assert
        await Assert.That(program).IsNull();
    }

    [Test]
    public async Task AWorktreeTabRestoresWithoutTheCommandLine()
    {
        // Act
        var program = RestorePrograms.Capture("claude", Launched, [], Launched);
        var restored = RestoreLaunch.CommandFor(program, ["claude"], ["zsh"]);

        // Assert
        await Assert.That(restored).IsNull();
    }

    [Test]
    public async Task OtherProgramsInAWorktreeTabStillRestore()
    {
        // Act
        var program = RestorePrograms.Capture("nvim", ["nvim", "notes.md"], ["nvim"], Launched);

        // Assert
        await Assert.That(program!.Argv).IsEquivalentTo(new[] { "nvim", "notes.md" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task OrdinaryPanesCaptureAsBefore()
    {
        // Act
        var program = RestorePrograms.Capture("cargo", ["cargo", "build"], ["nvim"], null);

        // Assert
        await Assert.That(program!.Name).IsEqualTo("cargo");
        await Assert.That(program.Argv).IsNull();
    }

    [Test]
    public async Task AShellIsNotSaved()
    {
        // Act
        var program = RestorePrograms.Capture("zsh", ["zsh"], ["zsh"], null);

        // Assert
        await Assert.That(program).IsNull();
    }

    // ---- Tab colors ----------------------------------------------------------------------------------

    [Test]
    public async Task ANewWorktreeTabPicksAnUnusedColor()
    {
        // Arrange
        string[] offered = ["red", "green", "blue"];
        string?[] inUse = ["red", "blue", null];

        // Act
        string color = WorktreeLauncher.ChooseTabColor(inUse, offered, "anything");

        // Assert
        await Assert.That(color).IsEqualTo("green");
    }

    [Test]
    public async Task TheColorIsStableForAName()
    {
        // Arrange
        string[] offered = ["red", "green", "blue"];

        // Act
        string first = WorktreeLauncher.ChooseTabColor([], offered, "fix-it");
        string second = WorktreeLauncher.ChooseTabColor([], offered, "fix-it");

        // Assert
        await Assert.That(first).IsEqualTo(second);
    }

    // ---- Config --------------------------------------------------------------------------------------

    [Test]
    public async Task WorktreeDirDefaultsNextToTheRepository()
    {
        // Act
        var settings = SettingsLoader.Parse("").Settings;

        // Assert
        await Assert.That(settings.GitWorktreeDir).IsEqualTo(WorktreeLauncher.DefaultDirTemplate);
    }

    [Test]
    public async Task AWorktreeDirWithoutNameIsRefused()
    {
        // Act
        var result = SettingsLoader.Parse("git_worktree_dir = \"/tmp/wt\"");

        // Assert
        await Assert.That(result.Settings.GitWorktreeDir).IsEqualTo(WorktreeLauncher.DefaultDirTemplate);
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("{name}", StringComparison.Ordinal));
    }

    [Test]
    public async Task WorktreeAgentsParseFromTables()
    {
        // Arrange
        const string toml = """
            [worktree_agents.claude]
            command = "claude-dev"
            args = ["--model", "sonnet"]

            [worktree_agents.aider]
            name = "Aider"
            command = "aider"
            prompt_args = ["--message", "{prompt}"]
            """;

        // Act
        var result = SettingsLoader.Parse(toml);
        var agents = result.Settings.WorktreeAgents;

        // Assert
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(agents.Single(a => a.Key == "claude").Args!).IsEquivalentTo(new[] { "--model", "sonnet" }, CollectionOrdering.Matching);
        await Assert.That(agents.Single(a => a.Key == "aider").PromptArgs!).IsEquivalentTo(new[] { "--message", "{prompt}" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PromptArgsWithoutThePlaceholderAreReported()
    {
        // Act
        var result = SettingsLoader.Parse("[worktree_agents.x]\ncommand = \"x\"\nprompt_args = [\"--go\"]\n");

        // Assert
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("{prompt}", StringComparison.Ordinal));
    }

    [Test]
    public async Task UnknownAgentKeysAreReported()
    {
        // Act
        var result = SettingsLoader.Parse("[worktree_agents.x]\ncomand = \"x\"\n");

        // Assert
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("did you mean", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task SetupAcceptsOneCommandForEveryRepository()
    {
        // Act
        var settings = SettingsLoader.Parse("git_worktree_setup = \"pnpm i\"").Settings;

        // Assert
        await Assert.That(WorktreeLauncher.SetupFor(settings.GitWorktreeSetup, "any")).IsEqualTo("pnpm i");
    }

    [Test]
    public async Task SetupAcceptsATablePerRepository()
    {
        // Act
        var settings = SettingsLoader.Parse("[git_worktree_setup]\napp = \"make\"\n").Settings;

        // Assert
        await Assert.That(WorktreeLauncher.SetupFor(settings.GitWorktreeSetup, "app")).IsEqualTo("make");
    }

    [Test]
    public async Task TheActionIsKnownAndHasADefaultChord()
    {
        // Act
        bool parsed = ActionSpec.TryParse("NewAgentWorktree", out _, out _);
        var bound = DefaultKeys.Table.Where(t => t.Action == "NewAgentWorktree").Select(t => t.Chord).ToList();

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(bound).IsEquivalentTo(new[] { "CTRL+SHIFT+g" });
    }

    [Test]
    public async Task PreferencesKeepTheLastAgentAndArguments()
    {
        // Arrange
        string dir = Directory.CreateTempSubdirectory("etwt-prefs").FullName;
        string path = Path.Combine(dir, "preferences.json");
        var saved = new PreferencesFile { WorktreeAgent = "codex", WorktreeAgentArguments = new() { ["codex"] = "--yolo" } };

        try
        {
            // Act
            PreferencesStore.Save(path, saved);
            var loaded = PreferencesStore.Load(path);

            // Assert
            await Assert.That(loaded.WorktreeAgent).IsEqualTo("codex");
            await Assert.That(loaded.WorktreeAgentArguments!["codex"]).IsEqualTo("--yolo");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Real repositories, fake agent ---------------------------------------------------------------

    /// <summary>A temporary repository with one commit on main. The path has a space in it on purpose.</summary>
    private sealed class TempRepo : IDisposable
    {
        public string Parent { get; }
        public string Root { get; }

        public TempRepo()
        {
            Parent = Directory.CreateTempSubdirectory("etwt repo").FullName;
            string root = Path.Combine(Parent, "my repo");
            Directory.CreateDirectory(root);
            Run(root, "init", "-q", "-b", "main");
            Run(root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", "first");
            Root = Run(root, "rev-parse", "--show-toplevel").Trim(); // the resolved path, as git prints it
        }

        public static string Run(string cwd, params string[] arguments)
        {
            var result = ToolRunner.Run(["git", "-C", cwd, .. arguments], null, 30_000);
            if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose() => Directory.Delete(Parent, recursive: true);
    }

    private static WorktreeRequest Request(string cwd, string root, string branch, string? @base = "main", WorktreeAgent? agent = null, string prompt = "") =>
        new(branch, WorktreeLauncher.ExpandDirectory(WorktreeLauncher.DefaultDirTemplate, WorktreeLauncher.RepoName(root), branch, root, "/home/u"), @base!,
            agent ?? WorktreeLauncher.BuiltIn[0], "", WorktreeLauncher.AgentArgv(agent ?? WorktreeLauncher.BuiltIn[0], [], prompt), null, cwd);

    /// <summary>A script that records its directory and each argument on a line of <paramref name="record"/>.</summary>
    private static void WriteFakeAgent(string path, string record)
    {
        File.WriteAllText(path, $"#!/bin/sh\npwd > '{record}'\nfor a in \"$@\"; do printf '%s\\n' \"$a\" >> '{record}'; done\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task ProbeFindsTheRepositoryFromASubdirectory()
    {
        // Arrange
        using var repo = new TempRepo();
        string sub = Directory.CreateDirectory(Path.Combine(repo.Root, "a", "b")).FullName;

        // Act
        var found = WorktreeGit.Probe(sub, out _);

        // Assert
        await Assert.That(found!.MainRoot).IsEqualTo(repo.Root);
        await Assert.That(found.Name).IsEqualTo("my repo");
        await Assert.That(found.DefaultBase).IsEqualTo("main");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task ProbeExplainsWhenThereIsNoRepository()
    {
        // Arrange
        string dir = Directory.CreateTempSubdirectory("etwt-none").FullName;
        try
        {
            // Act
            var found = WorktreeGit.Probe(dir, out string? error);

            // Assert
            await Assert.That(found).IsNull();
            await Assert.That(error).Contains("Not in a git repository");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task ProbePrefersOriginHead()
    {
        // Arrange
        using var repo = new TempRepo();
        TempRepo.Run(repo.Root, "branch", "trunk");
        TempRepo.Run(repo.Root, "update-ref", "refs/remotes/origin/trunk", "trunk");
        TempRepo.Run(repo.Root, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/trunk");

        // Act
        var found = WorktreeGit.Probe(repo.Root, out _);

        // Assert
        await Assert.That(found!.DefaultBase).IsEqualTo("origin/trunk");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task CreateMakesTheBranchAndWorktreeNextToTheRepository()
    {
        // Arrange
        using var repo = new TempRepo();
        var request = Request(repo.Root, repo.Root, "fix-it");

        // Act
        string? error = WorktreeGit.Create(request);

        // Assert
        await Assert.That(error).IsNull();
        // The parent as git reports it: on macOS the temp directory is under /var, a symlink to /private/var.
        string parent = Path.GetDirectoryName(TempRepo.Run(repo.Root, "rev-parse", "--show-toplevel").Trim())!;
        await Assert.That(request.Directory).IsEqualTo(Path.Combine(parent, "my repo.worktrees", "fix-it"));
        await Assert.That(TempRepo.Run(request.Directory, "rev-parse", "--abbrev-ref", "HEAD").Trim()).IsEqualTo("fix-it");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task CreateDoesNotTrackARemoteBase()
    {
        // Arrange
        using var repo = new TempRepo();
        TempRepo.Run(repo.Root, "update-ref", "refs/remotes/origin/main", "main");
        var request = Request(repo.Root, repo.Root, "fix-it", "origin/main");

        // Act
        string? error = WorktreeGit.Create(request);
        var upstream = ToolRunner.Run(["git", "-C", request.Directory, "rev-parse", "--abbrev-ref", "@{upstream}"], null, 30_000);

        // Assert
        await Assert.That(error).IsNull();
        await Assert.That(upstream.Ok).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task ALinkedWorktreeCreatesInTheSamePlace()
    {
        // Arrange
        using var repo = new TempRepo();
        var first = Request(repo.Root, repo.Root, "one");
        WorktreeGit.Create(first);

        // Act
        var found = WorktreeGit.Probe(first.Directory, out _);
        var second = Request(first.Directory, found!.MainRoot, "two");
        string? error = WorktreeGit.Create(second);

        // Assert
        await Assert.That(error).IsNull();
        await Assert.That(found.MainRoot).IsEqualTo(repo.Root);
        await Assert.That(Directory.Exists(Path.Combine(repo.Parent, "my repo.worktrees", "two"))).IsTrue();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task AnExistingDirectoryStopsCreate()
    {
        // Arrange
        using var repo = new TempRepo();
        var request = Request(repo.Root, repo.Root, "fix-it");
        Directory.CreateDirectory(request.Directory);

        // Act
        string? error = WorktreeGit.Create(request);

        // Assert
        await Assert.That(error).Contains("already exists");
        await Assert.That(ToolRunner.Run(["git", "-C", repo.Root, "show-ref", "--verify", "--quiet", "refs/heads/fix-it"], null, 30_000).Ok).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task AnExistingBranchStopsCreate()
    {
        // Arrange
        using var repo = new TempRepo();
        TempRepo.Run(repo.Root, "branch", "fix-it");

        // Act
        string? error = WorktreeGit.Create(Request(repo.Root, repo.Root, "fix-it"));

        // Assert
        await Assert.That(error).Contains("already exists");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task AnUnknownBaseIsReported()
    {
        // Arrange
        using var repo = new TempRepo();

        // Act
        string? error = WorktreeGit.Create(Request(repo.Root, repo.Root, "fix-it", "nope"));

        // Assert
        await Assert.That(error).Contains("Unknown base");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task CreateRefusesAnInvalidName()
    {
        // Arrange
        using var repo = new TempRepo();

        // Act
        string? error = WorktreeGit.Create(Request(repo.Root, repo.Root, "bad[name"));

        // Assert
        await Assert.That(error).IsNotNull();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task TheFakeAgentStartsInTheWorktreeWithExactArguments()
    {
        // Arrange
        using var repo = new TempRepo();
        string bin = Directory.CreateDirectory(Path.Combine(repo.Parent, "bin")).FullName;
        string record = Path.Combine(repo.Parent, "record.txt");
        string fake = Path.Combine(bin, "claude");
        WriteFakeAgent(fake, record);
        var agent = WorktreeLauncher.Available(WorktreeLauncher.BuiltIn, c => ToolRunner.Search(c, bin), windows: false).Single();
        const string prompt = "-rf $(touch /tmp/etwt-pwned) `id`; 'quoted' \"double\"";
        var request = Request(repo.Root, repo.Root, "fix-it", agent: agent, prompt: prompt);
        WorktreeGit.Create(request);
        string[] argv = [fake, .. request.AgentArgv.Skip(1)];

        // Act
        var run = ToolRunner.Run(argv, request.Directory, 30_000);
        string[] lines = File.ReadAllLines(record);

        // Assert
        await Assert.That(run.Ok).IsTrue();
        await Assert.That(lines[0]).IsEqualTo(request.Directory);
        await Assert.That(lines.Skip(1)).IsEquivalentTo(new[] { "--", prompt }, CollectionOrdering.Matching);
        await Assert.That(File.Exists("/tmp/etwt-pwned")).IsFalse();
    }
}
