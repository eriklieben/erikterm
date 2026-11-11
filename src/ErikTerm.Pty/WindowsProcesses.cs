using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static ErikTerm.Pty.Kernel32;
using static ErikTerm.Pty.Win32;

namespace ErikTerm.Pty;

/// <summary>
/// What runs in a ConPTY pane, from the OS: a Toolhelp process snapshot shared by every pane (<see cref="Current"/>),
/// and a process's image path, command line and current directory (<see cref="WindowsPeb"/>). Everything fails soft:
/// a process that is gone, protected or elevated (when erikterm isn't) gives null, never an exception.
/// </summary>
[SupportedOSPlatform("windows")]
#pragma warning disable S6640 // Toolhelp and the process queries fill native structs through pointers
internal static unsafe class WindowsProcesses
#pragma warning restore S6640
{
    /// <summary>
    /// A snapshot is reused for this long. The pollers (agent status every 400 ms, tab titles every 500 ms, the pane
    /// picker's labels every frame) all ask within a tick, so a window with many panes still takes about one snapshot
    /// per tick, and the whole process at most four a second however many windows and panes it has.
    /// </summary>
    public const int SnapshotMaxAgeMs = 250;

    private static readonly Lock Gate = new();
    private static Snapshot? _current;
    private static long _takenAt = long.MinValue / 2; // Environment.TickCount64 of the last attempt
    private static long _version;
    private static long _activityAt = long.MinValue / 2; // Environment.TickCount64 of the last output or input of any pane

    /// <summary>A snapshot takes milliseconds of CPU: with panes quiet for this long, what runs in them rarely changes.</summary>
    public const int QuietAfterMs = 1000, QuietSnapshotMaxAgeMs = 5000;

    /// <summary>A pane had output or input, so what runs in it may be changing.</summary>
    public static void NoteActivity() => Volatile.Write(ref _activityAt, Environment.TickCount64);

    /// <summary>Whether a snapshot can be reused. One taken while panes were busy is short-lived, so a new program's name shows promptly.</summary>
    internal static bool IsFresh(long now, long takenAt, long activityAt) =>
        now - takenAt < SnapshotMaxAgeMs || (takenAt - activityAt >= QuietAfterMs && activityAt <= takenAt && now - takenAt < QuietSnapshotMaxAgeMs);

    /// <summary>
    /// The current process snapshot (at most <see cref="SnapshotMaxAgeMs"/> old), or null if none could be taken — then
    /// it isn't tried again before <see cref="SnapshotMaxAgeMs"/> either.
    /// </summary>
    public static Snapshot? Current()
    {
        long tick = Environment.TickCount64;
        lock (Gate)
        {
            if (IsFresh(tick, _takenAt, Volatile.Read(ref _activityAt))) return _current;
            _takenAt = tick;
            return _current = Snapshot.Take(++_version);
        }
    }

    /// <summary>Every process at one moment (pid, parent pid, exe name); creation times and image kinds on demand.</summary>
    internal sealed class Snapshot
    {
        private readonly Row[] _rows;
        private readonly Dictionary<int, int> _rowOf = new();          // pid → row
        private readonly Dictionary<int, List<int>> _children = new(); // parent pid → rows
        private readonly Dictionary<int, (long Created, bool Windowed)> _details = new();
        private readonly Lock _gate = new();

        private readonly record struct Row(int Pid, int ParentPid, string Name);

        /// <summary>Which snapshot this is (1, 2, …): what a pane remembers its answer by, without keeping the snapshot alive.</summary>
        public long Version { get; }
        /// <summary>The wall clock (FILETIME, UTC) when it was taken, for <see cref="WindowsProcessTree.SettleTicks"/>.</summary>
        public long Now { get; }

        private Snapshot(Row[] rows, long version, long now)
        {
            _rows = rows;
            Version = version;
            Now = now;
            for (int i = 0; i < rows.Length; i++)
            {
                _rowOf.TryAdd(rows[i].Pid, i);
                if (rows[i].Pid == rows[i].ParentPid) continue;
                if (!_children.TryGetValue(rows[i].ParentPid, out var list)) _children[rows[i].ParentPid] = list = [];
                list.Add(i);
            }
        }

        public static Snapshot? Take(long version)
        {
            nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE || snap == 0) return null;
            try
            {
                var rows = new List<Row>(512);
                PROCESSENTRY32W e = default;
                e.dwSize = (uint)sizeof(PROCESSENTRY32W);
                if (Process32FirstW(snap, &e) == 0) return null;
                do
                {
                    var name = new ReadOnlySpan<char>(e.szExeFile, MAX_PATH);
                    int nul = name.IndexOf('\0');
                    rows.Add(new Row((int)e.th32ProcessID, (int)e.th32ParentProcessID, Intern(nul >= 0 ? name[..nul] : name)));
                    e.dwSize = (uint)sizeof(PROCESSENTRY32W);
                } while (Process32NextW(snap, &e) != 0);
                return new Snapshot([.. rows], version, DateTime.UtcNow.ToFileTimeUtc());
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        /// <summary>
        /// The foreground process of the pane whose own process is <paramref name="rootPid"/>, created at
        /// <paramref name="rootCreated"/> (see <see cref="WindowsProcessTree"/>); null when it isn't running.
        /// Only the root's descendants are opened (for their creation time), each once per snapshot.
        /// </summary>
        public ProcessEntry? Foreground(int rootPid, long rootCreated)
        {
            if (!_rowOf.TryGetValue(rootPid, out int r)) return null;
            var entries = new List<ProcessEntry> { new(rootPid, _rows[r].ParentPid, _rows[r].Name, rootCreated) };
            var created = new Dictionary<int, long> { [rootPid] = rootCreated };
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            lock (_gate)
            {
                while (queue.Count > 0 && entries.Count < WindowsProcessTree.MaxDescendants)
                {
                    int pid = queue.Dequeue();
                    if (!_children.TryGetValue(pid, out var kids)) continue;
                    foreach (int k in kids)
                    {
                        var row = _rows[k];
                        if (created.ContainsKey(row.Pid)) continue;
                        var (c, windowed) = Details(row.Pid);
                        created[row.Pid] = c;
                        entries.Add(new ProcessEntry(row.Pid, row.ParentPid, row.Name, c, windowed));
                        // Not below an orphan of an earlier pid (WindowsProcessTree drops it), a GUI program or a console host.
                        if (c > 0 && c >= created[pid] && !windowed && !WindowsProcessTree.IsConsoleHost(row.Name)) queue.Enqueue(row.Pid);
                    }
                }
            }
            return WindowsProcessTree.Foreground(entries, rootPid, rootCreated, Now);
        }

        private (long Created, bool Windowed) Details(int pid)
        {
            if (_details.TryGetValue(pid, out var d)) return d;
            d = default;
            nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, (uint)pid);
            if (h != 0)
            {
                try
                {
                    d.Created = ProcessCreationTime(h);
                    if (d.Created > 0) d.Windowed = IsWindowed(h, pid, d.Created);
                }
                finally
                {
                    CloseHandle(h);
                }
            }
            return _details[pid] = d;
        }

        // ---- Names and image kinds, cached across snapshots --------------------------------------------

        private static readonly HashSet<string> Names = new(StringComparer.Ordinal);
        private static readonly Dictionary<(int Pid, long Created), bool> WindowedProcesses = new();
        private static readonly Dictionary<string, bool> WindowedImages = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>One string per exe name, so a snapshot of a few hundred processes allocates nothing new.</summary>
        private static string Intern(ReadOnlySpan<char> name)
        {
            // Callers hold Gate (Current → Take).
            var lookup = Names.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(name, out var s)) return s;
            if (Names.Count > 4096) Names.Clear();
            s = name.ToString();
            Names.Add(s);
            return s;
        }

        /// <summary>
        /// Whether the process runs a GUI-subsystem image (notepad, an editor's window): one started from the shell stays
        /// its child, but it isn't attached to the console and doesn't end with the pane. Keyed by (pid, creation time),
        /// which no other process can share; the image's PE header is read once per path.
        /// </summary>
        private static bool IsWindowed(nint process, int pid, long created)
        {
            lock (WindowedImages)
            {
                if (WindowedProcesses.TryGetValue((pid, created), out bool known)) return known;
                bool windowed = false;
                // Not over the network: a header read from a slow share would hold up the UI thread.
                if (ImagePath(process) is { } path && !path.StartsWith(@"\\", StringComparison.Ordinal) && !WindowedImages.TryGetValue(path, out windowed))
                {
                    windowed = ReadSubsystem(path) == PeImage.SubsystemWindowsGui;
                    if (WindowedImages.Count > 1024) WindowedImages.Clear();
                    WindowedImages[path] = windowed;
                }
                if (WindowedProcesses.Count > 4096) WindowedProcesses.Clear();
                WindowedProcesses[(pid, created)] = windowed;
                return windowed;
            }
        }

        private static ushort? ReadSubsystem(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                Span<byte> header = stackalloc byte[PeImage.HeaderBytes];
                int n = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                return PeImage.Subsystem(header[..n]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static long ProcessCreationTime(nint process)
        {
            long created, exited, kernel, user;
            return GetProcessTimes(process, &created, &exited, &kernel, &user) != 0 ? created : 0;
        }
    }

    // ---- One process ---------------------------------------------------------------------------------------

    /// <summary>A process's creation time (FILETIME), or 0.</summary>
    public static long CreationTime(SafeProcessHandle process)
    {
        long created, exited, kernel, user;
        try { return GetProcessTimes(process, &created, &exited, &kernel, &user) != 0 ? created : 0; }
        catch (ObjectDisposedException) { return 0; }
    }

    /// <summary>The executable's full path (QueryFullProcessImageNameW), or null.</summary>
    public static string? ImagePath(int pid)
    {
        if (pid <= 0) return null;
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, (uint)pid);
        if (h == 0) return null;
        try { return ImagePath(h); }
        finally { CloseHandle(h); }
    }

    private static string? ImagePath(nint process)
    {
        const int Small = 512;
        char* buf = stackalloc char[Small];
        uint size = Small;
        if (QueryFullProcessImageNameW(process, 0, buf, &size) != 0) return new string(buf, 0, (int)size);
        if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER) return null;
        var big = new char[short.MaxValue];
        size = (uint)big.Length;
        fixed (char* p = big)
            return QueryFullProcessImageNameW(process, 0, p, &size) != 0 ? new string(p, 0, (int)size) : null;
    }

    /// <summary>The process's command line as CreateProcess passed it (RTL_USER_PROCESS_PARAMETERS.CommandLine), or null.</summary>
    public static string? CommandLine(int pid) => ReadParameter(pid, WindowsPeb.CommandLineOffset, nativeOnly: false);

    /// <summary>
    /// The process's current directory (RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath, without the trailing
    /// backslash), or null. Not for a 32-bit process (WOW64): its 32-bit side keeps a copy of its own that
    /// SetCurrentDirectory changes, and the 64-bit one stays where it started.
    /// </summary>
    public static string? CurrentDirectory(int pid) =>
        ReadParameter(pid, WindowsPeb.CurrentDirectoryOffset, nativeOnly: true) is { } dir ? WindowsPeb.TrimDirectory(dir) : null;

    /// <summary>Whether a process with this pid exists: open and not signalled, or there but not ours to open.</summary>
    public static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, 0, (uint)pid);
        if (h == 0) return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED; // like EPERM: someone else's
        try { return WaitForSingleObject(h, 0) == WAIT_TIMEOUT; }
        finally { CloseHandle(h); }
    }

    /// <summary>A UNICODE_STRING from the process's RTL_USER_PROCESS_PARAMETERS (64-bit layout; see <see cref="WindowsPeb"/>).</summary>
    private static string? ReadParameter(int pid, int offset, bool nativeOnly)
    {
        if (pid <= 0 || !Environment.Is64BitProcess) return null;
        // PROCESS_VM_READ is refused for an elevated process (when erikterm isn't) and for protected ones: null.
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, 0, (uint)pid);
        if (h == 0) return null;
        try
        {
            if (nativeOnly)
            {
                int wow64;
                if (IsWow64Process(h, &wow64) == 0 || wow64 != 0) return null;
            }
            PROCESS_BASIC_INFORMATION pbi;
            uint returned;
            if (Ntdll.NtQueryInformationProcess(h, WindowsPeb.ProcessBasicInformation, &pbi, (uint)sizeof(PROCESS_BASIC_INFORMATION), &returned) < 0)
                return null;
            if (pbi.PebBaseAddress == 0) return null; // the System process, minimal processes: no PEB

            ulong parameters;
            if (!Read(h, (ulong)pbi.PebBaseAddress + WindowsPeb.ProcessParametersOffset, &parameters, sizeof(ulong)) || parameters == 0)
                return null;
            byte* block = stackalloc byte[WindowsPeb.ParametersReadSize];
            if (!Read(h, parameters, block, WindowsPeb.ParametersReadSize)) return null;
            if (WindowsPeb.UnicodeString(new ReadOnlySpan<byte>(block, WindowsPeb.ParametersReadSize), offset, parameters) is not { } text)
                return null;

            var chars = new char[text.ByteLength / sizeof(char)];
            fixed (char* p = chars)
                if (!Read(h, text.Address, p, text.ByteLength)) return null;
            return new string(chars);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static bool Read(nint process, ulong address, void* buffer, int size)
    {
        nuint read;
        return ReadProcessMemory(process, (void*)address, buffer, (nuint)size, &read) != 0 && read == (nuint)size;
    }
}
