using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Repository changes run here, one program at a time in a visible split (<see cref="RepoPlan"/>): each step is the
/// pane's own process, so a commit's FIDO touch, a hook's prompt and git's editor work as in any terminal. A step that
/// fails stops the plan with [Retry] / [Close]; nothing after it has run. Restart Pane and a restored session give a
/// shell in that directory, never the command again.
/// </summary>
internal sealed class WindowRepoPlans(IFeatureWindow window, IPaneLauncher launcher, BackgroundJobs jobs, Lock gate)
{
    private static long Now => Environment.TickCount64;

    /// <summary>Runs the plan's first guard, then opens the split; <paramref name="failed"/> gets the guard's reason (nothing ran).</summary>
    public void Start(RepoPlan plan, Action<string>? failed, Action? started = null)
    {
        var first = plan.Current;
        if (first.Guard is { } guard)
        {
            jobs.Run(() => guard() ?? "", reason =>
            {
                if (reason is not null && reason.Length == 0) { OpenRepoPlan(plan); started?.Invoke(); }
                else ReportRepoPlanFailure(reason ?? "The check failed unexpectedly: nothing ran", failed);
            }, wakeLoop: true);
            return;
        }
        OpenRepoPlan(plan);
        started?.Invoke();
    }

    private void ReportRepoPlanFailure(string reason, Action<string>? failed)
    {
        if (failed is not null) failed(reason);
        else window.Toast(ToastKind.Error, "Stopped: nothing ran", reason);
        window.Redraw();
    }

    private void OpenRepoPlan(RepoPlan plan)
    {
        var step = plan.Current;
        string[] shell = launcher.DefaultShell();
        var pane = launcher.Split(SplitAxis.Vertical, [.. step.Argv], step.Cwd,
            prepare: term => term.Feed(System.Text.Encoding.UTF8.GetBytes($"\e[2m$ {step.Display}\e[0m\r\n")));
        if (pane is null) { plan.Fail("The pane couldn't start"); return; }
        pane.RepoPlan = plan;
        pane.LaunchArgv = shell;           // Restart Pane: a shell here, never the command again
        pane.LaunchCwd = step.Cwd;
        pane.LaunchedAgent = step.Argv;    // and out of the saved session
    }

    /// <summary>
    /// Hook from HoldIfWanted: a plan's program exited. Starts the next step (behind its guard), or shows how it went.
    /// True when the pane stays. Idempotent while a guard is out (reaped every tick).
    /// </summary>
    public bool Continue(Pane pane)
    {
        if (pane.RepoPlan is not { } plan) return false;
        switch (plan.State)
        {
            case RepoPlanState.Waiting or RepoPlanState.Done or RepoPlanState.Failed:
                return true; // a guard is out, or the bar is up
        }
        if (plan.StepExited(pane.ExitCode)) { RunRepoStep(pane, plan); return true; }
        if (plan.State == RepoPlanState.Done) Finished(pane, plan);
        else ShowRepoPlanFailure(pane, plan);
        return true;
    }

    private void RunRepoStep(Pane pane, RepoPlan plan)
    {
        var step = plan.Current;
        if (step.Guard is { } guard)
        {
            jobs.Run(() => guard() ?? "", reason =>
            {
                if (window.FindPane(pane.Id) is null) return;
                if (reason is null or { Length: > 0 }) { plan.Fail(reason ?? "The check failed unexpectedly"); ShowRepoPlanFailure(pane, plan); }
                else StartRepoStep(pane, plan);
            }, wakeLoop: true);
            return;
        }
        StartRepoStep(pane, plan);
    }

    private void StartRepoStep(Pane pane, RepoPlan plan)
    {
        var step = plan.Current;
        lock (gate) pane.Term.Feed(System.Text.Encoding.UTF8.GetBytes($"\r\n\e[2m$ {step.Display}\e[0m\r\n"));
        if (!launcher.RespawnPane(pane, [.. step.Argv], step.Cwd))
        {
            plan.Fail($"{step.Display} couldn't start");
            ShowRepoPlanFailure(pane, plan);
            return;
        }
        pane.LaunchedAgent = step.Argv;
        plan.Started();
    }

    private void Finished(Pane pane, RepoPlan plan)
    {
        pane.Held = true;
        lock (gate) pane.Term.Feed("\e[?25l"u8);
        ShowRepoBar(pane, plan, ok: true);
        window.ApplyLayout(animate: true);
        plan.OnSuccess?.Invoke();
    }

    private void ShowRepoPlanFailure(Pane pane, RepoPlan plan)
    {
        pane.Held = true;
        lock (gate)
        {
            pane.Term.Feed(System.Text.Encoding.UTF8.GetBytes($"\r\n\e[31m[stopped: {plan.Failure}]\e[0m\r\n"));
            pane.Term.Feed("\e[?25l"u8);
        }
        ShowRepoBar(pane, plan, ok: false);
        window.ApplyLayout(animate: true);
    }

    private void ShowRepoBar(Pane pane, RepoPlan plan, bool ok)
    {
        pane.ExitBar ??= new InfoBar();
        pane.ExitBarOwner = launcher.ExitBarOwner;
        pane.ExitBar.Show(new InfoBarMessage
        {
            Key = launcher.ExitBarKey,
            Tone = ok ? InfoBarTone.Success : InfoBarTone.Danger,
            Icon = ok ? Icons.Check : Icons.Error,
            Text = ok ? plan.Title + " finished" : plan.Title + " stopped",
            Detail = ok ? "Enter or Esc closes" : (plan.Failure ?? "") + " · Enter retries, Esc closes",
            Buttons = ok
                ? [new InfoBarButton("Close", () => launcher.ClosePane(pane), primary: true)]
                : [new InfoBarButton("Retry", () => RetryRepoPlan(pane, plan), primary: true), new InfoBarButton("Close", () => launcher.ClosePane(pane))],
            Dismissible = false,
        }, window.Theme, Now);
    }

    private void RetryRepoPlan(Pane pane, RepoPlan plan)
    {
        if (plan.State != RepoPlanState.Failed) return;
        pane.Held = false;
        pane.ExitBar?.Remove(launcher.ExitBarKey, Now);
        plan.Retry();
        RunRepoStep(pane, plan);
    }

    /// <summary>Hook from ExitBarKeyDown: Enter retries a failed plan and closes a finished one; Esc closes it. False: not a plan's pane.</summary>
    public bool KeyDown(Pane pane, uint key)
    {
        if (pane.RepoPlan is not { } plan || !pane.Held) return false;
        if (key is Native.Sdl.K_RETURN or Native.Sdl.K_KP_ENTER)
        {
            if (plan.State == RepoPlanState.Failed) RetryRepoPlan(pane, plan);
            else launcher.ClosePane(pane);
            return true;
        }
        if (key == Native.Sdl.K_ESCAPE) { launcher.ClosePane(pane); return true; }
        return false;
    }
}
