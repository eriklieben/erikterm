namespace ErikTerm.Core;

/// <summary>
/// Explicit agent signals (AgentSignals.cs): OSC 7501 program status records (with the probe answered),
/// desktop notifications (OSC 9 / 777 / 99) and OSC 9 ; 4 progress. They run on the feeding thread under the
/// terminal's lock; the UI polls <see cref="Version"/> and drains notifications from the main thread.
/// </summary>
internal sealed class ProgramSignals(TerminalReplies replies)
{
    private readonly Dictionary<(string App, string Id), ProgramStatusRecord> _programStatus = [];
    private readonly List<TerminalNotification> _notifications = [];
    private AgentSignals.KittyNotifications? _kitty99;

    /// <summary>Answer OSC 7501 probes, so programs (Claude Code, Codex) report their state. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Bumped whenever a program status record, a notification or the progress changes.</summary>
    public int Version { get; private set; }

    /// <summary>Current OSC 7501 records (main record has Id ""). Read under the terminal's lock.</summary>
    public IReadOnlyCollection<ProgramStatusRecord> Records => _programStatus.Values;

    /// <summary>
    /// OSC 9 ; 4 progress: state 0 none, 1 normal, 2 error, 3 indeterminate, 4 paused; value 0–100. Cleared by
    /// <c>OSC 9;4;0</c>, a new shell prompt (OSC 133;A) and RIS.
    /// </summary>
    public (int State, int Value) Progress { get; private set; }

    /// <summary>Notifications requested since the last drain (at most 8 are kept).</summary>
    public int PendingNotificationCount => _notifications.Count;

    /// <summary>Moves pending notifications into <paramref name="into"/>.</summary>
    public void DrainNotifications(List<TerminalNotification> into)
    {
        into.AddRange(_notifications);
        _notifications.Clear();
    }

    /// <summary>
    /// Drops program status records — all of them, or (<paramref name="transientOnly"/>) only working/blocked ones,
    /// which can't outlive the program or its turn (it exited, or a shell prompt came back).
    /// </summary>
    public void ClearStatus(bool transientOnly)
    {
        if (_programStatus.Count == 0) return;
        if (!transientOnly) _programStatus.Clear();
        else
            foreach (var key in _programStatus.Keys.Where(k => _programStatus[k].State is ProgramState.Working or ProgramState.Blocked).ToList())
                _programStatus.Remove(key);
        Version++;
    }

    public void Osc7501(ReadOnlySpan<byte> arg)
    {
        if (!Enabled) return;
        if (AgentSignals.IsProgramStatusQuery(arg))
        {
            replies.Send(AgentSignals.ProgramStatusReply);
            return;
        }
        if (!AgentSignals.TryParseProgramStatus(arg, out var record, out bool clear, out string? clearId)) return;
        if (clear) RemoveProgramStatus(clearId);
        else if (!StoreProgramStatus(record!)) return;
        Version++;
    }

    /// <summary>Drops the records with an id, or all of them when it is null.</summary>
    private void RemoveProgramStatus(string? id)
    {
        if (id is null) { _programStatus.Clear(); return; }
        foreach (var key in _programStatus.Keys.ToList())
            if (key.Id == id) _programStatus.Remove(key);
    }

    /// <summary>Stores a record; false when the table is full and it isn't an update of one already there.</summary>
    private bool StoreProgramStatus(ProgramStatusRecord record)
    {
        if (_programStatus.Count >= 64 && !_programStatus.ContainsKey((record.App, record.Id))) return false;
        _programStatus[(record.App, record.Id)] = record;
        return true;
    }

    public void Osc9(ReadOnlySpan<byte> arg)
    {
        if (AgentSignals.ParseOsc9Progress(arg) is { } progress)
        {
            Progress = progress;
            Version++;
            return;
        }
        Notify(AgentSignals.ParseOsc9(arg));
    }

    public void Osc777(ReadOnlySpan<byte> arg) => Notify(AgentSignals.ParseOsc777(arg));

    public void Osc99(ReadOnlySpan<byte> arg) => Notify((_kitty99 ??= new()).Feed(arg));

    private void Notify(TerminalNotification? n)
    {
        if (n is null) return;
        if (_notifications.Count >= 8) _notifications.RemoveAt(0);
        _notifications.Add(n);
        Version++;
    }

    /// <summary>A shell prompt (OSC 133;A): whatever reported progress has finished, even if it didn't say so.</summary>
    public void ClearProgress()
    {
        if (Progress.State == 0) return;
        Progress = default;
        Version++;
    }

    /// <summary>RIS: forget status and progress (the program is starting over).</summary>
    public void Reset()
    {
        _programStatus.Clear();
        Progress = default;
        Version++;
    }
}
