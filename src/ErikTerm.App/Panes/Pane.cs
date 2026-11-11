using ErikTerm.App.Agents;
using ErikTerm.App.Rendering;
using ErikTerm.App.Teammates;
using ErikTerm.App.Vim;
using ErikTerm.App.Worktrees;
using ErikTerm.Core;
using ErikTerm.Pty;
using ErikTerm.Ui;

namespace ErikTerm.App.Panes;

/// <summary>One terminal in the layout: emulator state, its PTY + reader thread, and per-pane UI animation state.</summary>
internal sealed class Pane : IDisposable, IVimPane, IWorktreePane, IAgentPane, ITeammatePane<Pane>
{
    private static int _nextId;

    public int Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>The id the next pane will get (panes are created on the main thread only).</summary>
    public static int PeekNextId() => Volatile.Read(ref _nextId) + 1;
    public Terminal Term { get; }
    /// <summary>Replaced by <see cref="Respawn"/> (tmux respawn-pane); main thread only.</summary>
    public Pty.Pty Pty { get; private set; }
    /// <summary>The program's output ended (it exited): the window closes or holds the pane (exit_behavior, Windowing/ExitHoldFlow.cs).</summary>
    public volatile bool Exited;
    private int _exitCode = int.MinValue;

    /// <summary>The exited program's status (Linux: 0–255, 128 + signal), or null when it isn't known. Set before <see cref="Exited"/>.</summary>
    public int? ExitCode => Volatile.Read(ref _exitCode) is var c && c != int.MinValue ? c : null;

    /// <summary>The program and directory the pane was started with: Restart Pane runs them again.</summary>
    public string[] LaunchArgv { get; set; } = [];
    public string? LaunchCwd { get; set; }
    /// <summary>A worktree tab's programs still to come (setup, agent, shell); null otherwise (Worktrees/WindowWorktrees.cs).</summary>
    public ErikTerm.Ui.WorktreeLaunchPlan? WorktreeLaunch { get; set; }
    /// <summary>The agent a worktree tab started: never saved with the session, its prompt and arguments were for that launch.</summary>
    public IReadOnlyList<string>? LaunchedAgent { get; set; }
    /// <summary>Visible git steps running in this pane (WindowFeatures/WindowRepoPlans.cs).</summary>
    public ErikTerm.Ui.RepoPlan? RepoPlan;
    /// <summary>
    /// Close when the program exits whatever exit_behavior says: tmux emulation panes, erikterm's own tool splits, and
    /// panes asked to go (kill-pane).
    /// </summary>
    public bool CloseOnExit { get; set; }
    /// <summary>The program exited and the pane stays (exit_behavior Hold / CloseOnCleanExit), with its exit bar.</summary>
    public bool Held;
    /// <summary>Pixels the exit bar takes above the teammate strip (the grid gets the rest; 0 when not held).</summary>
    public int HeldPx;
    /// <summary>Pixels at the bottom of the pane's rectangle that aren't grid: the teammate strip and the exit bar.</summary>
    public int BottomPx => StripPx + HeldPx;
    /// <summary>The exit bar, and the window its buttons were made for (a held pane can move to another window).</summary>
    public ErikTerm.App.Ui.InfoBar? ExitBar;
    public object? ExitBarOwner;
    /// <summary>OSC 9;4 progress as last drawn (Windowing/WindowProgress.cs), and the finished commands already looked at (WindowFeatures/WindowRecent.cs).</summary>
    public ProgressReport Progress;
    public long SeenCommandsFinished;
    /// <summary>The OSC 7 report last looked at (remembered directories: WindowFeatures/WindowRecent.cs).</summary>
    public string? SeenCwdUri;
    /// <summary>Made through the tmux emulation (split-window / new-window), so respawn-pane and kill-pane may target it.</summary>
    public bool CreatedByTmux { get; set; }
    /// <summary>A title set with tmux select-pane -T.</summary>
    public string? TmuxTitle;
    /// <summary>An agent-team teammate Claude Code opened through the tmux emulation (Teammate.cs); null otherwise.</summary>
    public Teammate? Teammate { get; set; }
    /// <summary>As a lead: its teammates in this tab, in layout order (rebuilt by Teammates/WindowTeammates.cs; main thread).</summary>
    public readonly List<Pane> Teammates = [];
    /// <summary>Pixels at the bottom of the pane's rectangle given to the teammate strip (the grid gets the rest).</summary>
    public int StripPx;

    /// <summary>A parked teammate takes no room in the layout (ILayoutLeaf).</summary>
    public bool LayoutHidden => Teammate is { Parked: true };

    /// <summary>Term.LinesScrolled when the last frame was built (Rendering/FramePacing.cs).</summary>
    public long LinesScrolledAtFrame;

    /// <summary>When the process started (Environment.TickCount64): early output is startup, not activity.</summary>
    public long SpawnedAt { get; } = Environment.TickCount64;

    // Layout + animation (main thread only).
    public RectI Target { get; set; }
    public RectTween Rect;
    public Tween Focus = new(1);     // 1 = focused; drives inactive-pane dimming
    public Tween Appear = new(1);    // new panes fade in
    public Tween Scrollbar = new(0); // overlay scrollbar visibility
    /// <summary>Prompts and failed commands for the scrollbar's ticks (ScrollbarRenderer.cs).</summary>
    public readonly ScrollMarks ScrollMarks = new();
    public long ScrollActivityAt;
    public int SeenBellCount;
    public Tween Bell = new(0);
    public Selection? Selection { get; set; }
    public readonly CursorState Cursor = new();
    public PaneLinks Links = new(); // hovered link, prompt flash, gutter hover (PaneLinks.cs)
    public readonly AgentPane Agent = new(); // coding-agent status (AgentPane.cs, Host/ProcessPanes.cs)
    /// <summary>Vim mode (copy mode) while it's on for this pane (Vim/WindowVim.cs); null otherwise.</summary>
    public ErikTerm.Ui.VimMode.VimEngine? Vim { get; set; }

    RectI IVimPane.Target => Target;
    void IVimPane.SnapScroll() => Scroll.Snap();
    string? IVimPane.AgentName => Agent.Definition?.Name;

    ErikTerm.Ui.AgentStatus.AgentState IPaneAgentStatus.AgentState => Agent.State;
    bool IPaneAgentStatus.AgentActive => Agent.Active;
    string IWorktreePane.AgentLabel => Agent.Name;
    string? IVimPane.TmuxTitle => TmuxTitle;
    bool IVimPane.RunsProgram => Pty.ForegroundProcessGroup() is > 0 and var pg && pg != Pty.ProcessId;

    AgentPane IAgentPane.Agent => Agent;
    bool IAgentPane.Exited => Exited;
    Selection? IAgentPane.Selection => Selection;
    Teammate? IAgentPane.Teammate => Teammate;
    List<Pane> ITeammatePane<Pane>.Teammates => Teammates;
    int ITeammatePane<Pane>.StripPx => StripPx;
    AgentPane ITeammatePane<Pane>.Agent => Agent;
    ref RectTween ITeammatePane<Pane>.Rect => ref Rect;
    ref Tween ITeammatePane<Pane>.Focus => ref Focus;
    ref Tween ITeammatePane<Pane>.Appear => ref Appear;
    string? ITeammatePane<Pane>.TmuxTitle => TmuxTitle;
    RectF IAgentPane.AgentBounds(long now) => Rect.Value(now);
    string? IAgentPane.ForegroundName => Pty.ForegroundProcessName;
    int IAgentPane.ForegroundProcessGroup() => Pty.ForegroundProcessGroup();
    ErikTerm.Ui.AgentStatus.AgentDefinition? IAgentPane.IdentifyForeground() => ErikTerm.Ui.AgentStatus.AgentCatalog.Identify(Pty.ForegroundProcessName, Pty.ForegroundArguments());
    string? IAgentPane.KnownDirectory => WorkingDirectory ?? ProcessInfo.WorkingDirectory(Pty.ProcessId);

    RectF IVimPane.OverlayArea(long now)
    {
        RectF r = Rect.Value(now);
        return BottomPx > 0 ? r with { H = MathF.Max(0, r.H - BottomPx) } : r;
    }

    /// <summary>Cached per-row quads (PaneRenderer).</summary>
    public readonly RowCache RowCache = new();

    /// <summary>Visual sub-line scroll offset (smooth scrolling, trackpads).</summary>
    public readonly SmoothScroll Scroll;

    /// <summary>Reflow generation the pane's UI state (selection, hover, ...) refers to (see ReflowInvalidation).</summary>
    public int SeenReflowCount;

    public Pane(Terminal term, Pty.Pty pty)
    {
        Term = term;
        Pty = pty;
        SeenBellCount = term.BellCount;
        Scroll = new SmoothScroll(term);
        SeenReflowCount = term.ReflowCount;
    }

    /// <summary>Scrollback rows compacted per idle step (a few hundred microseconds under the lock).</summary>
    private const int CompactionStep = 512;

    /// <summary>Cell storage released by compaction after which a full GC hands the memory back to the system.</summary>
    private const long CollectAfterBytes = 8L << 20;

    /// <summary>Windows: how long output has to stop before the scrollback is compacted.</summary>
    private const int CompactAfterQuietMs = 100;

    /// <summary>How long a cleared scrollback's rows wait for more output: `clear` between two runs of a noisy build.</summary>
    private const int SpareRowsKeptMs = 3000;

    /// <summary>Decides when the deferred full GC runs: it stops every thread, so never just as you start typing.</summary>
    internal static readonly QuietCollection QuietCollection = new();

    private static readonly Lock CollectTimerLock = new();
    private static Timer? _collectTimer;

    private static void CollectWhenQuiet()
    {
        QuietCollection.Request();
        lock (CollectTimerLock)
        {
            _collectTimer ??= new Timer(_ => CollectIfQuiet());
            _collectTimer.Change(1000, 1000);
        }
    }

    private static void CollectIfQuiet()
    {
        if (!QuietCollection.TryTake(Environment.TickCount64)) return;
        lock (CollectTimerLock) _collectTimer?.Change(Timeout.Infinite, Timeout.Infinite);
#pragma warning disable S1215 // Measured: only an aggressive compacting collection after a big flood gives the peak back to the OS (see CollectIfCompacted).
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
#pragma warning restore S1215
    }

    /// <summary>
    /// Reads PTY output on a background thread, feeding the terminal under <paramref name="gate"/>. While the program
    /// is quiet the thread compacts the scrollback (Terminal.CompactScrollback): lines enter it whole so a flood of
    /// output scrolls at full speed, and lose their blank tails afterwards.
    /// </summary>
    public void StartReader(Lock gate, Action wake)
    {
        var pty = Pty;
        var term = Term;
        long collected = term.CompactedBytes;
        void CollectIfCompacted()
        {
            if (term.CompactedBytes - collected < CollectAfterBytes) return;
            // A big flood was compacted: the full-width arrays it held are garbage among live rows. An aggressive
            // collection compacts and decommits right away; otherwise RSS stays near the flood's peak until later
            // full GCs give it back bit by bit.
            collected = term.CompactedBytes;
            CollectWhenQuiet();
        }
        bool Idle()
        {
            if (!term.NeedsCompaction) return false;
            bool more;
            lock (gate) more = term.CompactScrollback(CompactionStep);
            if (!more) CollectIfCompacted();
            return more;
        }
        void CollectAfterRelease()
        {
            // Few bytes, but the screen's and the spare rows' arrays lie scattered through the pinned heap and hold on to
            // the regions around them, past the collection that followed the compaction.
            collected = term.CompactedBytes;
            CollectWhenQuiet();
        }
        var spareRows = new SpareRowRelease(term, gate, SpareRowsKeptMs, CollectAfterRelease);
        // A read on a Windows pipe blocks without a timeout, so there the reader's idle step doesn't run once output stops.
        var compaction = OperatingSystem.IsWindows() ? new AfterOutputStops(CompactAfterQuietMs, () => term.NeedsCompaction, () => { while (Idle()) { /* each pass compacts one step */ } }) : null;
        var thread = new Thread(() =>
        {
            ReadUntilClosed(pty, gate, wake, () => { spareRows.NoteOutput(); compaction?.NoteOutput(); }, Idle);
            spareRows.Dispose();
            compaction?.Dispose();
            RecordExit(pty);
            wake();
        })
        {
            IsBackground = true,
            Name = $"pty-reader-{Id}",
        };
        thread.Start();
    }

    private void ReadUntilClosed(Pty.Pty pty, Lock gate, Action wake, Action noteOutput, Func<bool> idle)
    {
        var buffer = new byte[64 * 1024];
        bool reported = false;
        try
        {
            int n;
            while ((n = pty.Read(buffer, idle)) > 0)
            {
                try
                {
                    lock (gate) Term.Feed(buffer.AsSpan(0, n));
                    noteOutput();
                }
                catch (Exception ex) when (ex is not (IOException or ObjectDisposedException or ThreadInterruptedException))
                {
                    // A bug the program's output reached must not take the whole terminal (every tab and window
                    // of this process) down: drop the chunk, say so once, keep reading.
                    if (!reported) Console.Error.WriteLine($"erikterm: pane {Id}: output dropped after an internal error: {ex.GetType().Name}: {ex.Message}");
                    reported = true;
                }
                wake();
            }
        }
        catch (IOException) { /* the pty closed: handled as an exit by the caller */ }
        catch (ObjectDisposedException) { /* the pty was disposed while reading: handled as an exit by the caller */ }
    }

    private void RecordExit(Pty.Pty pty)
    {
        if (!ReferenceEquals(pty, Pty)) return; // Respawn replaced it
        int code = ReapExitCode(pty);
        if (!ReferenceEquals(pty, Pty)) return;
        if (code != int.MinValue) Interlocked.CompareExchange(ref _exitCode, code, int.MinValue); // a simulated exit (SimulateExit) wins
        Exited = true;
    }

    /// <summary>
    /// Waits briefly for the exited child's status: the pty reports end of output as the process exits, a moment
    /// before it can be reaped. A child that closed its terminal but lives on gives up after ~300 ms (unknown status).
    /// Reaping also keeps exited panes' programs from lingering as zombies.
    /// </summary>
    private static int ReapExitCode(Pty.Pty pty)
    {
        int waited = 0, step = 1;
        while (true)
        {
            if (pty.TryGetExitCode(out int code)) return code;
            if (waited >= 300) return int.MinValue;
            Thread.Sleep(step);
            waited += step;
            step = Math.Min(step * 2, 50);
        }
    }

    /// <summary>
    /// Runs a new program in this pane (tmux respawn-pane -k, Restart Pane): the old one is hung up and output now comes
    /// from <paramref name="pty"/>. tmux's respawn resets the screen (RIS); a restart (<paramref name="keepOutput"/>)
    /// keeps the old output and only resets what the old program set (Terminal.ResetForNewProgram). The pane keeps its
    /// id, place and scrollback settings.
    /// </summary>
    public void Respawn(Pty.Pty pty, Lock gate, Action wake, bool keepOutput = false)
    {
        var old = Pty;
        lock (gate)
        {
            Pty = pty;
            Term.Response = data => pty.WriteReply(data);
            if (keepOutput) Term.ResetForNewProgram();
            else Term.Feed("\ec"u8);
        }
        old.Hangup();
        old.Dispose();
        Volatile.Write(ref _exitCode, int.MinValue);
        Exited = false;
        Held = false;
        StartReader(gate, wake);
    }

    /// <summary>Debug script exit:N — the program "exits" with status N (it is hung up; N is what the window sees).</summary>
    public void SimulateExit(int code)
    {
        Interlocked.CompareExchange(ref _exitCode, code, int.MinValue);
        Pty.Hangup();
    }

    /// <summary>The container the pane's shell runs in, while it does (WindowFeatures/WindowContainers.cs); null on this machine. Main thread.</summary>
    public Containers.PaneContainer? Container { get; set; }

    /// <summary>
    /// Current directory reported by the shell (OSC 7), for spawning splits in the same place. Unknown (null) when
    /// the report names another host: a shell over ssh reports the remote directory. In a container, the host
    /// directory its report maps to (Containers/PaneContainer.cs).
    /// </summary>
    public string? WorkingDirectory
    {
        get
        {
            if (Container is { } c) return c.HostDirectory(Term.WorkingDirectoryUri, Directory.Exists);
            return PaneCwd.FromOsc7(Term.WorkingDirectoryUri) is { } path && Directory.Exists(path) ? path : null;
        }
    }

    /// <summary>Where a split / tab / window opened from this pane starts (OSC 7, foreground program, shell, default; see PaneCwd).</summary>
    public string? StartDirectory(string? defaultCwd) => PaneCwd.Resolve(
        WorkingDirectory,
        () => Pty.ForegroundProcessGroup() is > 0 and var pgid && pgid != Pty.ProcessId ? ProcessInfo.WorkingDirectory(pgid) : null,
        () => ProcessInfo.WorkingDirectory(Pty.ProcessId),
        defaultCwd);

    /// <summary>A short label: the foreground program, or the title the app set.</summary>
    public string Label => Pty.ForegroundProcessName ?? Term.Title;

    public void Dispose()
    {
        Pty.Hangup();
        Pty.Dispose();
    }
}
