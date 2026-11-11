using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ipc;

/// <summary>What a wait can see of a live pane. <paramref name="Ended"/>: its program is gone and it is only held open (exit_behavior).</summary>
internal readonly record struct PaneWaitView(AgentState State, bool Ended);

/// <summary>The states <c>wait-for-state</c> takes, and when a pane counts as having reached one. Pure.</summary>
internal static class WaitStates
{
    public const string Exit = "exit";
    public const string Expected = "idle, working, waiting, done, none or exit";

    private static readonly string[] Names = ["idle", "working", "waiting", "done", "none", Exit];

    /// <summary>"idle,done" as lower-case names without repeats; null when it is empty or a name isn't a state.</summary>
    public static string[]? Parse(string? text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).Distinct().ToArray();
        return parts.All(p => Names.Contains(p)) ? parts : null;
    }

    /// <summary>
    /// The first of <paramref name="targets"/> the pane is in, or null. <paramref name="pane"/> null: it closed.
    /// <paramref name="turnEnded"/>: the wait saw it work and it has stopped. A finished turn shows as Done only while
    /// nobody looked at it, so idle counts Done too, and done counts a turn that ended in front of the user.
    /// </summary>
    public static string? Matching(IReadOnlyList<string> targets, PaneWaitView? pane, bool turnEnded)
    {
        return targets.FirstOrDefault(target => Reached(target, pane, turnEnded));
    }

    private static bool Reached(string target, PaneWaitView? pane, bool turnEnded)
    {
        if (pane is not { } p) return target == Exit;
        return target switch
        {
            Exit => p.Ended,
            "idle" => p.State is AgentState.Idle or AgentState.Done,
            "done" => p.State == AgentState.Done || turnEnded,
            "working" => p.State == AgentState.Working,
            "waiting" => p.State == AgentState.Waiting,
            _ => p.State == AgentState.None,
        };
    }
}

/// <summary>
/// The <c>wait-for-state</c> requests in progress. The client sits on its socket; the event loop calls <see cref="Check"/>
/// after it has polled the agents, so a wait costs no extra wake-ups beyond its own deadline. Main thread only, except
/// that the server may cancel a wait's reply when the client hangs up: <see cref="Check"/> then drops it.
/// </summary>
internal sealed class PaneWaitList
{
    private sealed class PaneWait(int paneId, string[] targets, long? deadline)
    {
        public readonly int PaneId = paneId;
        public readonly string[] Targets = targets;
        public readonly long? Deadline = deadline;
        public readonly TaskCompletionSource<IpcResponse> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawWorking;
    }

    private readonly List<PaneWait> _waits = [];

    public int Count => _waits.Count;

    /// <param name="deadline">When to give up (in the clock <see cref="Check"/> is given), or null for never.</param>
    public TaskCompletionSource<IpcResponse> Add(int paneId, string[] targets, long? deadline)
    {
        var wait = new PaneWait(paneId, targets, deadline);
        _waits.Add(wait);
        return wait.Reply;
    }

    /// <summary>Answers every wait that is settled; <paramref name="lookup"/> gives a pane's state, or null when it is gone.</summary>
    public void Check(long now, Func<int, PaneWaitView?> lookup)
    {
        for (int i = _waits.Count - 1; i >= 0; i--)
        {
            var wait = _waits[i];
            if (!wait.Reply.Task.IsCompleted) // else cancelled: the client hung up
            {
                if (Outcome(wait, lookup(wait.PaneId), now) is not { } reply) continue;
                wait.Reply.TrySetResult(reply);
            }
            _waits.RemoveAt(i);
        }
    }

    private static IpcResponse? Outcome(PaneWait wait, PaneWaitView? pane, long now)
    {
        wait.SawWorking |= pane is { State: AgentState.Working };
        bool turnEnded = wait.SawWorking && pane is { State: AgentState.Idle or AgentState.Done };
        if (WaitStates.Matching(wait.Targets, pane, turnEnded) is { } reached) return new IpcResponse { Ok = true, Output = reached + "\n" };
        if (pane is not { } p) return IpcResponse.Failure($"pane {wait.PaneId} closed", CliExit.PaneGone);
        if (p.Ended) return IpcResponse.Failure($"pane {wait.PaneId} has exited", CliExit.PaneGone);
        if (wait.Deadline is not { } deadline || now < deadline) return null;
        return IpcResponse.Failure($"timed out waiting for pane {wait.PaneId} to be {string.Join(" or ", wait.Targets)}: it is {AgentStateText.Word(p.State)}", CliExit.TimedOut);
    }

    /// <summary>Until the nearest deadline, for the event loop's sleep; int.MaxValue when none is set.</summary>
    public int WakeMs(long now)
    {
        long ms = int.MaxValue;
        foreach (var wait in _waits)
            if (wait.Deadline is { } deadline) ms = Math.Min(ms, Math.Max(1, deadline - now));
        return (int)ms;
    }
}
