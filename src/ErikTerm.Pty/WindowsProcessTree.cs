namespace ErikTerm.Pty;

/// <summary>
/// One process from a Toolhelp snapshot, with what the foreground pick needs. <paramref name="CreationTime"/> is a
/// FILETIME (100 ns ticks since 1601, UTC; GetProcessTimes), 0 when it couldn't be read. <paramref name="Windowed"/>:
/// the image is a GUI-subsystem program (notepad started from the shell), which has no part in the console.
/// <paramref name="ConsoleHost"/>: the pid of the console host (conhost.exe / OpenConsole.exe) the process is attached
/// to, 0 when unknown or not asked (see <see cref="WindowsProcessTree"/>, step 2).
/// </summary>
internal readonly record struct ProcessEntry(int Pid, int ParentPid, string ExeName, long CreationTime, bool Windowed = false, int ConsoleHost = 0);

/// <summary>
/// Which program a ConPTY pane is running. A console has no foreground process group (nothing like <c>tcgetpgrp</c>),
/// so this is inferred from the process tree under the pane's own process, the way wezterm does on Windows: the
/// youngest process in the tree is what the user started last. Pure (no OS calls), so it is unit-tested everywhere.
/// <list type="number">
/// <item>The tree: a process is a child of its parent pid only if it was created no earlier than that parent — pids
/// are reused quickly on Windows, and an orphan whose parent exited would otherwise look like the child of whatever
/// process got that pid next. Console hosts (conhost.exe, OpenConsole.exe), GUI programs and anything they started
/// are left out, and so are processes younger than <see cref="SettleTicks"/>: prompt helpers (oh-my-posh, starship,
/// posh-git's <c>git status</c>) that live for a moment at every prompt would otherwise flash through tab titles.</item>
/// <item>Adopted into it, as children of the root: processes attached to the pane's console (their console host is the
/// root's) whose parent is gone. MSYS2 and Cygwin emulate fork: a program Git Bash's sh starts the usual way (fork, then
/// exec) is a Windows process created by the forked sh, which exits once it has handed over, so <c>sleep</c>,
/// <c>vim</c> or <c>npm run dev</c> would otherwise never be reached from the root. Its console says where it belongs,
/// whoever its parent was.</item>
/// <item>The youngest process in that tree (by creation time, never by pid). It is always a leaf, since a child is
/// created after its parent. With none, the root itself.</item>
/// <item>On the path from the root down to it, the first process that isn't a shell: like the leader of a Unix
/// foreground job, that is what the user ran — nvim rather than the language server nvim started, Claude Code
/// rather than its MCP servers, cargo rather than rustc. When the path is all shells, the innermost one.</item>
/// </list>
/// </summary>
internal static class WindowsProcessTree
{
    /// <summary>How old a process must be to count (250 ms, in FILETIME ticks); the root always counts.</summary>
    public const long SettleTicks = 250 * TimeSpan.TicksPerMillisecond;

    /// <summary>Upper bound on the processes looked at under one root (a fork bomb shouldn't stall the UI thread).</summary>
    public const int MaxDescendants = 4096;

    private static readonly HashSet<string> ConsoleHosts = new(StringComparer.OrdinalIgnoreCase) { "conhost.exe", "OpenConsole.exe" };

    /// <summary>Shells (and cmd, which also runs .cmd/.bat wrappers such as npm's claude.cmd): stepped through on the path.</summary>
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "pwsh", "powershell", "cmd", "bash", "sh", "zsh", "fish", "nu", "dash", "ksh", "mksh", "tcsh", "csh", "elvish", "xonsh", "tcc", "busybox" };

    /// <summary>
    /// The foreground process of the tree rooted at <paramref name="rootPid"/> (see the class summary), or null when
    /// the root isn't in <paramref name="processes"/> as the process created at <paramref name="rootCreated"/> (it has
    /// exited). <paramref name="now"/> is the snapshot's time (FILETIME) for <see cref="SettleTicks"/>.
    /// <paramref name="consoleHost"/> is the root's <see cref="ProcessEntry.ConsoleHost"/> (0: none adopted).
    /// </summary>
    public static ProcessEntry? Foreground(IReadOnlyList<ProcessEntry> processes, int rootPid, long rootCreated, long now, int consoleHost = 0)
    {
        var children = ChildrenByParent(processes, rootPid, rootCreated, out int root);
        if (root < 0) return null;
        if (consoleHost > 0) Adopt(processes, children, root, consoleHost);

        var parentOf = new Dictionary<int, int> { [root] = -1 }; // also the visited set: a pid-reuse cycle can't loop
        int youngest = FindYoungest(processes, children, root, now - SettleTicks, parentOf);

        var path = new List<int>();
        for (int i = youngest; i >= 0; i = parentOf[i]) path.Add(i);
        for (int j = path.Count - 1; j >= 0; j--)
            if (!IsShell(processes[path[j]].ExeName)) return processes[path[j]];
        return processes[youngest];
    }

    /// <summary>Indexes of each parent pid's processes; <paramref name="root"/> is the index of the root process, or -1.</summary>
    private static Dictionary<int, List<int>> ChildrenByParent(IReadOnlyList<ProcessEntry> processes, int rootPid, long rootCreated, out int root)
    {
        root = -1;
        var children = new Dictionary<int, List<int>>();
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            if (p.Pid == rootPid)
            {
                if (p.CreationTime == rootCreated) root = i;
                continue; // the root is nobody's child here
            }
            if (p.Pid <= 0 || p.Pid == p.ParentPid) continue; // the idle process (pid 0, its own parent)
            if (!children.TryGetValue(p.ParentPid, out var list)) children[p.ParentPid] = list = [];
            list.Add(i);
        }
        return children;
    }

    /// <summary>
    /// Makes the processes on the root's console whose parent isn't among <paramref name="processes"/> (exited, or its
    /// pid reused by a younger process) children of the root. Only ones created after the root: an older process on the
    /// same console host can't be the pane's.
    /// </summary>
    private static void Adopt(IReadOnlyList<ProcessEntry> processes, Dictionary<int, List<int>> children, int root, int consoleHost)
    {
        var top = processes[root];
        Dictionary<int, int>? indexOf = null;
        List<int>? adopted = null;
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            if (p.ConsoleHost != consoleHost || p.Pid == top.Pid || p.CreationTime <= 0 || p.CreationTime < top.CreationTime) continue;
            indexOf ??= IndexOfPid(processes);
            if (indexOf.TryGetValue(p.ParentPid, out int parent) && IsParent(processes[parent], p)) continue; // reached through it
            (adopted ??= []).Add(i);
        }
        if (adopted is null) return;
        if (!children.TryGetValue(top.Pid, out var list)) children[top.Pid] = list = [];
        list.AddRange(adopted);
    }

    private static Dictionary<int, int> IndexOfPid(IReadOnlyList<ProcessEntry> processes)
    {
        var indexOf = new Dictionary<int, int>(processes.Count);
        for (int i = 0; i < processes.Count; i++) indexOf.TryAdd(processes[i].Pid, i);
        return indexOf;
    }

    /// <summary>Whether <paramref name="parent"/> (the process with the child's parent pid) is its parent: not created after it.</summary>
    private static bool IsParent(in ProcessEntry parent, in ProcessEntry child) =>
        parent.Pid != child.Pid && parent.CreationTime > 0 && parent.CreationTime <= child.CreationTime;

    /// <summary>Walks the tree below the root, filling <paramref name="parentOf"/>; returns the youngest process's index.</summary>
    private static int FindYoungest(IReadOnlyList<ProcessEntry> processes, Dictionary<int, List<int>> children, int root, long settled, Dictionary<int, int> parentOf)
    {
        var depthOf = new Dictionary<int, int> { [root] = 0 };
        var pending = new Stack<int>();
        pending.Push(root);
        int youngest = root;
        while (pending.Count > 0 && parentOf.Count <= MaxDescendants)
        {
            int at = pending.Pop();
            var parent = processes[at];
            if (!children.TryGetValue(parent.Pid, out var kids)) continue;
            foreach (int k in kids)
            {
                var child = processes[k];
                if (!BelongsToTree(child, parent, settled) || !parentOf.TryAdd(k, at)) continue;
                int depth = depthOf[at] + 1;
                depthOf[k] = depth;
                if (IsYounger(child, depth, processes[youngest], depthOf[youngest])) youngest = k;
                pending.Push(k);
            }
        }
        return youngest;
    }

    private static bool BelongsToTree(in ProcessEntry child, in ProcessEntry parent, long settled)
    {
        if (child.CreationTime <= 0 || child.CreationTime < parent.CreationTime) return false; // unknown, or an orphan of an earlier pid
        return child.CreationTime <= settled && !child.Windowed && !IsConsoleHost(child.ExeName);
    }

    /// <summary>Equal times (the clock is coarse): the deeper one, which is the child of the other.</summary>
    private static bool IsYounger(in ProcessEntry candidate, int depth, in ProcessEntry best, int bestDepth) =>
        candidate.CreationTime > best.CreationTime || candidate.CreationTime == best.CreationTime && depth >= bestDepth;

    /// <summary>conhost.exe / OpenConsole.exe: the console host a program started with a console of its own.</summary>
    public static bool IsConsoleHost(string exeName) => ConsoleHosts.Contains(exeName);

    /// <summary>Whether an executable ("pwsh.exe", "bash.exe") is a shell, ignoring case and ".exe".</summary>
    public static bool IsShell(string exeName) => Shells.Contains(DisplayName(exeName));

    /// <summary>
    /// The name <see cref="Pty.ForegroundProcessName"/> reports: the executable's file name without ".exe" ("nvim.exe"
    /// → "nvim"), like a Unix process name — what close confirmation, tab titles and agent detection match.
    /// </summary>
    public static string DisplayName(string exeName) =>
        exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exeName.Length > 4 ? exeName[..^4] : exeName;
}
