using ErikTerm.App.Integrations;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.WindowFeatures;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Visible git plans: the split each one runs in, its guards, the next step, and the bar when it stops or finishes.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowRepoPlansTests
{
    private sealed class Harness : IDisposable
    {
        public readonly FakeFeatureWindow Window = new();
        public readonly BackgroundJobs Jobs = new(() => { });
        public readonly WindowRepoPlans Plans;

        public Harness() => Plans = new WindowRepoPlans(Window, Window, Jobs, new Lock());

        /// <summary>Runs the background jobs' continuations until <paramref name="done"/> holds.</summary>
        public Task<bool> Drain(Func<bool> done) => Eventually.True(() =>
        {
            Jobs.Drain();
            return done();
        });

        /// <summary>The split the plan's first step runs in.</summary>
        public Pane PlanPane => Window.Focused;

        public void Dispose() => Window.Dispose();
    }

    private static RepoStep Step(string name, Func<string?>? guard = null) => new(["git", name], "/repo", "git " + name, guard);

    [Test]
    public async Task APlanWithoutAGuardOpensItsSplitAtOnce()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge")]);
        bool started = false;

        // Act
        h.Plans.Start(plan, null, () => started = true);

        // Assert: the split runs the step; Restart Pane gives a shell there, never the command again.
        var split = h.Window.Splits.Single();
        await Assert.That(split.Command!).IsEquivalentTo(["git", "merge"], CollectionOrdering.Matching);
        await Assert.That(started).IsTrue();
        await Assert.That(h.PlanPane.RepoPlan).IsSameReferenceAs(plan);
        await Assert.That(h.PlanPane.LaunchArgv).IsEquivalentTo(h.Window.DefaultShell());
        await Assert.That(h.PlanPane.LaunchCwd).IsEqualTo("/repo");
    }

    [Test]
    public async Task AGuardThatSaysNoStopsItBeforeAnythingRuns()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge", () => "main moved")]);
        string? failure = null;

        // Act
        h.Plans.Start(plan, reason => failure = reason);
        await h.Drain(() => failure is not null);

        // Assert
        await Assert.That(failure).IsEqualTo("main moved");
        await Assert.That(h.Window.Splits).IsEmpty();
    }

    [Test]
    public async Task WithoutAFailureHandlerTheRefusalIsAToast()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge", () => "main moved")]);

        // Act
        h.Plans.Start(plan, null);
        await h.Drain(() => h.Window.Toasts.Count > 0);

        // Assert
        await Assert.That(h.Window.Toasts.Single()).IsEqualTo((App.Ui.ToastKind.Error, "Stopped: nothing ran", (string?)"main moved"));
    }

    [Test]
    public async Task AGuardThatPassesOpensTheSplit()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge", () => null)]);
        bool started = false;

        // Act
        h.Plans.Start(plan, null, () => started = true);
        await h.Drain(() => started);

        // Assert
        await Assert.That(h.Window.Splits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ASplitThatCannotStartFailsThePlan()
    {
        // Arrange
        using var h = new Harness();
        h.Window.SplitFails = true;
        var plan = new RepoPlan("Merge", [Step("merge")]);

        // Act
        h.Plans.Start(plan, null);

        // Assert
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Failed);
        await Assert.That(plan.Failure).IsEqualTo("The pane couldn't start");
    }

    [Test]
    public async Task APaneWithoutAPlanIsNotOneToContinue()
    {
        // Arrange
        using var h = new Harness();

        // Act
        bool kept = h.Plans.Continue(h.Window.Focused);
        bool key = h.Plans.KeyDown(h.Window.Focused, Sdl.K_RETURN);

        // Assert
        await Assert.That(kept).IsFalse();
        await Assert.That(key).IsFalse();
    }

    [Test]
    public async Task AStepThatSucceedsStartsTheNextInTheSamePane()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Rebase", [Step("rebase"), Step("merge")]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(0);

        // Act
        bool kept = h.Plans.Continue(pane);

        // Assert
        await Assert.That(kept).IsTrue();
        await Assert.That(h.Window.Calls).Contains("Respawn git merge");
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Running);
        await Assert.That(pane.Term.GetText(pane.Term.FirstLine, 0, pane.Term.ScreenTopLine + pane.Term.Rows, 200)).Contains("$ git merge");
    }

    [Test]
    public async Task TheNextStepsGuardRunsFirst()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Rebase", [Step("rebase"), Step("merge", () => "main moved")]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(0);

        // Act
        h.Plans.Continue(pane);
        await h.Drain(() => plan.State == RepoPlanState.Failed);

        // Assert: it stopped with the guard's reason, and the bar says so.
        await Assert.That(plan.Failure).IsEqualTo("main moved");
        await Assert.That(pane.Held).IsTrue();
        await Assert.That(pane.ExitBar!.Current!.Text).IsEqualTo("Rebase stopped");
        await Assert.That(h.Window.Calls).DoesNotContain("Respawn git merge");
    }

    [Test]
    public async Task AFailedStepStopsThePlanWithABar()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge")]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(1);

        // Act
        h.Plans.Continue(pane);

        // Assert
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Failed);
        await Assert.That(pane.ExitBar!.Current!.Buttons.Select(b => b.Label)).IsEquivalentTo(["Retry", "Close"], CollectionOrdering.Matching);
        await Assert.That(h.Window.Calls).Contains("ApplyLayout(True)");
    }

    [Test]
    public async Task WhileTheBarIsUpContinueKeepsThePane()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge")]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(1);
        h.Plans.Continue(pane);

        // Act
        bool kept = h.Plans.Continue(pane);

        // Assert
        await Assert.That(kept).IsTrue();
        await Assert.That(h.Window.Calls.Count(c => c == "ApplyLayout(True)")).IsEqualTo(1);
    }

    [Test]
    public async Task EnterOnAFailedPlanRetriesTheStep()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge")]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(1);
        h.Plans.Continue(pane);

        // Act
        bool handled = h.Plans.KeyDown(pane, Sdl.K_RETURN);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Running);
        await Assert.That(pane.Held).IsFalse();
        await Assert.That(h.Window.Calls).Contains("Respawn git merge");
    }

    [Test]
    public async Task AFinishedPlanRunsItsSuccessAndEnterClosesThePane()
    {
        // Arrange
        using var h = new Harness();
        bool succeeded = false;
        var plan = new RepoPlan("Merge", [Step("merge")]) { OnSuccess = () => succeeded = true };
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(0);
        h.Plans.Continue(pane);

        // Act
        bool handled = h.Plans.KeyDown(pane, Sdl.K_KP_ENTER);

        // Assert
        await Assert.That(succeeded).IsTrue();
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Done);
        await Assert.That(pane.ExitBar!.Current!.Text).IsEqualTo("Merge finished");
        await Assert.That(handled).IsTrue();
        await Assert.That(h.Window.Calls).Contains("ClosePane " + pane.Id);
    }

    [Test]
    public async Task EscapeClosesAndOtherKeysAreNotThePlans()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge")]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(1);
        h.Plans.Continue(pane);

        // Act
        bool other = h.Plans.KeyDown(pane, 'x');
        bool escape = h.Plans.KeyDown(pane, Sdl.K_ESCAPE);

        // Assert
        await Assert.That(other).IsFalse();
        await Assert.That(escape).IsTrue();
        await Assert.That(h.Window.Calls).Contains("ClosePane " + pane.Id);
    }

    [Test]
    public async Task TheBarsButtonsRetryAndClose()
    {
        // Arrange
        using var h = new Harness();
        var plan = new RepoPlan("Merge", [Step("merge")]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(1);
        h.Plans.Continue(pane);
        var buttons = pane.ExitBar!.Current!.Buttons;

        // Act
        buttons[1].Run();
        buttons[0].Run();

        // Assert
        await Assert.That(h.Window.Calls).Contains("ClosePane " + pane.Id);
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Running);
    }

    [Test]
    public async Task AGuardAnswerForAPaneThatClosedStartsNothing()
    {
        // Arrange: the first step succeeded, then its pane was closed.
        using var h = new Harness();
        var plan = new RepoPlan("Rebase", [Step("rebase"), Step("merge", () => null)]);
        h.Plans.Start(plan, null);
        var pane = h.PlanPane;
        pane.SimulateExit(0);
        h.Window.Layout.Remove(pane);

        // Act: the guard's answer comes back for a pane that closed meanwhile.
        h.Plans.Continue(pane);
        await h.Drain(() => !h.Jobs.Busy);

        // Assert: nothing was started in it.
        await Assert.That(h.Window.Calls).DoesNotContain("Respawn git merge");
        await Assert.That(plan.State).IsEqualTo(RepoPlanState.Waiting);
    }
}
