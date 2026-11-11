using ErikTerm.App.Cli;
using ErikTerm.App.Ipc;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>Parsing of new-worktree and wait-for-state.</summary>
public class NewCliCommandParsingTests
{
    private static IpcRequest Req(params string[] args) => CliParser.Parse(args).Request;

    [Test]
    public async Task NewWorktreeCarriesEveryFlag()
    {
        // Act
        var r = Req("new-worktree", "--cwd", "/tmp", "--branch", "fix/it", "--base", "main", "--agent", "claude", "--prompt", "fix it",
            "--agent-args", "--model x", "--split", "--background", "--window-id", "3");

        // Assert
        await Assert.That(r.Command).IsEqualTo("new-worktree");
        await Assert.That(r.Cwd).IsEqualTo(Path.GetFullPath("/tmp"));
        await Assert.That(r.Branch).IsEqualTo("fix/it");
        await Assert.That(r.Base).IsEqualTo("main");
        await Assert.That(r.Agent).IsEqualTo("claude");
        await Assert.That(r.Prompt).IsEqualTo("fix it");
        await Assert.That(r.AgentArguments).IsEqualTo("--model x");
        await Assert.That(r.Placement).IsEqualTo("split");
        await Assert.That(r.NoFocus).IsTrue();
        await Assert.That(r.WindowId).IsEqualTo(3);
    }

    [Test]
    public async Task NewWorktreeLooksWhereItWasRunAndLeavesThePlaceToTheConfig()
    {
        // Act
        var r = Req("new-worktree");

        // Assert
        await Assert.That(r.Cwd).IsEqualTo(Directory.GetCurrentDirectory());
        await Assert.That(r.Placement).IsNull();
        await Assert.That(r.Agent).IsNull();
    }

    [Test]
    public async Task NewWorktreeWithAPaneLooksInThatPane()
    {
        // Act
        var r = Req("new-worktree", "--pane-id", "4");

        // Assert
        await Assert.That(r.Cwd).IsNull();
        await Assert.That(r.PaneId).IsEqualTo(4);
    }

    [Test]
    [Arguments("new-worktree", "--prompt", "x")]
    [Arguments("new-worktree", "--agent-args", "x")]
    [Arguments("new-worktree", "--tab", "--split")]
    [Arguments("new-worktree", "extra", "")]
    [Arguments("wait-for-state", "--timeout", "5")]
    [Arguments("wait-for-state", "--state", "asleep")]
    [Arguments("wait-for-state", "--state", "idle,")]
    public async Task BadArgumentsAreUsageErrors(string sub, string a, string b)
    {
        // Arrange
        string[] args = b.Length > 0 ? [sub, a, b] : [sub, a];

        // Act
        var thrown = Assert.Throws<CliUsageException>(() => CliParser.Parse(args));

        // Assert
        await Assert.That(thrown.Message.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task WaitTakesSeveralStatesAndTidiesThem()
    {
        // Act
        var r = Req("wait-for-state", "--state", " Idle, waiting,idle");

        // Assert
        await Assert.That(r.State).IsEqualTo("idle,waiting");
        await Assert.That(r.TimeoutMs).IsNull();
    }

    [Test]
    public async Task WaitTakesTheStateAsAnArgument()
    {
        // Act
        var r = Req("wait-for-state", "exit", "--pane-id", "2");

        // Assert
        await Assert.That(r.State).IsEqualTo("exit");
        await Assert.That(r.PaneId).IsEqualTo(2);
    }

    [Test]
    [Arguments("30", 30_000L)]
    [Arguments("30s", 30_000L)]
    [Arguments("1.5", 1_500L)]
    [Arguments("500ms", 500L)]
    [Arguments("5m", 300_000L)]
    [Arguments("1h", 3_600_000L)]
    [Arguments("0", 0L)]
    public async Task TimeoutDurations(string text, long ms)
    {
        // Act
        var r = Req("wait-for-state", "--state", "idle", "--timeout", text);

        // Assert
        await Assert.That(r.TimeoutMs).IsEqualTo(ms);
    }

    [Test]
    [Arguments("soon")]
    [Arguments("-1")]
    [Arguments("5d")]
    [Arguments("")]
    [Arguments("9999999h")]
    public async Task BadTimeoutsAreUsageErrors(string text)
    {
        // Act
        var thrown = Assert.Throws<CliUsageException>(() => CliParser.Parse(["wait-for-state", "--state", "idle", "--timeout=" + text]));

        // Assert
        await Assert.That(thrown.Message).Contains("--timeout");
    }

    [Test]
    public async Task UsageNamesBothCommandsAndTheExitCodes()
    {
        // Act
        string usage = CliParser.Usage;

        // Assert
        await Assert.That(usage).Contains("new-worktree");
        await Assert.That(usage).Contains("wait-for-state");
        await Assert.That(usage).Contains("exits 2 on");
    }
}

/// <summary>When a pane counts as having reached a state, and the list of waits in progress.</summary>
public class PaneWaitTests
{
    private static PaneWaitView View(AgentState state, bool ended = false) => new(state, ended);

    [Test]
    [Arguments("idle", AgentState.Idle, true)]
    [Arguments("idle", AgentState.Done, true)] // finished, nobody looked
    [Arguments("idle", AgentState.Working, false)]
    [Arguments("idle", AgentState.None, false)]
    [Arguments("done", AgentState.Done, true)]
    [Arguments("done", AgentState.Idle, false)]
    [Arguments("working", AgentState.Working, true)]
    [Arguments("working", AgentState.Waiting, false)]
    [Arguments("waiting", AgentState.Waiting, true)]
    [Arguments("waiting", AgentState.Idle, false)]
    [Arguments("none", AgentState.None, true)]
    [Arguments("none", AgentState.Idle, false)]
    [Arguments("exit", AgentState.Idle, false)]
    public async Task EachStateMatches(string target, AgentState state, bool matches)
    {
        // Act
        string? reached = WaitStates.Matching([target], View(state), turnEnded: false);

        // Assert
        await Assert.That(reached).IsEqualTo(matches ? target : null);
    }

    [Test]
    public async Task ATurnThatEndedCountsAsDone()
    {
        // Act
        string? reached = WaitStates.Matching(["done"], View(AgentState.Idle), turnEnded: true);

        // Assert
        await Assert.That(reached).IsEqualTo("done");
    }

    [Test]
    public async Task AHeldPaneHasExited()
    {
        // Act
        string? reached = WaitStates.Matching(["exit"], View(AgentState.None, ended: true), turnEnded: false);

        // Assert
        await Assert.That(reached).IsEqualTo("exit");
    }

    [Test]
    public async Task AClosedPaneHasExitedAndIsInNoOtherState()
    {
        // Act
        string? exit = WaitStates.Matching(["none", "exit"], null, turnEnded: false);
        string? idle = WaitStates.Matching(["idle", "none"], null, turnEnded: false);

        // Assert
        await Assert.That(exit).IsEqualTo("exit");
        await Assert.That(idle).IsNull();
    }

    [Test]
    public async Task TheFirstStateAskedForIsTheOneReported()
    {
        // Act
        string? reached = WaitStates.Matching(["waiting", "done", "idle"], View(AgentState.Done), turnEnded: false);

        // Assert
        await Assert.That(reached).IsEqualTo("done");
    }

    [Test]
    public async Task AWaitStaysOpenUntilTheStateIsReached()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["waiting"], null);

        // Act
        list.Check(0, _ => View(AgentState.Working));
        bool openBefore = !reply.Task.IsCompleted;
        list.Check(1, _ => View(AgentState.Waiting));

        // Assert
        await Assert.That(openBefore).IsTrue();
        await Assert.That(reply.Task.Result.Ok).IsTrue();
        await Assert.That(reply.Task.Result.Output).IsEqualTo("waiting\n");
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DoneCountsATurnThatEndedInFrontOfTheUser()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["done"], null);

        // Act
        list.Check(0, _ => View(AgentState.Idle)); // a fresh agent at its prompt is not done
        bool openWhileIdle = !reply.Task.IsCompleted;
        list.Check(1, _ => View(AgentState.Working));
        list.Check(2, _ => View(AgentState.Idle)); // watched: never shows as Done

        // Assert
        await Assert.That(openWhileIdle).IsTrue();
        await Assert.That(reply.Task.Result.Output).IsEqualTo("done\n");
    }

    [Test]
    public async Task ADeadlineEndsTheWaitWithTheTimeoutCode()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["idle"], deadline: 100);

        // Act
        list.Check(99, _ => View(AgentState.Working));
        bool openBefore = !reply.Task.IsCompleted;
        list.Check(100, _ => View(AgentState.Working));

        // Assert
        await Assert.That(openBefore).IsTrue();
        await Assert.That(reply.Task.Result.Ok).IsFalse();
        await Assert.That(reply.Task.Result.ExitCode).IsEqualTo(CliExit.TimedOut);
        await Assert.That(reply.Task.Result.Error!).Contains("it is working");
    }

    [Test]
    public async Task AStateReachedAtTheDeadlineWins()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["idle"], deadline: 0);

        // Act
        list.Check(0, _ => View(AgentState.Idle)); // --timeout 0: one look

        // Assert
        await Assert.That(reply.Task.Result.Ok).IsTrue();
    }

    [Test]
    public async Task AClosedPaneEndsTheWaitWithThePaneGoneCode()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["idle"], null);

        // Act
        list.Check(0, _ => null);

        // Assert
        await Assert.That(reply.Task.Result.ExitCode).IsEqualTo(CliExit.PaneGone);
    }

    [Test]
    public async Task AHeldPaneEndsAWaitForAnAgentState()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["idle"], null);

        // Act
        list.Check(0, _ => View(AgentState.None, ended: true));

        // Assert
        await Assert.That(reply.Task.Result.ExitCode).IsEqualTo(CliExit.PaneGone);
    }

    [Test]
    public async Task AClosedPaneSatisfiesAWaitForExit()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["exit"], null);

        // Act
        list.Check(0, _ => null);

        // Assert
        await Assert.That(reply.Task.Result.Output).IsEqualTo("exit\n");
    }

    [Test]
    public async Task AWaitWhoseClientLeftIsDropped()
    {
        // Arrange
        var list = new PaneWaitList();
        var reply = list.Add(1, ["idle"], null);
        reply.TrySetCanceled();
        bool looked = false;

        // Act
        list.Check(0, _ => { looked = true; return View(AgentState.Working); });

        // Assert
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(looked).IsFalse();
    }

    [Test]
    public async Task WakesForTheNearestDeadlineOnly()
    {
        // Arrange
        var list = new PaneWaitList();
        list.Add(1, ["idle"], null);
        int without = list.WakeMs(0);
        list.Add(2, ["idle"], 500);
        list.Add(3, ["idle"], 200);

        // Act
        int with = list.WakeMs(50);

        // Assert
        await Assert.That(without).IsEqualTo(int.MaxValue);
        await Assert.That(with).IsEqualTo(150);
    }
}

/// <summary>The exit codes of the two commands, over a real socket.</summary>
[NotInParallel(Isolation.Processes)]
[ExcludeOn(OS.Windows)]
public class LateReplyCliTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-late-" + Guid.NewGuid().ToString("N")[..8]);
    private string Socket => Path.Combine(_dir, "erikterm-1.sock");

    [After(Test)]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IpcServer Serve(Func<IpcRequest, IpcResponse> handler)
    {
        var server = new IpcServer(Socket, r => Task.FromResult(handler(r)));
        server.Start();
        return server;
    }

    private (int Code, string Out, string Err) Cli(params string[] args)
    {
        var env = new Dictionary<string, string> { ["ERIKTERM_SOCKET"] = Socket };
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int code = CliMain.Run(args, new StringReader(""), o, e, k => env.GetValueOrDefault(k), TimeSpan.FromSeconds(30));
        return (code, o.ToString(), e.ToString());
    }

    private static TaskCompletionSource<IpcResponse> NewReply() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task ALateReplyReachesTheCliAndItPrintsTheState()
    {
        // Arrange
        var later = NewReply();
        using var server = Serve(_ => new IpcResponse { Ok = true, Later = later });

        // Act
        var run = Task.Run(() => Cli("wait-for-state", "--pane-id", "1", "--state", "idle"));
        await Task.Delay(50);
        bool stillWaiting = !run.IsCompleted;
        later.SetResult(new IpcResponse { Ok = true, Output = "idle\n" });
        var result = await run;

        // Assert
        await Assert.That(stillWaiting).IsTrue();
        await Assert.That(result).IsEqualTo((0, "idle\n", ""));
    }

    [Test]
    public async Task TimeoutExitsWithTwo()
    {
        // Arrange
        using var server = Serve(_ => IpcResponse.Failure("timed out", CliExit.TimedOut));

        // Act
        var result = Cli("wait-for-state", "--pane-id", "1", "--state", "idle", "--timeout", "1");

        // Assert
        await Assert.That(result.Code).IsEqualTo(2);
        await Assert.That(result.Err).Contains("timed out");
    }

    [Test]
    public async Task AnInstanceThatNeverAnswersIsATimeoutToo()
    {
        // Arrange
        using var server = Serve(_ => new IpcResponse { Ok = true, Later = NewReply() });

        // Act
        var result = Cli("wait-for-state", "--pane-id", "1", "--state", "idle", "--timeout", "0");

        // Assert
        await Assert.That(result.Code).IsEqualTo(2);
    }

    [Test]
    public async Task AnUnknownOrClosedPaneExitsWithThree()
    {
        // Arrange
        using var server = Serve(_ => IpcResponse.Failure("no pane with id 9", CliExit.PaneGone));

        // Act
        var result = Cli("wait-for-state", "--pane-id", "9", "--state", "idle");

        // Assert
        await Assert.That(result).IsEqualTo((3, "", "erikterm cli: no pane with id 9\n"));
    }

    [Test]
    public async Task AnyOtherFailureExitsWithOne()
    {
        // Arrange
        using var server = Serve(_ => IpcResponse.Failure("agent_status is off"));

        // Act
        var result = Cli("wait-for-state", "--pane-id", "1", "--state", "idle");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
    }

    [Test]
    public async Task NoInstanceExitsWithOne()
    {
        // Arrange
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Socket, ""); // a leftover file, nothing listening

        // Act
        var result = Cli("wait-for-state", "--pane-id", "1", "--state", "idle");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
    }

    [Test]
    public async Task UsageErrorsExitWithOne()
    {
        // Act
        var result = Cli("wait-for-state");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
    }

    [Test]
    [Arguments("idle", 3, "")]
    [Arguments("idle,exit", 0, "exit\n")]
    public async Task AnInstanceThatQuitsMidWaitTookThePaneWithIt(string state, int code, string printed)
    {
        // Arrange
        using var seen = new SemaphoreSlim(0);
        var server = Serve(_ => { seen.Release(); return new IpcResponse { Ok = true, Later = NewReply() }; });
        var run = Task.Run(() => Cli("wait-for-state", "--pane-id", "1", "--state", state));
        await seen.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        server.Dispose();
        var result = await run;

        // Assert
        await Assert.That(result.Code).IsEqualTo(code);
        await Assert.That(result.Out).IsEqualTo(printed);
    }

    [Test]
    public async Task AClientThatHangsUpCancelsTheWait()
    {
        // Arrange
        var later = NewReply();
        using var seen = new SemaphoreSlim(0);
        using var server = Serve(_ => { seen.Release(); return new IpcResponse { Ok = true, Later = later }; });
        using var cancel = new CancellationTokenSource();
        using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        await client.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(Socket));
        await client.SendAsync(System.Text.Encoding.UTF8.GetBytes("{\"command\":\"wait-for-state\"}\n"));
        await seen.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        client.Close();
        await Task.WhenAny(later.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert
        await Assert.That(later.Task.IsCanceled).IsTrue();
    }

    [Test]
    public async Task NewWorktreePrintsThePaneIdOnceItIsOpen()
    {
        // Arrange
        var later = NewReply();
        using var server = Serve(_ => new IpcResponse { Ok = true, Later = later });

        // Act
        var run = Task.Run(() => Cli("new-worktree", "--branch", "x"));
        await Task.Delay(50);
        bool stillWaiting = !run.IsCompleted;
        later.SetResult(new IpcResponse { Ok = true, PaneId = 7 });
        var result = await run;

        // Assert
        await Assert.That(stillWaiting).IsTrue();
        await Assert.That(result).IsEqualTo((0, "7\n", ""));
    }

    [Test]
    public void LateReplyIsNotSentOverTheWire()
    {
        // Act
        string json = IpcJson.Encode(new IpcResponse { Ok = true, Later = NewReply() });

        // Assert
        if (json.Contains("later", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(json);
    }
}

/// <summary>A launch without an agent.</summary>
public class ShellOnlyLaunchPlanTests
{
    private static readonly string[] Setup = ["setup"], Shell = ["sh"];

    [Test]
    public async Task WithoutSetupTheShellIsAllThereIs()
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(null, null, Shell);

        // Act
        var first = plan.First;
        var next = plan.Next(0);

        // Assert
        await Assert.That(first).IsEquivalentTo(Shell);
        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task SetupThenTheShellAsTheLastStep()
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(Setup, null, Shell);

        // Act
        var first = plan.First;
        var step = plan.Next(0)!;
        var after = plan.Next(0);

        // Assert
        await Assert.That(first).IsEquivalentTo(Setup);
        await Assert.That(step.Argv).IsEquivalentTo(Shell);
        await Assert.That(step.IsLast).IsTrue();
        await Assert.That(step.Message).IsNull();
        await Assert.That(after).IsNull();
    }

    [Test]
    public async Task AFailedSetupDoesNotMentionAnAgent()
    {
        // Arrange
        var plan = new WorktreeLaunchPlan(Setup, null, Shell);

        // Act
        var step = plan.Next(1)!;

        // Assert
        await Assert.That(step.Message!).Contains("setup failed");
        await Assert.That(step.Message!).DoesNotContain("agent");
    }
}
