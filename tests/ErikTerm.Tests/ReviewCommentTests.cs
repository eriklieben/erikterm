using System.Text;
using ErikTerm.App.Compare;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;
using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>Comments in <c>erikterm review</c>: the prompt, finding the agent, keeping comments, and the screen behind fakes.</summary>
public class ReviewCommentRulesTests
{
    private static ReviewComment C(string path, int start, int end = 0, string body = "fix", ReviewCommentSide side = ReviewCommentSide.New, string[]? quoted = null, string? commit = null) =>
        new(1, path, side, start, end == 0 ? start : end, quoted ?? [], body, commit);

    [Test]
    public async Task PromptNamesTheBranchAndEveryFileAndLine()
    {
        // Arrange
        PlacedComment[] comments = [new(C("src/b.cs", 9, body: "second", quoted: ["int y;"]), 9), new(C("src/a.cs", 3, 5, "first", quoted: ["int x;"]), 3)];

        // Act
        string prompt = ReviewPrompt.Format("feature", "main", comments);

        // Assert
        await Assert.That(prompt).IsEqualTo("""
            Review comments on branch feature (compared with main). Please address each one, then say what you changed.

            1. src/a.cs:3-5
               > int x;
               first

            2. src/b.cs:9
               > int y;
               second
            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task PromptUsesTheLineTheCommentMovedTo()
    {
        // Arrange
        PlacedComment[] comments = [new(C("a.cs", 10, 12), 40)];

        // Act
        string prompt = ReviewPrompt.Format("f", "main", comments);

        // Assert
        await Assert.That(prompt).Contains("1. a.cs:40-42\n");
    }

    [Test]
    public async Task PromptSaysWhenTheCodeHasChangedSinceTheComment()
    {
        // Arrange
        PlacedComment[] comments = [new(C("a.cs", 7, quoted: ["old text"]), null)];

        // Act
        string prompt = ReviewPrompt.Format("f", "main", comments);

        // Assert
        await Assert.That(prompt).Contains("a.cs:7 (this code has changed since the comment; the quote shows what it was)");
    }

    [Test]
    public async Task PromptMarksRemovedLinesAndCommits()
    {
        // Arrange
        PlacedComment[] comments = [new(C("a.cs", 7, side: ReviewCommentSide.Old, commit: "deadbeef00"), 7)];

        // Act
        string prompt = ReviewPrompt.Format("f", "main", comments);

        // Assert
        await Assert.That(prompt).Contains("removed code: this line number is in the base version; as of commit deadbee");
    }

    [Test]
    public async Task PromptIndentsEveryLineOfABody()
    {
        // Arrange
        PlacedComment[] comments = [new(C("a.cs", 1, body: "one\ntwo"), 1)];

        // Act
        string prompt = ReviewPrompt.Format("f", "main", comments);

        // Assert
        await Assert.That(prompt).Contains("   one\n   two");
    }

    [Test]
    public async Task LocateKeepsTheLineWhenItIsStillThere()
    {
        // Arrange
        var document = TextDocument.FromText("a\nb\nc\nb\n");
        var comment = C("x", 4, quoted: ["b"]);

        // Act
        int? line = ReviewCommentRules.Locate(comment, document);

        // Assert
        await Assert.That(line).IsEqualTo(4);
    }

    [Test]
    public async Task LocateFollowsALineToTheNearestCopy()
    {
        // Arrange
        var document = TextDocument.FromText("x\nx\nb\nx\nx\nx\nx\nb\n");
        var comment = C("x", 6, quoted: ["b"]);

        // Act
        int? line = ReviewCommentRules.Locate(comment, document);

        // Assert
        await Assert.That(line).IsEqualTo(8);
    }

    [Test]
    public async Task LocateIsNullWhenTheLinesAreGone()
    {
        // Arrange
        var document = TextDocument.FromText("a\nb\n");
        var comment = C("x", 1, quoted: ["gone"]);

        // Act
        int? line = ReviewCommentRules.Locate(comment, document);

        // Assert
        await Assert.That(line).IsNull();
    }

    [Test]
    public async Task LocateIgnoresLineEndings()
    {
        // Arrange
        var document = TextDocument.FromText("a\r\nb\r\n");
        var comment = C("x", 2, quoted: ["b"]);

        // Act
        int? line = ReviewCommentRules.Locate(comment, document);

        // Assert
        await Assert.That(line).IsEqualTo(2);
    }

    [Test]
    public async Task CleanBodyDropsTerminalControlAndKeepsNewlines()
    {
        // Arrange
        const string text = "a\e[31m\u0007\r\nb\tc";

        // Act
        string clean = ReviewCommentRules.CleanBody(text);

        // Assert
        await Assert.That(clean).IsEqualTo("a[31m\nb    c");
    }

    [Test]
    public async Task WrapBreaksBetweenWords()
    {
        // Arrange
        const string text = "hello wide world";

        // Act
        var rows = TuiText.Wrap(text, 10);

        // Assert
        await Assert.That(rows.Select(r => text[r.Start..r.End])).IsEquivalentTo(["hello ", "wide world"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AQuestionOnScreenOrFromAHookBlocksSending()
    {
        // Arrange, Act
        bool onScreen = AgentSend.AwaitsAnswer(AgentState.Waiting, AgentSource.Notification, AgentState.Waiting);
        bool hook = AgentSend.AwaitsAnswer(AgentState.Waiting, AgentSource.Hook, AgentState.None);

        // Assert
        await Assert.That(onScreen).IsTrue();
        await Assert.That(hook).IsTrue();
    }

    [Test]
    public async Task AnIdleNotificationDoesNotBlockSending()
    {
        // Arrange, Act
        bool blocked = AgentSend.AwaitsAnswer(AgentState.Waiting, AgentSource.Notification, AgentState.None);

        // Assert
        await Assert.That(blocked).IsFalse();
    }

    [Test]
    public async Task ABracketedPasteIsOneEventEvenAcrossReads()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();

        // Act
        decoder.Feed("\e[200~line one\nsq\e[2"u8, events);
        bool pending = decoder.EscapePending;
        decoder.Feed("01~j"u8, events);

        // Assert
        await Assert.That(pending).IsFalse();
        await Assert.That(events[0].Paste).IsEqualTo("line one\nsq");
        await Assert.That(events[1].Key).IsEqualTo(KeyEvent.OfChar('j'));
    }
}

public class ReviewAgentFinderTests
{
    private const string Root = "/repo/.claude/worktrees/a";

    private static ListedPane P(int id, string? agent, string? path, int tab = 1, int window = 1, string socket = "s", bool tmux = false, string status = "idle") =>
        new(socket, new PaneInfo { PaneId = id, Agent = agent, CurrentPath = path, TabId = tab, TabIndex = tab - 1, WindowId = window, CreatedByTmux = tmux, AgentStatus = agent is null ? null : status });

    private static string? TopLevel(string path) =>
        path.StartsWith("/repo/.claude/worktrees/a", StringComparison.Ordinal) ? Root
        : path.StartsWith("/repo/.claude/worktrees/b", StringComparison.Ordinal) ? "/repo/.claude/worktrees/b"
        : path.StartsWith("/repo", StringComparison.Ordinal) ? "/repo" : null;

    [Test]
    public async Task FindsTheAgentInTheWorktreeAndNamesIt()
    {
        // Arrange
        ListedPane[] panes = [P(1, null, Root), P(2, "claude-code", Root + "/src")];

        // Act
        var targets = ReviewAgentFinder.Targets(panes, Root, TopLevel, null);

        // Assert
        await Assert.That(targets.Single().PaneId).IsEqualTo(2);
        await Assert.That(targets.Single().Agent).IsEqualTo("Claude");
    }

    [Test]
    public async Task AWorktreeNestedInTheRepositoryIsNotTheRepository()
    {
        // Arrange
        ListedPane[] panes = [P(1, "claude-code", "/repo/.claude/worktrees/b")];

        // Act
        var targets = ReviewAgentFinder.Targets(panes, "/repo", TopLevel, null);

        // Assert
        await Assert.That(targets).IsEmpty();
    }

    [Test]
    public async Task APaneWithoutAKnownDirectoryIsNoTarget()
    {
        // Arrange
        ListedPane[] panes = [P(1, "codex", null)];

        // Act
        var targets = ReviewAgentFinder.Targets(panes, Root, TopLevel, null);

        // Assert
        await Assert.That(targets).IsEmpty();
    }

    [Test]
    public async Task TheLeadComesBeforeATeammateEvenWhenTheTeammateIsNearer()
    {
        // Arrange
        ListedPane[] panes = [P(9, null, Root, tab: 1), P(2, "claude-code", Root, tab: 1, tmux: true), P(3, "claude-code", Root, tab: 4)];

        // Act
        var targets = ReviewAgentFinder.Targets(panes, Root, TopLevel, ("s", 9));

        // Assert
        await Assert.That(targets.Select(t => t.PaneId)).IsEquivalentTo([3, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheNearestAgentComesFirst()
    {
        // Arrange
        ListedPane[] panes = [P(9, null, Root, tab: 2), P(1, "codex", Root, window: 2), P(2, "codex", Root, tab: 3), P(3, "codex", Root, tab: 2)];

        // Act
        var targets = ReviewAgentFinder.Targets(panes, Root, TopLevel, ("s", 9));

        // Assert
        await Assert.That(targets.Select(t => t.PaneId)).IsEquivalentTo([3, 2, 1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ThePlaceSaysTheWindowOnlyWhenThereAreSeveral()
    {
        // Arrange
        ListedPane[] one = [P(1, "codex", Root, tab: 2)];
        ListedPane[] two = [P(1, "codex", Root, tab: 2), P(2, null, Root, window: 2)];

        // Act
        var single = ReviewAgentFinder.Targets(one, Root, TopLevel, null).Single().Place;
        var many = ReviewAgentFinder.Targets(two, Root, TopLevel, null).Single().Place;

        // Assert
        await Assert.That(single).StartsWith("Tab 2 · ");
        await Assert.That(many).StartsWith("Window 1 · Tab 2 · ");
    }
}

public class ReviewCommentHostTests
{
    private sealed class TempState : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("etcomments").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static readonly ReviewComment Sample = new(1, "a.cs", ReviewCommentSide.New, 2, 2, ["b"], "note", null);

    private static ReviewCommentHost HostWith(Func<string, IpcRequest, IpcResponse> transport, string directory = "/nonexistent", params string[] sockets) =>
        new(directory, transport, () => [.. sockets], path => path);

    [Test]
    public async Task UnsentCommentsSurviveAndBelongToTheirBranch()
    {
        // Arrange
        using var state = new TempState();
        var host = new ReviewCommentHost(state.Path);

        // Act
        host.Save("/repo", "feature", [Sample]);
        var again = new ReviewCommentHost(state.Path).Load("/repo", "feature");
        var other = host.Load("/repo", "other");

        // Assert
        await Assert.That(again.Single().Body).IsEqualTo("note");
        await Assert.That(again.Single().Quoted).IsEquivalentTo(["b"]);
        await Assert.That(other).IsEmpty();
    }

    [Test]
    public async Task SavingNoCommentsRemovesTheFile()
    {
        // Arrange
        using var state = new TempState();
        var host = new ReviewCommentHost(state.Path);
        host.Save("/repo", "feature", [Sample]);

        // Act
        host.Save("/repo", "feature", []);

        // Assert
        await Assert.That(Directory.GetFiles(state.Path)).IsEmpty();
    }

    [Test]
    public async Task AMalformedFileReadsAsNoComments()
    {
        // Arrange
        using var state = new TempState();
        var host = new ReviewCommentHost(state.Path);
        File.WriteAllText(host.FileFor("/repo", "feature"), "{ not json");

        // Act
        var comments = host.Load("/repo", "feature");

        // Assert
        await Assert.That(comments).IsEmpty();
    }

    [Test]
    public async Task FindAgentsAsksEveryInstanceAndSkipsOneThatIsGone()
    {
        // Arrange
        var host = HostWith((socket, _) => socket == "dead"
            ? throw new IOException("gone")
            : new IpcResponse { Ok = true, Panes = [new PaneInfo { PaneId = 5, Agent = "codex", CurrentPath = "/w", AgentStatus = "working" }] }, sockets: ["dead", "live"]);

        // Act
        var search = host.FindAgents("/w");

        // Assert
        await Assert.That(search.Targets.Single().Socket).IsEqualTo("live");
        await Assert.That(search.Targets.Single().State).IsEqualTo("working");
    }

    [Test]
    public async Task FindAgentsSaysWhenNoErikTermRuns()
    {
        // Arrange
        var host = HostWith((_, _) => throw new InvalidOperationException("no socket to call"));

        // Act
        var search = host.FindAgents("/w");

        // Assert
        await Assert.That(search.Targets).IsEmpty();
        await Assert.That(search.Problem).IsNotNull();
    }

    [Test]
    public async Task SendAsksThePaneByIdToSubmit()
    {
        // Arrange
        IpcRequest? seen = null;
        var host = HostWith((_, request) => { seen = request; return IpcResponse.Success(); });

        // Act
        string? problem = host.Send(new ReviewAgentTarget("s", 7, "Codex", "Tab 1", "idle"), "the prompt");

        // Assert
        await Assert.That(problem).IsNull();
        await Assert.That(seen!.Command).IsEqualTo("send-to-agent");
        await Assert.That(seen.PaneId).IsEqualTo(7);
        await Assert.That(seen.Text).IsEqualTo("the prompt");
        await Assert.That(seen.Submit).IsTrue();
    }

    [Test]
    public async Task SendReportsWhatTheTerminalRefused()
    {
        // Arrange
        var host = HostWith((_, _) => IpcResponse.Failure("That pane no longer runs a coding agent"));

        // Act
        string? problem = host.Send(new ReviewAgentTarget("s", 7, "Codex", "Tab 1", "idle"), "p");

        // Assert
        await Assert.That(problem).IsEqualTo("That pane no longer runs a coding agent");
    }

    [Test]
    public async Task SendSurvivesALostConnection()
    {
        // Arrange
        var host = HostWith((_, _) => throw new TimeoutException("no answer"));

        // Act
        string? problem = host.Send(new ReviewAgentTarget("s", 7, "Codex", "Tab 1", "idle"), "p");

        // Assert
        await Assert.That(problem).Contains("no answer");
    }

    [Test]
    public async Task TheNewRequestFieldCrossesTheWire()
    {
        // Arrange
        var request = new IpcRequest { Command = "send-to-agent", PaneId = 3, Text = "t", Submit = true };

        // Act
        var decoded = IpcJson.DecodeRequest(IpcJson.Encode(request));

        // Assert
        await Assert.That(decoded!.Submit).IsTrue();
    }
}

public class ReviewCommentScreenTests
{
    private const string Left = "1\n2\n3\n4\n5\n6\n7\n8\n9\n", Right = "1\nb\n3\n4\n5\n6\n7\nh\n9\n";

    private sealed class InlineBackground : IBackground
    {
        public void Run<T>(Func<T> work, Action<T> done) => done(work());
    }

    private sealed class Source : IReviewSource
    {
        public string RightText = Right;

        public ReviewSnapshot Load(string? baseOverride) => new()
        {
            State = ReviewState.Ok, Root = "/repo", Branch = "feature", HeadOid = "1111111", Base = "main", BaseOid = "2222222", MergeBase = "3333333",
            Committed = [new ReviewFile("a.ts", null, ReviewChange.Modified, 2, 2)],
            WithUncommitted = [new ReviewFile("a.ts", null, ReviewChange.Modified, 2, 2)],
        };

        public (CompareSide Left, CompareSide Right) Sides(ReviewSnapshot snapshot, ReviewFile file, ReviewScope scope) =>
            (new CompareSide(file.Path, "main", TextDocument.FromText(Left)), new CompareSide(file.Path, "branch", TextDocument.FromText(RightText)));

        public IReadOnlyList<ReviewFile> CommitFiles(ReviewSnapshot snapshot, ReviewCommit commit) => [];
        public ConflictPrediction Predict(ReviewSnapshot snapshot) => new(true, true, null, [], null);
        public IReadOnlyList<string> Branches(ReviewSnapshot snapshot) => [];
        public string? Fetch(ReviewSnapshot snapshot) => null;
        public string? RequestFinish(ReviewSnapshot snapshot) => null;
    }

    private sealed class Host : IReviewCommentHost
    {
        public List<ReviewComment> Stored = [];
        public List<ReviewAgentTarget> Agents = [new("s", 4, "Claude Code", "Tab 2 · ~/w", "idle")];
        public List<string> Sent = [];
        public string? SendProblem;
        public int Finds;

        public IReadOnlyList<ReviewComment> Load(string root, string branch) => Stored;

        public string? Save(string root, string branch, IReadOnlyList<ReviewComment> comments)
        {
            Stored = [.. comments];
            return null;
        }

        public ReviewAgentSearch FindAgents(string root)
        {
            Finds++;
            return new ReviewAgentSearch(Agents);
        }

        public string? Send(ReviewAgentTarget target, string prompt)
        {
            if (SendProblem is null) Sent.Add(prompt);
            return SendProblem;
        }
    }

    private static (ReviewScreen Screen, Host Host, Source Source) Start(Host? host = null)
    {
        host ??= new Host();
        var source = new Source();
        var screen = new ReviewScreen(new InlineBackground(), source, comments: host);
        screen.Start();
        return (screen, host, source);
    }

    private static void Press(ReviewScreen screen, char c) => screen.OnKey(KeyEvent.OfChar(c));

    private static void Comment(ReviewScreen screen, string text)
    {
        Press(screen, 'a');
        foreach (char c in text) Press(screen, c);
        screen.OnKey(new KeyEvent(TuiKey.Enter));
    }

    private static string Draw(ReviewScreen screen, int width = 120, int height = 30)
    {
        var frame = new Frame(width, height);
        screen.Render(frame);
        return frame.AllText();
    }

    [Test]
    public async Task ACommentGoesOnTheCursorLineAndShowsUnderIt()
    {
        // Arrange
        var (screen, _, _) = Start();

        // Act
        Comment(screen, "why b?");
        string text = Draw(screen);

        // Assert
        await Assert.That(screen.Comments.Single().StartLine).IsEqualTo(2);
        await Assert.That(screen.Comments.Single().Quoted).IsEquivalentTo(["b"]);
        await Assert.That(text).Contains("L2  why b?");
        await Assert.That(text).Contains("s send 1 comment");
    }

    [Test]
    public async Task ARangeCoversTheMarkedLines()
    {
        // Arrange
        var (screen, _, _) = Start();
        Press(screen, 'v');
        Press(screen, 'j');
        Press(screen, 'j');

        // Act
        Comment(screen, "all of it");

        // Assert
        await Assert.That(screen.Comments.Single().StartLine).IsEqualTo(2);
        await Assert.That(screen.Comments.Single().EndLine).IsEqualTo(4);
        await Assert.That(screen.Pane.HasMark).IsFalse();
    }

    [Test]
    public async Task EscDropsARangeWithoutClosingTheReview()
    {
        // Arrange
        var (screen, _, _) = Start();
        Press(screen, 'v');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(screen.Pane.HasMark).IsFalse();
        await Assert.That(screen.Done).IsFalse();
    }

    [Test]
    public async Task EditingStartsFromTheOldText()
    {
        // Arrange
        var (screen, host, _) = Start();
        Comment(screen, "one");
        Press(screen, 'j'); // onto the comment

        // Act
        Press(screen, 'a');
        Press(screen, '!');
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Comments.Single().Body).IsEqualTo("one!");
        await Assert.That(host.Stored.Single().Body).IsEqualTo("one!");
    }

    [Test]
    public async Task DeletingTakesTwoPresses()
    {
        // Arrange
        var (screen, host, _) = Start();
        Comment(screen, "one");
        Press(screen, 'j');

        // Act
        Press(screen, 'x');
        int afterOne = screen.Comments.Count;
        Press(screen, 'x');

        // Assert
        await Assert.That(afterOne).IsEqualTo(1);
        await Assert.That(screen.Comments).IsEmpty();
        await Assert.That(host.Stored).IsEmpty();
    }

    [Test]
    public async Task AnotherKeyDisarmsADelete()
    {
        // Arrange
        var (screen, _, _) = Start();
        Comment(screen, "one");
        Press(screen, 'j');
        Press(screen, 'x');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Right));
        Press(screen, 'x');

        // Assert
        await Assert.That(screen.Comments.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EscOnChangedTextAsksFirst()
    {
        // Arrange
        var (screen, _, _) = Start();
        Press(screen, 'a');
        Press(screen, 'h');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));
        bool stillOpen = screen.CommentEditorOpen;
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(stillOpen).IsTrue();
        await Assert.That(screen.CommentEditorOpen).IsFalse();
        await Assert.That(screen.Comments).IsEmpty();
    }

    [Test]
    public async Task APasteIsTextInTheEditorAndNeverCommands()
    {
        // Arrange
        var (screen, _, _) = Start();

        // Act
        screen.OnPaste("sq\nrs");
        bool stillRunning = !screen.Done && !screen.SendConfirmOpen;
        Press(screen, 'a');
        screen.OnPaste("first\nsecond");
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(stillRunning).IsTrue();
        await Assert.That(screen.Comments.Single().Body).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task EmptyTextAddsNothing()
    {
        // Arrange
        var (screen, _, _) = Start();

        // Act
        Comment(screen, "   ");

        // Assert
        await Assert.That(screen.Comments).IsEmpty();
    }

    [Test]
    public async Task CommentsFromAnEarlierRunAreBackUnderTheirLines()
    {
        // Arrange
        var host = new Host { Stored = [new ReviewComment(1, "a.ts", ReviewCommentSide.New, 8, 8, ["h"], "from before", null)] };

        // Act
        var (screen, _, _) = Start(host);
        string text = Draw(screen);

        // Assert
        await Assert.That(text).Contains("L8  from before");
    }

    [Test]
    public async Task ACommentFollowsItsLineAndSaysWhenItIsGone()
    {
        // Arrange
        var host = new Host { Stored = [new ReviewComment(1, "a.ts", ReviewCommentSide.New, 2, 2, ["gone"], "stale", null)] };

        // Act
        var (screen, _, _) = Start(host);
        string text = Draw(screen);

        // Assert
        await Assert.That(text).Contains("L2 outdated  stale");
    }

    [Test]
    public async Task NothingIsSentBeforeEnterInTheConfirmation()
    {
        // Arrange
        var (screen, host, _) = Start();
        Comment(screen, "one");

        // Act
        Press(screen, 's');
        string text = Draw(screen);

        // Assert
        await Assert.That(screen.SendConfirmOpen).IsTrue();
        await Assert.That(host.Sent).IsEmpty();
        await Assert.That(text).Contains("Send 1 comment to:");
        await Assert.That(text).Contains("Claude Code");
        await Assert.That(text).Contains("Tab 2 · ~/w");
    }

    [Test]
    public async Task EscInTheConfirmationSendsNothingAndKeepsTheComments()
    {
        // Arrange
        var (screen, host, _) = Start();
        Comment(screen, "one");
        Press(screen, 's');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(screen.SendConfirmOpen).IsFalse();
        await Assert.That(host.Sent).IsEmpty();
        await Assert.That(screen.Comments.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EnterSendsOnePromptAndClearsTheComments()
    {
        // Arrange
        var (screen, host, _) = Start();
        Comment(screen, "one");
        Press(screen, 's');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Sent.Single()).Contains("1. a.ts:2\n");
        await Assert.That(host.Sent.Single()).Contains("   one");
        await Assert.That(screen.Comments).IsEmpty();
        await Assert.That(host.Stored).IsEmpty();
        await Assert.That(screen.Status).Contains("Sent 1 comment to Claude Code");
    }

    [Test]
    public async Task ThePromptUsesTheLinesAsTheyAreNow()
    {
        // Arrange
        var (screen, host, source) = Start();
        Comment(screen, "one");
        source.RightText = "new\nnew\n" + Right;

        // Act
        Press(screen, 's');
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Sent.Single()).Contains("1. a.ts:4\n");
    }

    [Test]
    public async Task WithSeveralAgentsTheChosenOneGetsIt()
    {
        // Arrange
        var host = new Host { Agents = [new("s", 4, "Claude Code", "Tab 2", "idle"), new("s", 5, "Codex", "Tab 3", "idle")] };
        var (screen, _, _) = Start(host);
        Comment(screen, "one");
        Press(screen, 's');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Down));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Status).Contains("Codex");
    }

    [Test]
    public async Task WithoutAnAgentItSaysSoAndKeepsTheComments()
    {
        // Arrange
        var host = new Host { Agents = [] };
        var (screen, _, _) = Start(host);
        Comment(screen, "one");

        // Act
        Press(screen, 's');

        // Assert
        await Assert.That(screen.SendConfirmOpen).IsFalse();
        await Assert.That(screen.Status).Contains("No coding agent is running in this worktree");
        await Assert.That(screen.Status).Contains("your comment is kept");
        await Assert.That(screen.Comments.Count).IsEqualTo(1);
        await Assert.That(host.Stored.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ARefusedSendKeepsTheComments()
    {
        // Arrange
        var host = new Host { SendProblem = "Claude Code is waiting for an answer: answer it there first, then send again" };
        var (screen, _, _) = Start(host);
        Comment(screen, "one");
        Press(screen, 's');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Comments.Count).IsEqualTo(1);
        await Assert.That(screen.Status).Contains("is waiting for an answer");
        await Assert.That(screen.SendConfirmOpen).IsFalse();
    }

    [Test]
    public async Task SendingWithNoCommentsAsksNobody()
    {
        // Arrange
        var (screen, host, _) = Start();

        // Act
        Press(screen, 's');

        // Assert
        await Assert.That(host.Finds).IsEqualTo(0);
        await Assert.That(screen.SendConfirmOpen).IsFalse();
    }

    [Test]
    public async Task EnterOutsideTheConfirmationSendsNothing()
    {
        // Arrange
        var (screen, host, _) = Start();
        Comment(screen, "one");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnKey(new KeyEvent(TuiKey.Tab));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Sent).IsEmpty();
    }

    [Test]
    public async Task ChangeStepsStillMoveFromACommentRow()
    {
        // Arrange
        var (screen, _, _) = Start();
        Comment(screen, "one");
        Press(screen, 'j'); // onto the comment under the first change

        // Act
        Press(screen, ']');
        Draw(screen);

        // Assert
        await Assert.That(screen.Pane.CursorNote).IsNull();
        await Assert.That(screen.Pane.SelectedRows()!.Single().Right).IsEqualTo(7);
    }

    [Test]
    public async Task TheScreenAsksForBracketedPaste()
    {
        // Arrange
        var (screen, _, _) = Start();

        // Act
        bool wanted = ((ITuiScreen)screen).WantsPaste;

        // Assert
        await Assert.That(wanted).IsTrue();
    }

    [Test]
    public async Task EditorMovesOverAWideCharacterAsOne()
    {
        // Arrange
        var editor = new CommentEditor("a😀");

        // Act
        editor.OnKey(new KeyEvent(TuiKey.Backspace), 20);

        // Assert
        await Assert.That(editor.Text).IsEqualTo("a");
        await Assert.That(Encoding.UTF8.GetByteCount(editor.Text)).IsEqualTo(1);
    }
}
