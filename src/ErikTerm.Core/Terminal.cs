using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ErikTerm.Core.Images;

namespace ErikTerm.Core;

public enum CursorShape : byte { Block, Underline, Bar }

public enum MouseTracking : byte { None, Press, ButtonMotion, AnyMotion }

public delegate void ResponseWriter(ReadOnlySpan<byte> data);

/// <summary>
/// The terminal state machine: screen grid, cursor, modes and scrollback, driven by <see cref="VtParser"/>.
/// Not thread-safe — callers serialise access (the app holds a lock while feeding and while rendering).
/// Reference: https://invisible-island.net/xterm/ctlseqs/ctlseqs.html
/// <para>
/// This class keeps the grid, the cursor and the pen, and everything the parser calls per byte, per character and per
/// cell: printing, C0 controls, ESC and CSI dispatch, cursor movement, scrolling, editing and SGR all stay direct calls
/// on its own fields. The colder parts are classes of their own that it holds: <see cref="SynchronizedOutputGate"/>
/// (DEC 2026), <see cref="ScreenResizer"/> (resize and reflow), <see cref="InputModes"/>, <see cref="TerminalReplies"/>,
/// <see cref="ColorReports"/> (OSC colors), <see cref="Hyperlinks"/> (OSC 8), <see cref="SemanticPrompts"/> (OSC 133),
/// <see cref="ProgramSignals"/> (OSC 7501 / 9 / 777 / 99), <see cref="UserVariables"/>, <see cref="ProgramTabColor"/>,
/// the image protocols (<see cref="KittyGraphics"/>, <see cref="CellImages"/>, <see cref="PlacementTracker"/>) and the
/// styled export (<see cref="StyledTextExport"/>).
/// </para>
/// </summary>
public sealed class Terminal : ISynchronizedOutputTarget, ILinkedRows, ITerminalLines, IImageGrid
{
    /// <summary>Parser callback adapter; a struct so the parser is specialised for it (see VtParser).</summary>
    private readonly struct Sink(Terminal t) : IVtHandler
    {
        public void PrintAscii(ReadOnlySpan<byte> text) => t.PrintAscii(text);
        public int PrintUtf8(ReadOnlySpan<byte> text) => t.PrintUtf8(text);
        public void Print(int codepoint) => t.Print(codepoint);
        public void Execute(byte control) => t.Execute(control);
        public void EscDispatch(ReadOnlySpan<byte> intermediates, byte final) => t.EscDispatch(intermediates, final);
        public void CsiDispatch(CsiParams parameters, ReadOnlySpan<byte> intermediates, byte privateMarker, byte final) => t.CsiDispatch(parameters, intermediates, privateMarker, final);
        public void CursorPosition(int row, int col) => t.CursorPosition(row, col);
        public void OscDispatch(ReadOnlySpan<byte> data) => t.OscDispatch(data);
        public void ApcDispatch(ReadOnlySpan<byte> data) => t.ApcDispatch(data); // kitty graphics
        public void DcsDispatch(ReadOnlySpan<byte> data) => t.DcsDispatch(data); // sixel, XTGETTCAP, DECRQSS
    }

    private struct SavedCursor
    {
        public int X, Y;
        public uint Fg, Bg, UnderlineColor;
        public CellAttributes Attr;
        public bool OriginMode, PendingWrap, G0Graphics, G1Graphics, UseG1;
        public bool Valid;
    }

    private readonly VtParser<Sink> _parser;
    private readonly Scrollback _scrollback;
    private readonly CellArrayPool _cellPool = new(); // trimmed scrollback rows' arrays
    private readonly ImageStore _images = new(); // asked HasPlacements on every scroll
    private int _cols, _rows;
    private Row[] _main, _alt, _lines;
    private bool _altActive;

    private int _x, _y;
    private bool _pendingWrap;
    private uint _fg, _bg;
    /// <summary>SGR 58 underline color of the pen (a <see cref="TermColor"/>; 0 = the text color).</summary>
    private uint _ulColor;
    private CellAttributes _attr;
    private int _top, _bottom;
    private bool[] _tabs;

    private bool _autoWrap = true, _originMode, _insertMode, _newLineMode;
    private bool _g0Graphics, _g1Graphics, _useG1;
    private SavedCursor _savedMain, _savedAlt;
    private int _lastPrinted = ' ';
    /// <summary>OSC 8 link printed characters belong to (0 = none), part of the pen. Checked once per printed run.</summary>
    private int _linkId;
    /// <summary>Strings of the clusters this terminal printed; created by the first cluster.</summary>
    private ClusterStringPool? _clusterStrings;

    // The parts this terminal holds. Declared after the grid, cursor and pen so that those fields, read on every
    // byte and cell, keep the small offsets they had (the runtime lays out references in declaration order).
    private readonly ScrollbackCompactor _compactor;
    private readonly TerminalReplies _replies = new();
    private readonly SynchronizedOutputGate _sync;
    private readonly ScreenResizer _resizer;
    private readonly InputModes _input = new();
    private readonly ColorReports _colors;
    private readonly Hyperlinks _hyperlinks;
    private readonly SemanticPrompts _shell;
    private readonly ProgramSignals _signals;
    private readonly UserVariables _userVars = new();
    private readonly ProgramTabColor _tabColor = new();
    private readonly PlacementTracker _placements;
    private readonly KittyGraphics _kitty;
    private readonly CellImages _cellImages;

    public Terminal(int cols, int rows, int scrollbackLines = 10_000, Palette? palette = null)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _parser = new VtParser<Sink>(new Sink(this));
        _scrollback = new Scrollback(scrollbackLines);
        Palette = palette ?? new Palette();
        _main = NewBuffer(_cols, _rows);
        _alt = NewBuffer(_cols, _rows);
        _lines = _main;
        _tabs = NewTabs(_cols);
        _bottom = _rows - 1;
        _sync = new SynchronizedOutputGate(this);
        _compactor = new ScrollbackCompactor(_scrollback, _cellPool);
        _resizer = new ScreenResizer(_scrollback, _cellPool, _images);
        _colors = new ColorReports(Palette, _replies);
        _hyperlinks = new Hyperlinks(this);
        _shell = new SemanticPrompts(this);
        _signals = new ProgramSignals(_replies);
        _placements = new PlacementTracker(_images);
        _kitty = new KittyGraphics(_images, this, _replies);
        _cellImages = new CellImages(_images, this);
    }

    // ---- Public state -------------------------------------------------------------------------

    public Palette Palette { get; }
    public int Columns => _cols;
    public int Rows => _rows;
    public int CursorX => _x;
    public int CursorY => _y;
    public bool CursorVisible { get; private set; } = true;
    public CursorShape CursorShape { get; private set; } = CursorShape.Block;

    /// <summary>Shape used until an app asks for another (DECSCUSR), and restored by DECSCUSR 0 / reset.</summary>
    public CursorShape DefaultCursorShape
    {
        get => _defaultCursorShape;
        set
        {
            if (CursorShape == _defaultCursorShape) CursorShape = value;
            _defaultCursorShape = value;
            Dirty = true;
        }
    }
    private CursorShape _defaultCursorShape = CursorShape.Block;

    /// <summary>Whether the cursor blinks: DECSCUSR 1/3/5 (on) or 2/4/6 (off), else <see cref="DefaultCursorBlink"/>.</summary>
    public bool CursorBlink => _cursorBlink ?? DefaultCursorBlink;
    public bool DefaultCursorBlink { get; set; }
    private bool? _cursorBlink;

    /// <summary>Incremented by every <see cref="Feed"/>, so caches of the content (search) know when to refresh.</summary>
    public long ContentVersion { get; private set; }
    public bool CursorKeysApplication => _input.CursorKeysApplication;
    public bool KeypadApplication => _input.KeypadApplication;
    public bool BracketedPaste => _input.BracketedPaste;
    public bool FocusReporting => _input.FocusReporting;
    public bool AlternateScroll => _input.AlternateScroll;
    public MouseTracking MouseTracking => _input.MouseTracking;
    public bool MouseSgr => _input.MouseSgr;
    public bool SynchronizedOutput { get; private set; }

    bool ISynchronizedOutputTarget.SynchronizedOutput
    {
        get => SynchronizedOutput;
        set => SynchronizedOutput = value;
    }
    public bool IsAlternateScreen => _altActive;

    /// <summary>
    /// Kitty keyboard protocol flags in effect for the active screen (each screen has its own stack). Pass them to the
    /// key encoder; <see cref="KittyKeyboardModes.None"/> means legacy xterm encoding.
    /// </summary>
    public KittyKeyboardModes KeyboardFlags => KeyboardStack.Current;

    /// <summary>Where ignored escape sequences are logged (ERIKTERM_LOG_UNHANDLED; null = off). See <see cref="UnhandledLog"/>.</summary>
    public UnhandledLog? Unhandled { get; set; } = UnhandledLog.Shared;

    private KittyKeyboardStack KeyboardStack => _input.Keyboard(_altActive);
    public string Title { get; private set; } = "erikterm";
    public string? WorkingDirectoryUri { get; private set; }
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Scrollback storage arrays that had to be allocated rather than recycled (tests, diagnostics).</summary>
    internal long CellPoolMisses => _cellPool.Misses;

    /// <summary>
    /// Scrollback rows wait to be compacted (<see cref="CompactScrollback"/>). Safe to read without the lock (a hint:
    /// lets an idle reader thread skip taking it).
    /// </summary>
    public bool NeedsCompaction => _compactor.NeedsCompaction;

    /// <summary>Bytes of cell storage released so far by <see cref="CompactScrollback"/> and <see cref="ReleaseSpareRows"/> (monotonic).</summary>
    public long CompactedBytes => _compactor.CompactedBytes;

    /// <summary>A cleared scrollback left rows for the next flood to reuse. Safe to read without the lock (a hint).</summary>
    public bool HasSpareRows => _compactor.HasSpareRows;

    /// <summary>Lets go of the rows a cleared scrollback left behind, so that a full GC can return their memory.</summary>
    public void ReleaseSpareRows() => _compactor.ReleaseSpareRows(_cols, _main, _alt);

    /// <summary>
    /// Packs or trims up to <paramref name="maxRows"/> scrollback rows, oldest first (<see cref="ScrollbackCompactor"/>).
    /// Lines enter the scrollback whole, so scrolling stays as cheap as it is without this; call it when there is time
    /// (the PTY reader does when no output is waiting). Rows whose arrays are on the pinned heap (made while the
    /// scrollback filled) move to ordinary arrays. Nothing visible changes. Returns true while rows remain.
    /// </summary>
    public bool CompactScrollback(int maxRows) => _compactor.Compact(maxRows, _cols, _main, _alt);

    /// <summary>Lines scrolled back from the live screen (0 = following output).</summary>
    public int ViewOffset { get; private set; }

    /// <summary>Set whenever visible state changes; the renderer clears it.</summary>
    public bool Dirty { get; set; } = true;

    private long _linesScrolled;

    /// <summary>Lines scrolled up since the start, for telling a flood from ordinary output. Safe without the lock.</summary>
    public long LinesScrolled => Volatile.Read(ref _linesScrolled);

    /// <summary>Bytes the terminal sends back to the application (DA, DSR, …).</summary>
    public ResponseWriter? Response
    {
        get => _replies.Writer;
        set => _replies.Writer = value;
    }

    public event Action? Bell;

    /// <summary>Incremented on every BEL, so a renderer can notice bells without an event subscription.</summary>
    public int BellCount { get; private set; }

    /// <summary>User variables set with OSC 1337 SetUserVar (wezterm/iTerm2), e.g. IS_NVIM from smart-splits.nvim.</summary>
    public IReadOnlyDictionary<string, string> UserVars => _userVars.All;

    /// <summary>Limits on OSC 1337 user variables: how many, and the longest name and (decoded) value in bytes.</summary>
    public const int MaxUserVars = 32, MaxUserVarName = 64, MaxUserVarValue = 4096;

    // ---- Output (synchronized output: SynchronizedOutputGate) -----------------------------------

    public const int SynchronizedOutputTimeoutMs = 150;
    public const int SynchronizedOutputMaxHeld = 8 << 20;

    /// <summary>Whether output is waiting for the end of a synchronized update.</summary>
    public bool HoldsSynchronizedOutput => _sync.Holds;

    /// <summary>
    /// Feeds program output. Between DEC 2026's begin and end sequences it is held back, not parsed, so the grid
    /// always shows the last complete update (see <see cref="SynchronizedOutputGate"/>).
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data) => _sync.Feed(data, Environment.TickCount64);

    internal void Feed(ReadOnlySpan<byte> data, long now) => _sync.Feed(data, now);

    /// <summary>Parses held output whose update has taken too long. True when it did (the caller holds the terminal's lock).</summary>
    public bool ReleaseExpiredSynchronizedOutput(long now) => _sync.ReleaseExpired(now);

    void ISynchronizedOutputTarget.Parse(ReadOnlySpan<byte> data)
    {
        _parser.Feed(data);
        Dirty = true;
        ContentVersion++;
    }

    // ---- Scrollback limit and styled export ------------------------------------------------------

    /// <summary>
    /// How many lines the scrollback keeps. Setting it applies immediately: the newest lines are kept and
    /// the dropped ones advance <see cref="FirstLine"/>, so absolute line numbers stay coherent.
    /// </summary>
    public int ScrollbackCapacity
    {
        get => _scrollback.Capacity;
        set
        {
            if (Math.Max(0, value) == _scrollback.Capacity) return;
            _scrollback.SetCapacity(value);
            ViewOffset = Math.Min(ViewOffset, _altActive ? 0 : _scrollback.Count);
            Dirty = true;
        }
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> logical lines of the main screen and its scrollback (soft-wrapped rows
    /// joined, trailing blank rows skipped), each as text with SGR sequences for colors and attributes. Each line
    /// starts from default attributes. Hyperlinks and other metadata are not included. For session restore.
    /// </summary>
    public List<string> ExportStyledLines(int maxLines) => StyledTextExport.Export(_scrollback, _main, maxLines);

    // ---- OSC 8 hyperlinks (Hyperlinks) ------------------------------------------------------------

    public Hyperlink? GetHyperlink(int id) => _hyperlinks.Get(id);

    /// <summary>OSC 8 link at an absolute position, or null.</summary>
    public Hyperlink? HyperlinkAt(long line, int col) => GetLine(line) is { } row ? GetHyperlink(row.LinkIdAt(col)) : null;

    IEnumerable<Row?> ILinkedRows.AllRows()
    {
        for (int i = 0; i < _scrollback.Count; i++) yield return _scrollback.Peek(i);
        foreach (var row in _main) yield return row;
        foreach (var row in _alt) yield return row;
    }

    // ---- OSC 52 clipboard (ClipboardOsc) ------------------------------------------------------------

    /// <summary>Programs may set the clipboard with OSC 52 (reading it back is never allowed).</summary>
    public bool AllowClipboardWrite { get; set; } = true;

    /// <summary>
    /// A program asked to set a clipboard (OSC 52). Raised on the thread feeding the terminal, under
    /// its lock — handlers should queue the request for the UI thread.
    /// </summary>
    public event Action<ClipboardTarget, string>? ClipboardRequested;

    private void Osc52(ReadOnlySpan<byte> arg)
    {
        if (!AllowClipboardWrite || ClipboardRequested is null) return;
        if (!ClipboardOsc.TryParse(arg, out string text, out bool clipboard, out bool primary)) return;
        if (clipboard) ClipboardRequested(ClipboardTarget.Clipboard, text);
        if (primary) ClipboardRequested(ClipboardTarget.Primary, text);
    }

    // ---- OSC 6 tab color (ProgramTabColor) -----------------------------------------------------------

    /// <summary>
    /// The tab color a program asked for with iTerm2's <c>OSC 6 ; 1 ; bg ; red|green|blue ; brightness ; N</c> (0xRRGGBB;
    /// components not sent yet are 0), or null — never set, or reset with <c>OSC 6 ; 1 ; bg ; * ; default</c> or RIS.
    /// </summary>
    public uint? ProgramTabColor => _tabColor.Value;

    // ---- OSC 133 semantic prompts (SemanticPrompts) -------------------------------------------------

    /// <summary>A command started (OSC 133;C) and hasn't finished (D) or been replaced by a new prompt (A).</summary>
    public bool CommandRunning => _shell.CommandRunning && !_altActive;

    /// <summary>Commands started so far (OSC 133;C): lets callers tell command output from a prompt redraw.</summary>
    public long CommandsStarted => _shell.CommandsStarted;

    /// <summary>Exit status of the last command that reported one (OSC 133;D), or null.</summary>
    public int? LastExitCode => _shell.LastExitCode;

    /// <summary>Commands that finished so far (OSC 133;D after C); <see cref="LastFinishedCommand"/> is the latest.</summary>
    public long CommandsFinished => _shell.CommandsFinished;

    /// <summary>The latest command that finished (valid once <see cref="CommandsFinished"/> &gt; 0).</summary>
    public FinishedCommand LastFinishedCommand => _shell.LastFinishedCommand;

    /// <summary>
    /// The <paramref name="index"/>-th command that finished (0 = the first), for callers that look less often than
    /// commands can finish; only the latest few are kept.
    /// </summary>
    public bool TryGetFinishedCommand(long index, out FinishedCommand command) => _shell.TryGetFinishedCommand(index, out command);

    /// <summary>Monotonic milliseconds for command durations (tests replace it).</summary>
    public Func<long> CommandClock
    {
        get => _shell.CommandClock;
        set => _shell.CommandClock = value;
    }

    /// <summary>
    /// The command line of a finished command: the one the shell sent, else the text typed after its prompt
    /// (OSC 133;B up to where output began), or "" when that has scrolled out of the history.
    /// </summary>
    public string CommandText(in FinishedCommand command) => _shell.CommandText(command);

    /// <summary>The nearest prompt line before (dir &lt; 0) or after (dir &gt; 0) <paramref name="fromLine"/>.</summary>
    public long? FindPrompt(long fromLine, int dir) => _altActive ? null : _shell.FindPrompt(fromLine, dir);

    /// <summary>Scrolls the view so <paramref name="line"/> is at the top (as far as the history allows).</summary>
    public void ScrollViewToLine(long line)
    {
        long offset = Math.Clamp(ScreenTopLine - line, 0, _altActive ? 0 : _scrollback.Count);
        ScrollView((int)(offset - ViewOffset));
    }

    /// <summary>True when any row carries OSC 133 marks (the shell has integration).</summary>
    public bool HasPromptMarks => _shell.HasPromptMarks;

    /// <summary>
    /// The command whose prompt or output contains <paramref name="line"/>. A line on the current
    /// (still editable) prompt, or on a prompt whose command printed nothing, refers to the previous
    /// command — so "the command at the cursor" means the last one that ran.
    /// </summary>
    public CommandBlock? CommandAt(long line) => _altActive ? null : _shell.CommandAt(line, ScreenTopLine + _y);

    /// <summary>OSC 133 ; A|B|C|D [; params]; ignored on the alternate screen.</summary>
    private void Osc133(ReadOnlySpan<byte> arg)
    {
        if (arg.IsEmpty || _altActive) return;
        if (!_shell.Mark(arg, _lines[_y], ScreenTopLine + _y, _x)) return;
        // A prompt is back: nothing is still working, nor is anything in progress (OSC 9;4 a program didn't take back).
        _signals.ClearStatus(transientOnly: true);
        _signals.ClearProgress();
        // Back at the shell: user variables (IS_NVIM, …) belonged to the program that set them.
        _userVars.Clear();
    }

    // ---- Agent signals (ProgramSignals) -----------------------------------------------------------------

    /// <summary>Answer OSC 7501 probes, so programs (Claude Code, Codex) report their state. On by default.</summary>
    public bool ProgramStatusEnabled
    {
        get => _signals.Enabled;
        set => _signals.Enabled = value;
    }

    /// <summary>Bumped whenever a program status record, a notification or the progress changes.</summary>
    public int AgentSignalVersion => _signals.Version;

    /// <summary>Current OSC 7501 records (main record has Id ""). Read under the terminal's lock.</summary>
    public IReadOnlyCollection<ProgramStatusRecord> ProgramStatus => _signals.Records;

    /// <summary>
    /// OSC 9 ; 4 progress: state 0 none, 1 normal, 2 error, 3 indeterminate, 4 paused; value 0–100. Cleared by
    /// <c>OSC 9;4;0</c>, a new shell prompt (OSC 133;A) and RIS.
    /// </summary>
    public (int State, int Value) Progress => _signals.Progress;

    /// <summary>Notifications requested since the last drain (at most 8 are kept).</summary>
    public int PendingNotificationCount => _signals.PendingNotificationCount;

    /// <summary>Moves pending notifications into <paramref name="into"/>.</summary>
    public void DrainNotifications(List<TerminalNotification> into) => _signals.DrainNotifications(into);

    /// <summary>
    /// Drops program status records — all of them, or (<paramref name="transientOnly"/>) only working/blocked ones,
    /// which can't outlive the program or its turn (it exited, or a shell prompt came back).
    /// </summary>
    public void ClearProgramStatus(bool transientOnly) => _signals.ClearStatus(transientOnly);

    // ---- Absolute line numbering ---------------------------------------------------------------
    // Line numbers that stay attached to content while it scrolls: scrollback line i is
    // Dropped + i, screen row y is Dropped + ScrollbackCount + y. Used for selections.

    public long FirstLine => _scrollback.Dropped;
    public long ScreenTopLine => _scrollback.Dropped + _scrollback.Count;
    public long ViewTopLine => ScreenTopLine - ViewOffset;
    public long EndLine => ScreenTopLine + _rows;

    public Row? GetLine(long line)
    {
        long i = line - _scrollback.Dropped;
        if (i < 0) return null;
        if (i < _scrollback.Count) return _scrollback[(int)i];
        i -= _scrollback.Count;
        return i < _rows ? _lines[(int)i] : null;
    }

    /// <summary>Text between two absolute positions (inclusive), joining soft-wrapped rows and trimming line ends.</summary>
    public string GetText(long startLine, int startCol, long endLine, int endCol, bool block = false) =>
        ScreenText.Between(this, startLine, startCol, endLine, endCol, block);

    /// <summary>Column range of the "word" under a position (for double-click selection).</summary>
    public (int Start, int End) WordAt(long line, int col) => ScreenText.WordAt(GetLine(line), col);

    /// <summary>Row as currently displayed, accounting for <see cref="ViewOffset"/>. May be wider or narrower than <see cref="Columns"/>.</summary>
    public Row GetVisibleRow(int y)
    {
        if (ViewOffset > 0 && y < ViewOffset)
            return _scrollback[_scrollback.Count - ViewOffset + y];
        return _lines[y - ViewOffset];
    }

    public Row GetScreenRow(int y) => _lines[y];

    public void ScrollView(int delta)
    {
        int max = _altActive ? 0 : _scrollback.Count;
        int next = Math.Clamp(ViewOffset + delta, 0, max);
        if (next != ViewOffset) { ViewOffset = next; Dirty = true; }
    }

    public void ResetView() => ScrollView(-ViewOffset);

    public string GetLineText(int y) => ScreenText.Row(_lines[y], _cols);

    /// <summary>
    /// The grapheme cluster in a cell when it has more than one code point (base + combining marks, VS16, skin tone,
    /// ZWJ sequence, flag). Returns false for ordinary cells, whose text is just <see cref="Cell.Rune"/>.
    /// The span stays valid until the cell is next written.
    /// </summary>
    public static bool TryGetCluster(Row row, int col, out ReadOnlySpan<char> text) => row.TryGetCluster(col, out text);

    /// <summary>Text of the cell at a screen position (cluster, code point, or "" when empty / a wide trail).</summary>
    public string GetCellText(int x, int y) => ScreenText.Cell(_lines[y], x);

    public string GetScreenText()
    {
        var sb = new StringBuilder();
        for (int y = 0; y < _rows; y++) sb.Append(GetLineText(y)).Append('\n');
        return sb.ToString();
    }

    public ref readonly Cell CellAt(int x, int y) => ref _lines[y].Cells[x];

    // ---- Resize -------------------------------------------------------------------------------

    /// <summary>
    /// Resizes the grid. When the column count changes, soft-wrapped lines of the main screen and scrollback are
    /// re-wrapped to the new width (reflow) and the main cursor stays on the same character; the alternate screen
    /// is only truncated/extended. Reflow renumbers absolute lines (<see cref="FirstLine"/> etc. stay coherent, but
    /// a given line number may now refer to different content), so callers holding absolute positions (selections,
    /// search hits) should drop them when <see cref="ReflowCount"/> changes.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == _cols && rows == _rows) return;

        bool mainFromSaved = _altActive && _savedMain.Valid;
        int mainX = _x;
        if (mainFromSaved) mainX = _savedMain.X;
        else if (_altActive) mainX = 0;
        int mainY = mainFromSaved ? _savedMain.Y : _y;
        bool mainPending = mainFromSaved ? _savedMain.PendingWrap : !_altActive && _pendingWrap;
        var imageAnchors = _placements.Capture(_scrollback, _main, _alt);
        if (cols != _cols)
        {
            var cursor = new ResizeCursor { X = mainX, Y = mainY, Pending = mainPending, AutoWrap = _autoWrap };
            long promptLine = _shell.PromptLine;
            _main = _resizer.ReflowMain(_main, _cols, cols, rows, ref promptLine, ref cursor);
            _shell.PromptLine = promptLine;
            (mainX, mainY, mainPending) = (cursor.X, cursor.Y, cursor.Pending);
            ReflowCount++;
        }
        else
        {
            _main = _resizer.ResizeMain(_main, _cols, cols, rows, ref mainY);
            mainPending = false;
        }
        int altCursorY = _altActive ? _y : 0;
        _alt = _resizer.ResizeSimple(_alt, _cols, cols, rows);
        _placements.Restore(imageAnchors, _scrollback, _main, _alt);

        if (_altActive)
        {
            _savedMain.X = Math.Min(mainX, cols - 1);
            _savedMain.Y = Math.Clamp(mainY, 0, rows - 1);
            _savedMain.PendingWrap = mainPending;
            _x = Math.Min(_x, cols - 1);
            _y = Math.Clamp(altCursorY, 0, rows - 1);
            _pendingWrap = false;
            _lines = _alt;
        }
        else
        {
            _x = Math.Min(mainX, cols - 1);
            _y = Math.Clamp(mainY, 0, rows - 1);
            _pendingWrap = mainPending;
            _savedMain.X = Math.Min(_savedMain.X, cols - 1);
            _lines = _main;
        }

        _cols = cols;
        _rows = rows;
        _top = 0;
        _bottom = rows - 1;
        _tabs = NewTabs(cols);
        _savedAlt.X = Math.Min(_savedAlt.X, cols - 1);
        _savedAlt.Y = Math.Min(_savedAlt.Y, rows - 1);
        ViewOffset = Math.Min(ViewOffset, _scrollback.Count);
        Dirty = true;
        if (InBandResize) ReportInBandSize(); // DECSET 2048
    }

    /// <summary>Incremented by every resize that reflowed the main screen (absolute line numbers changed meaning).</summary>
    public int ReflowCount { get; private set; }

    /// <summary>A full-width screen row from an evicted scrollback row that isn't one (compacted, or another width).</summary>
    private Row ScreenRowFrom(Row? evicted, int cols)
    {
        if (evicted is null)
        {
            _compactor.PinnedScreenRows = true;
            return Row.NewForScrollback(cols);
        }
        if (!evicted.CellsPinned) _cellPool.Return(evicted.Cells, cols);
        evicted.SetCells(_cellPool.RentFull(cols), cols); // stale contents: the caller clears the row
        return evicted;
    }

    // ---- Parser callbacks: printing -----------------------------------------------------------------

    private void PrintAscii(ReadOnlySpan<byte> text)
    {
        // Rare modes take the per-character path; the common case is a straight block copy.
        if ((_useG1 ? _g1Graphics : _g0Graphics) || _insertMode || !_autoWrap)
        {
            foreach (byte b in text) Print(b);
            return;
        }

        while (!text.IsEmpty)
        {
            if (_pendingWrap) WrapToNextLine();

            int n = Math.Min(_cols - _x, text.Length);
            WriteAsciiCells(text[..n]);

            _lastPrinted = text[n - 1];
            text = text[n..];
            _x += n;
            if (_x >= _cols)
            {
                _x = _cols - 1;
                _pendingWrap = true;
            }
        }
    }

    /// <summary>Copies a run that fits on the current row to the cursor position, with the current attributes.</summary>
    private void WriteAsciiCells(ReadOnlySpan<byte> run)
    {
        var row = _lines[_y];
        row.Touch();
        var cells = row.Cells;

        FixWideAt(cells, _x);
        StoreAsciiCells(cells.AsSpan(_x, run.Length), run);
        // The run may have replaced the first half of a wide character whose second half lies just past it.
        int end = _x + run.Length;
        if (end < _cols && (cells[end].Flags & CellAttributes.WideTrail) != 0) cells[end] = Cell.Blank(cells[end].Bg);
        TagLink(row, _x, run.Length); // OSC 8
        ApplyUnderlineColor(row, _x, run.Length);
    }

    /// <summary>Gives printed cells the current underline color, when they are underlined and the row needs colors.</summary>
    private void ApplyUnderlineColor(Row row, int x, int count)
    {
        if ((_attr & CellAttributes.Underline) != 0 && (_ulColor != 0 || row.HasUnderlineColors)) row.SetUnderlineColor(x, count, _ulColor);
    }

    private void Print(int cp)
    {
        if (cp is >= 0x5F and <= 0x7E && (_useG1 ? _g1Graphics : _g0Graphics))
            cp = DecSpecialGraphics[cp - 0x5F];

        int width = 1;
        if (cp >= 0x300 && !TryMeasureOrJoin(cp, out width)) return;

        if (_pendingWrap) WrapToNextLine();

        if (width == 2 && _x == _cols - 1 && !WrapWideCharacter()) return;

        if (_insertMode) InsertChars(width);

        WriteCell(cp, width);

        _lastPrinted = cp;
        int next = _x + width;
        if (next >= _cols)
        {
            _x = _cols - 1;
            _pendingWrap = _autoWrap;
        }
        else _x = next;
    }

    /// <summary>The width of a non-ASCII code point. False when it needs no cell: it joined the previous cluster, or it has nothing to attach to.</summary>
    private bool TryMeasureOrJoin(int cp, out int width)
    {
        byte props = CharWidth.Props(cp);
        width = props & CharWidth.WidthMask;
        if ((width == 0 || (props & JoinCandidate) != 0) && TryJoinCluster(cp, props)) return false;
        return width != 0;
    }

    /// <summary>A wide character at the last column: blanks it and wraps, or steps back without auto-wrap. False when the screen has one column.</summary>
    private bool WrapWideCharacter()
    {
        if (_cols < 2) return false;
        if (_autoWrap)
        {
            _lines[_y].Touch();
            _lines[_y].Cells[_x] = Cell.Blank(_bg);
            WrapToNextLine();
        }
        else _x = _cols - 2;
        return true;
    }

    /// <summary>Stores a code point of <paramref name="width"/> at the cursor, with the current attributes.</summary>
    private void WriteCell(int cp, int width)
    {
        var row = _lines[_y];
        row.Touch();
        var cells = row.Cells;
        FixWideAt(cells, _x);
        if (width == 2) FixWideAt(cells, _x + 1);

        // Field by field: a Cell built on the stack and copied is a store-forwarding stall on every character.
        ref var c = ref cells[_x];
        c.Rune = cp;
        c.Fg = _fg;
        c.Bg = _bg;
        c.Flags = _attr | (width == 2 ? CellAttributes.WideLead : 0);
        if (width == 2)
        {
            ref var trail = ref cells[_x + 1];
            trail.Rune = 0;
            trail.Fg = _fg;
            trail.Bg = _bg;
            trail.Flags = _attr | CellAttributes.WideTrail;
        }
        TagLink(row, _x, width); // OSC 8
        ApplyUnderlineColor(row, _x, width);
    }

    /// <summary>Code points that join the previous cell in some contexts even though they have a width of their own.</summary>
    private const byte JoinCandidate = CharWidth.EmojiModifier | CharWidth.RegionalIndicator | CharWidth.ExtendedPictographic;

    /// <summary>
    /// Attaches <paramref name="cp"/> to the cluster in the cell before the cursor, if it continues it: combining marks,
    /// variation selectors, ZWJ and other zero-width code points always; a skin-tone modifier after an emoji modifier
    /// base; the second regional indicator of a flag; a pictograph after ZWJ. VS16 widens a narrow emoji to 2 cells.
    /// A simplified UAX #29 that covers what terminals see in practice; Mc (spacing marks) stay separate cells, as in wcwidth.
    /// </summary>
    private bool TryJoinCluster(int cp, byte props)
    {
        int px = _pendingWrap ? _x : _x - 1;
        var row = _lines[_y];
        var cells = row.Cells;
        if ((uint)px >= (uint)cells.Length) return false;
        if ((cells[px].Flags & CellAttributes.WideTrail) != 0)
        {
            if (px == 0) return false;
            px--;
        }
        ref var c = ref cells[px];
        if (c.Rune == 0) return false;

        if ((props & CharWidth.WidthMask) != 0 && !ContinuesCluster(row, px, props)) return false;

        if (!row.AppendToCluster(px, cp, _clusterStrings ??= new ClusterStringPool())) return true; // cluster full: the code point is dropped
        bool emojiPresentation = cp == CharWidth.VariationSelector16 || (props & CharWidth.EmojiModifier) != 0;
        if (emojiPresentation && (c.Flags & CellAttributes.WideLead) == 0 && CharWidth.IsEmoji(c.Rune))
            WidenCell(row, px);
        return true;
    }

    /// <summary>Whether a code point with a width of its own (<paramref name="props"/>) continues the cluster in the cell at <paramref name="px"/>.</summary>
    private static bool ContinuesCluster(Row row, int px, byte props)
    {
        ref readonly var c = ref row.Cells[px];
        if ((props & CharWidth.RegionalIndicator) != 0)
            return CharWidth.IsRegionalIndicator(c.Rune) && (c.Flags & CellAttributes.Cluster) == 0;
        if ((props & CharWidth.EmojiModifier) != 0)
            return (CharWidth.Props(c.Rune) & CharWidth.EmojiModifierBase) != 0;
        return (c.Flags & CellAttributes.Cluster) != 0 && CharWidth.IsExtendedPictographic(c.Rune)
            && row.GetClusterString(px) is { } s && s.EndsWith('‍'); // UAX #29 GB11
    }

    /// <summary>Turns the narrow cell just printed at <paramref name="px"/> into a wide one (emoji presentation via VS16).</summary>
    private void WidenCell(Row row, int px)
    {
        if (_cols < 2) return;
        row.Touch();
        if (!_pendingWrap) WidenCellInPlace(row, px);
        else if (_autoWrap) MoveWidenedCellToNextRow(row, px);
    }

    /// <summary>The cursor is right after the cell: the wide character takes the next column as well.</summary>
    private void WidenCellInPlace(Row row, int px)
    {
        var cells = row.Cells;
        FixWideAt(cells, px + 1);
        ref var c = ref cells[px];
        c.Flags |= CellAttributes.WideLead;
        cells[px + 1] = new Cell { Fg = c.Fg, Bg = c.Bg, Flags = (c.Flags & CellAttributes.SgrMask) | CellAttributes.WideTrail };
        if (row.HasUnderlineColors) row.SetUnderlineColor(px + 1, 1, row.UnderlineColorAt(px));
        int next = px + 2;
        if (next >= _cols) { _x = _cols - 1; _pendingWrap = _autoWrap; }
        else _x = next;
    }

    /// <summary>The cell was printed in the last column: moves it to the next line, as if it had been wide all along.</summary>
    private void MoveWidenedCellToNextRow(Row row, int px)
    {
        var cells = row.Cells;
        Cell moved = cells[px];
        string? text = row.GetClusterString(px);
        uint movedUnderline = row.UnderlineColorAt(px);
        cells[px] = Cell.Blank(moved.Bg);
        WrapToNextLine();
        var next = _lines[_y];
        next.Touch();
        FixWideAt(next.Cells, 0);
        FixWideAt(next.Cells, 1);
        next.Cells[0] = moved;
        next.Cells[0].Flags |= CellAttributes.WideLead;
        next.Cells[1] = new Cell { Fg = moved.Fg, Bg = moved.Bg, Flags = (moved.Flags & CellAttributes.SgrMask) | CellAttributes.WideTrail };
        if (text is not null) next.SetCluster(0, text);
        else next.Cells[0].Flags &= ~CellAttributes.Cluster;
        if ((moved.Flags & CellAttributes.Underline) != 0 && (movedUnderline != 0 || next.HasUnderlineColors))
            next.SetUnderlineColor(0, 2, movedUnderline);
        if (_cols > 2) _x = 2;
        else { _x = _cols - 1; _pendingWrap = true; }
    }

    private void WrapToNextLine()
    {
        _pendingWrap = false;
        _lines[_y].Wrapped = true;
        _x = 0;
        Index();
    }

    /// <summary>Overwriting either half of a wide character blanks the other half.</summary>
    private static void FixWideAt(Cell[] cells, int x)
    {
        if ((uint)x >= (uint)cells.Length) return;
        var flags = cells[x].Flags;
        if ((flags & CellAttributes.WideTrail) != 0 && x > 0) cells[x - 1] = Cell.Blank(cells[x - 1].Bg);
        else if ((flags & CellAttributes.WideLead) != 0 && x + 1 < cells.Length) cells[x + 1] = Cell.Blank(cells[x + 1].Bg);
    }

    // ---- Printing runs (the single-character path is Print(int)) ---------------------------------

    private const CellAttributes WideHalf = CellAttributes.WideLead | CellAttributes.WideTrail;

    /// <summary>A Cell as two 64-bit stores: <c>Rune | Fg &lt;&lt; 32</c>, then <c>Bg | Flags &lt;&lt; 32</c> (padding zero). Little-endian, as on every supported target.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreCell(ref Cell cell, ulong runeAndFg, ulong bgAndFlags)
    {
        ref byte p = ref Unsafe.As<Cell, byte>(ref cell);
        Unsafe.WriteUnaligned(ref p, runeAndFg);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 8), bgAndFlags);
    }

    private ulong FgBits => (ulong)_fg << 32;

    private ulong BgAndFlags => _bg | (ulong)(ushort)_attr << 32;

    /// <summary>Stores printable ASCII over cells (one per byte, already known to fit), with the current attributes.</summary>
    private void StoreAsciiCells(Span<Cell> dst, ReadOnlySpan<byte> run)
    {
        ulong fg = FgBits;
        ulong rest = BgAndFlags;
        ref Cell d = ref MemoryMarshal.GetReference(dst);
        ref byte src = ref MemoryMarshal.GetReference(run);
        for (int i = 0; i < dst.Length; i++)
            StoreCell(ref Unsafe.Add(ref d, i), Unsafe.Add(ref src, i) | fg, rest);
    }

    /// <summary>Printing that only the per-byte path handles: replaced character sets, insert mode, no auto-wrap, links, underlines.</summary>
    private bool PrintRunUnavailable =>
        (_useG1 ? _g1Graphics : _g0Graphics) || _insertMode || !_autoWrap || _linkId != 0 || (_attr & CellAttributes.Underline) != 0;

    /// <summary>
    /// Prints text that starts with a non-ASCII byte: decodes it here and writes directly into the row. ASCII stretches
    /// go to <see cref="PrintAscii"/> (the same writer as a run on its own, wide-character fix-ups included), and a code
    /// point goes to <see cref="Print(int)"/> only when it needs more than a plain cell (a zero-width or cluster-joining
    /// code point, a wide character at the margin) or the target cell is half of a wide character or the row has links.
    /// Stops at a control byte and at invalid or incomplete UTF-8. Returns the bytes printed.
    /// </summary>
    private int PrintUtf8(ReadOnlySpan<byte> text)
    {
        if (PrintRunUnavailable) return 0;
        ulong fg = FgBits;
        ulong rest = BgAndFlags;
        var row = RowForRun(out var cells);
        int i = 0;
        while (i < text.Length)
        {
            byte b = text[i];
            if (b < 0x80)
            {
                int n = ScanPrintableAscii(text[i..]);
                if (n == 0) break;
                PrintAscii(text.Slice(i, n));
                i += n;
                row = RowForRun(out cells);
                continue;
            }
            int len = VtUtf8.Decode(text[i..], out int cp);
            if (len == 0) break;
            i += len;
            int width = 1;
            if (cp >= 0x300 && !IsSimpleWidth(cp, out width))
            {
                Print(cp);
                row = RowForRun(out cells);
                continue;
            }
            WrapPendingForRun(ref row, ref cells);
            if (width == 2 && _x == _cols - 1)
            {
                Print(cp);
                row = RowForRun(out cells);
                continue;
            }
            PutRunCell(row, cells, cp, width, fg, rest);
            AdvanceAfterPrint(cp, width);
        }
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WrapPendingForRun(ref Row row, ref Cell[] cells)
    {
        if (!_pendingWrap) return;
        WrapToNextLine();
        row = RowForRun(out cells);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutRunCell(Row row, Cell[] cells, int cp, int width, ulong fg, ulong rest)
    {
        if (CanStoreDirectly(row, cells, width)) StoreRunCell(cells, cp, width, fg, rest);
        else WriteCell(cp, width);
    }

    /// <summary>The length of the printable ASCII at the start of <paramref name="text"/>.</summary>
    private static int ScanPrintableAscii(ReadOnlySpan<byte> text)
    {
        int n = text.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
        return n < 0 ? text.Length : n;
    }

    private Row RowForRun(out Cell[] cells)
    {
        var row = _lines[_y];
        row.Touch();
        cells = row.Cells;
        return row;
    }

    /// <summary>The width of a code point at or above U+0300, or false for one that joins the previous cell or takes no cell.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSimpleWidth(int cp, out int width)
    {
        byte props = CharWidth.Props(cp);
        width = props & CharWidth.WidthMask;
        return width != 0 && (props & JoinCandidate) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CanStoreDirectly(Row row, Cell[] cells, int width)
    {
        var flags = cells[_x].Flags;
        if (width == 2) flags |= cells[_x + 1].Flags;
        return (flags & WideHalf) == 0 && !row.HasLinks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreRunCell(Cell[] cells, int cp, int width, ulong fg, ulong rest)
    {
        if (width == 1)
        {
            StoreCell(ref cells[_x], (uint)cp | fg, rest);
            return;
        }
        ulong lead = rest | (ulong)(ushort)CellAttributes.WideLead << 32;
        StoreCell(ref cells[_x], (uint)cp | fg, lead);
        StoreCell(ref cells[_x + 1], fg, rest | (ulong)(ushort)CellAttributes.WideTrail << 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceAfterPrint(int cp, int width)
    {
        _lastPrinted = cp;
        int next = _x + width;
        if (next >= _cols)
        {
            _x = _cols - 1;
            _pendingWrap = true; // the run paths only run with auto-wrap on
        }
        else _x = next;
    }

    /// <summary>Print hook: records the active link on the cells just written (and un-links overwritten cells).</summary>
    private void TagLink(Row row, int x, int width)
    {
        if (_linkId != 0 || row.HasLinks) row.SetLink(x, width, _linkId);
    }

    // ---- Parser callbacks: C0 controls --------------------------------------------------------------

    private void Execute(byte control)
    {
        switch (control)
        {
            case 0x07: BellCount++; Bell?.Invoke(); break;
            case 0x08: // BS
                if (_pendingWrap) _pendingWrap = false;
                else if (_x > 0) _x--;
                break;
            case 0x09: TabForward(1); break;
            case 0x0A: case 0x0B: case 0x0C: // LF VT FF
                if (_newLineMode) _x = 0;
                _pendingWrap = false;
                Index();
                break;
            case 0x0D: _x = 0; _pendingWrap = false; break;
            case 0x0E: _useG1 = true; break;  // SO
            case 0x0F: _useG1 = false; break; // SI
        }
    }

    // ---- Parser callbacks: ESC ----------------------------------------------------------------------

    private void EscDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        if (intermediates.IsEmpty)
        {
            switch ((char)final)
            {
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': _pendingWrap = false; Index(); break;
                case 'E': _x = 0; _pendingWrap = false; Index(); break;
                case 'H': _tabs[_x] = true; break;
                case 'M': ReverseIndex(); break;
                case 'c': FullReset(); break;
                case '=': _input.KeypadApplication = true; break;
                case '>': _input.KeypadApplication = false; break;
                case '\\': break; // ST ending a string the parser already handled
                default: Unhandled?.Esc(intermediates, final); break;
            }
            return;
        }

        if (intermediates.Length == 1)
        {
            switch ((char)intermediates[0])
            {
                case '#' when final == '8': ScreenAlignment(); return;
                case '(': _g0Graphics = final == '0'; return;
                case ')': _g1Graphics = final == '0'; return;
            }
        }
        Unhandled?.Esc(intermediates, final);
    }

    // ---- Parser callbacks: CSI ----------------------------------------------------------------------

#pragma warning disable S3776 // one flat dispatch on the hottest path; splitting it adds calls per sequence
    private void CsiDispatch(CsiParams p, ReadOnlySpan<byte> inter, byte priv, byte final)
#pragma warning restore S3776
    {
        if (priv == 0 && inter.IsEmpty)
        {
            int n = p.Get(0, 1);
            switch ((char)final)
            {
                case '@': InsertChars(n); break;
                case 'A': CursorUp(n); break;
                case 'B': case 'e': CursorDown(n); break;
                case 'C': case 'a': CursorForward(n); break;
                case 'D': CursorBack(n); break;
                case 'E': CursorDown(n); _x = 0; break;
                case 'F': CursorUp(n); _x = 0; break;
                case 'G': case '`': SetColumn(n - 1); break;
                case 'H': case 'f': CursorPosition(p.Get(0, 1), p.Get(1, 1)); break;
                case 'I': TabForward(n); break;
                case 'J': EraseDisplay(p.Raw(0)); break;
                case 'K': EraseLine(p.Raw(0)); break;
                case 'L': InsertLines(n); break;
                case 'M': DeleteLines(n); break;
                case 'P': DeleteChars(n); break;
                case 'S': ScrollUpRegion(_top, _bottom, n, allowScrollback: _top == 0); break;
                case 'T': ScrollDownRegion(_top, _bottom, n); break;
                case 'X': EraseChars(n); break;
                case 'Z': TabBack(n); break;
                case 'b': Repeat(n); break;
                case 'c':
                    if (p.Raw(0) == 0) Respond("\e[?62;4;22c"u8); // 4 = sixel
                    break;
                case 'd': SetRow(n - 1); break;
                case 'g': TabClear(p.Raw(0)); break;
                case 'h':
                    for (int i = 0; i < p.Count; i++) SetAnsiMode(p[i], true);
                    break;
                case 'l':
                    for (int i = 0; i < p.Count; i++) SetAnsiMode(p[i], false);
                    break;
                case 'm': Sgr(p); break;
                case 'n': DeviceStatus(p.Raw(0)); break;
                case 'r': SetMargins(p.Get(0, 1), p.Get(1, _rows)); break;
                case 't': WindowOps(p); break; // title stack, sizes
                case 's': SaveCursor(); break;
                case 'u': RestoreCursor(); break;
                default: Unhandled?.Csi(p, inter, priv, final); break;
            }
            return;
        }

        if (priv == '?')
        {
            if (inter.IsEmpty && final == 'h') { for (int i = 0; i < p.Count; i++) SetDecMode(p[i], true); }
            else if (inter.IsEmpty && final == 'l') { for (int i = 0; i < p.Count; i++) SetDecMode(p[i], false); }
            else if (inter.Length == 1 && inter[0] == '$' && final == 'p') ReportDecMode(p.Raw(0));
            else if (inter.IsEmpty && final == 'u') Respond(Encoding.ASCII.GetBytes($"\e[?{(int)KeyboardFlags}u"));
            else if (inter.IsEmpty && final == 'n') DecDeviceStatus(p.Raw(0));
            else Unhandled?.Csi(p, inter, priv, final);
            return;
        }

        // Kitty keyboard protocol: push / pop / set the progressive enhancement flags.
        if (final == 'u' && inter.IsEmpty)
        {
            switch ((char)priv)
            {
                case '>': KeyboardStack.Push(p.Raw(0)); return;
                case '<': KeyboardStack.Pop(p.Get(0, 1)); return;
                case '=': KeyboardStack.Set(p.Raw(0), p.Get(1, 1)); return;
            }
        }

        if (priv == '>')
        {
            if (final == 'c') Respond("\e[>1;10;0c"u8);
            else if (final == 'q') Respond("\eP>|erikterm 0.1\e\\"u8); // XTVERSION
            else Unhandled?.Csi(p, inter, priv, final);
            return;
        }

        if (priv == '=' && inter.IsEmpty && final == 'c')
        {
            if (p.Raw(0) == 0) Respond("\eP!|00000000\e\\"u8); // DA3: unit id
            return;
        }

        if (priv == 0 && inter.Length == 1)
        {
            if (inter[0] == ' ' && final == 'q') { SetCursorStyle(p.Raw(0)); return; }
            if (inter[0] == '$' && final == 'p') { ReportAnsiMode(p.Raw(0)); return; } // ANSI DECRQM
            if (inter[0] == '!' && final == 'p') { SoftReset(); return; }
        }
        Unhandled?.Csi(p, inter, priv, final);
    }

    // ---- Parser callbacks: OSC ----------------------------------------------------------------------

    /// <summary>Splits an OSC payload into its numeric code and argument; false when it doesn't start with a number.</summary>
    private static bool TrySplitOsc(ReadOnlySpan<byte> data, out int code, out ReadOnlySpan<byte> arg)
    {
        int semi = data.IndexOf((byte)';');
        var codeText = semi < 0 ? data : data[..semi]; // OSC 104 / 110 / 111 / 112 come without arguments
        arg = semi < 0 ? default : data[(semi + 1)..];
        code = 0;
        return !codeText.IsEmpty && int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out code);
    }

    private void OscDispatch(ReadOnlySpan<byte> data)
    {
        if (!TrySplitOsc(data, out int code, out var arg))
        {
            Unhandled?.Osc(data);
            return;
        }

        switch (code)
        {
            case 0:
            case 2:
                Title = Encoding.UTF8.GetString(arg);
                break;
            case 7:
                WorkingDirectoryUri = Encoding.UTF8.GetString(arg);
                break;
            // Colors (ColorReports).
            case 4: Dirty |= _colors.Osc4(arg, _parser.OscEndedWithBel); break;
            case 104: _colors.Osc104(arg); Dirty = true; break;
            case 10 or 11 or 12: Dirty |= _colors.OscDynamicColor(code, arg, _parser.OscEndedWithBel); break;
            case 110: OscResetDynamic(DynamicColor.Foreground); break;
            case 111: OscResetDynamic(DynamicColor.Background); break;
            case 112: OscResetDynamic(DynamicColor.Cursor); break;
            case 1337:
                if (!_cellImages.Osc1337(arg)) _userVars.Set(arg); // iTerm2 images, else SetUserVar
                break;
            // Shell integration.
            case 8: _linkId = _hyperlinks.Open(arg, _linkId); break;
            case 52: Osc52(arg); break;
            case 133: Osc133(arg); break;
            case 6 when _tabColor.TryApply(arg): break; // iTerm2's tab color; xterm's OSC 6 (color mode) falls through to unhandled
            // Agent signals (ProgramSignals).
            case 9: _signals.Osc9(arg); break;
            case 99: _signals.Osc99(arg); break;
            case 777: _signals.Osc777(arg); break;
            case 7501: _signals.Osc7501(arg); break;
            default: Unhandled?.Osc(data); break;
        }
    }

    // ---- Cursor movement ----------------------------------------------------------------------

    private void CursorUp(int n)
    {
        int limit = _y >= _top ? _top : 0;
        _y = Math.Max(limit, _y - n);
        _pendingWrap = false;
    }

    private void CursorDown(int n)
    {
        int limit = _y <= _bottom ? _bottom : _rows - 1;
        _y = Math.Min(limit, _y + n);
        _pendingWrap = false;
    }

    private void CursorForward(int n)
    {
        _x = Math.Min(_cols - 1, _x + n);
        _pendingWrap = false;
    }

    private void CursorBack(int n)
    {
        _x = Math.Max(0, _x - n);
        _pendingWrap = false;
    }

    private void SetColumn(int x)
    {
        _x = Math.Clamp(x, 0, _cols - 1);
        _pendingWrap = false;
    }

    private void SetRow(int y)
    {
        if (_originMode) _y = Math.Clamp(_top + y, _top, _bottom);
        else _y = Math.Clamp(y, 0, _rows - 1);
        _pendingWrap = false;
    }

    private void CursorPosition(int row, int col)
    {
        SetRow(row - 1);
        SetColumn(col - 1);
    }

    private void Index()
    {
        if (_y == _bottom) ScrollUpRegion(_top, _bottom, 1, allowScrollback: _top == 0);
        else if (_y < _rows - 1) _y++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (_y == _top) ScrollDownRegion(_top, _bottom, 1);
        else if (_y > 0) _y--;
    }

    private void TabForward(int n)
    {
        while (n-- > 0 && _x < _cols - 1)
        {
            do _x++; while (_x < _cols - 1 && !_tabs[_x]);
        }
        _pendingWrap = false;
    }

    private void TabBack(int n)
    {
        while (n-- > 0 && _x > 0)
        {
            do _x--; while (_x > 0 && !_tabs[_x]);
        }
        _pendingWrap = false;
    }

    private void TabClear(int mode)
    {
        if (mode == 0) _tabs[_x] = false;
        else if (mode == 3) Array.Clear(_tabs);
    }

    private void SetMargins(int top, int bottom)
    {
        top = Math.Clamp(top, 1, _rows) - 1;
        bottom = Math.Clamp(bottom, 1, _rows) - 1;
        if (top >= bottom) return;
        _top = top;
        _bottom = bottom;
        CursorPosition(1, 1);
    }

    private ref SavedCursor Saved => ref _altActive ? ref _savedAlt : ref _savedMain;

    private void SaveCursor()
    {
        ref var s = ref Saved;
        s = new SavedCursor
        {
            X = _x, Y = _y, Fg = _fg, Bg = _bg, UnderlineColor = _ulColor, Attr = _attr, OriginMode = _originMode, PendingWrap = _pendingWrap,
            G0Graphics = _g0Graphics, G1Graphics = _g1Graphics, UseG1 = _useG1, Valid = true,
        };
    }

    private void RestoreCursor()
    {
        ref var s = ref Saved;
        if (!s.Valid)
        {
            _x = 0; _y = 0; _fg = _bg = 0; _ulColor = 0; _attr = 0; _originMode = false; _pendingWrap = false;
            return;
        }
        _x = Math.Min(s.X, _cols - 1);
        _y = Math.Min(s.Y, _rows - 1);
        _fg = s.Fg; _bg = s.Bg; _ulColor = s.UnderlineColor; _attr = s.Attr;
        _originMode = s.OriginMode;
        _pendingWrap = s.PendingWrap;
        _g0Graphics = s.G0Graphics; _g1Graphics = s.G1Graphics; _useG1 = s.UseG1;
    }

    // ---- Scrolling ----------------------------------------------------------------------------

    private void ScrollUpRegion(int top, int bottom, int n, bool allowScrollback)
    {
        n = Math.Min(n, bottom - top + 1);
        Volatile.Write(ref _linesScrolled, _linesScrolled + n);
        bool toScrollback = allowScrollback && !_altActive && _scrollback.Capacity > 0;
        if (_images.HasPlacements) _placements.ScrollUp(_altActive, ScreenTopLine, _rows, top, bottom, n, toScrollback);
        for (int i = 0; i < n; i++)
        {
            Row old = _lines[top];
            Row fresh = old;
            if (toScrollback)
            {
                Row? evicted = _scrollback.Push(old);
                fresh = evicted is not null && evicted.Cells.Length == _cols && !evicted.IsTrimmed ? evicted : ScreenRowFrom(evicted, _cols);
                if (ViewOffset > 0) ViewOffset = Math.Min(ViewOffset + 1, _scrollback.Count);
            }
            Array.Copy(_lines, top + 1, _lines, top, bottom - top);
            fresh.Clear(_bg);
            _lines[bottom] = fresh;
        }
    }

    private void ScrollDownRegion(int top, int bottom, int n)
    {
        n = Math.Min(n, bottom - top + 1);
        if (_images.HasPlacements) _placements.ScrollDown(_altActive, ScreenTopLine, top, bottom, n);
        for (int i = 0; i < n; i++)
        {
            Row recycled = _lines[bottom];
            Array.Copy(_lines, top, _lines, top + 1, bottom - top);
            recycled.Clear(_bg);
            _lines[top] = recycled;
        }
    }

    private void InsertLines(int n)
    {
        if (_y < _top || _y > _bottom) return;
        ScrollDownRegion(_y, _bottom, n);
        _x = 0;
        _pendingWrap = false;
    }

    private void DeleteLines(int n)
    {
        if (_y < _top || _y > _bottom) return;
        ScrollUpRegion(_y, _bottom, n, allowScrollback: false);
        _x = 0;
        _pendingWrap = false;
    }

    // ---- Editing ------------------------------------------------------------------------------

    private void InsertChars(int n)
    {
        _lines[_y].Touch();
        var cells = _lines[_y].Cells;
        n = Math.Min(n, _cols - _x);
        Array.Copy(cells, _x, cells, _x + n, _cols - _x - n);
        _lines[_y].CopyUnderlineColors(_x, _x + n, _cols - _x - n);
        cells.AsSpan(_x, n).Fill(Cell.Blank(_bg));
        _lines[_y].ClearLinksFrom(_x);
        if (_lines[_y].HasClusters) _lines[_y].ShiftClusters(_x, n);
        _pendingWrap = false;
    }

    private void DeleteChars(int n)
    {
        _lines[_y].Touch();
        var cells = _lines[_y].Cells;
        n = Math.Min(n, _cols - _x);
        Array.Copy(cells, _x + n, cells, _x, _cols - _x - n);
        _lines[_y].CopyUnderlineColors(_x + n, _x, _cols - _x - n);
        if (_lines[_y].HasClusters) _lines[_y].ShiftClusters(_x + n, -n);
        cells.AsSpan(_cols - n, n).Fill(Cell.Blank(_bg));
        _lines[_y].ClearLinksFrom(_x);
        _pendingWrap = false;
    }

    private void EraseChars(int n)
    {
        n = Math.Min(n, _cols - _x);
        _lines[_y].Touch();
        _lines[_y].Cells.AsSpan(_x, n).Fill(Cell.Blank(_bg));
        _lines[_y].SetLink(_x, n, 0);
        _pendingWrap = false;
    }

    private void EraseLine(int mode)
    {
        var row = _lines[_y];
        row.Touch();
        switch (mode)
        {
            case 0: row.Cells.AsSpan(_x).Fill(Cell.Blank(_bg)); row.Wrapped = false; row.ClearLinksFrom(_x); break;
            case 1: row.Cells.AsSpan(0, _x + 1).Fill(Cell.Blank(_bg)); row.SetLink(0, _x + 1, 0); break;
            case 2: row.Clear(_bg); break;
        }
        _pendingWrap = false;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int y = _y + 1; y < _rows; y++) _lines[y].Clear(_bg);
                break;
            case 1:
                for (int y = 0; y < _y; y++) _lines[y].Clear(_bg);
                EraseLine(1);
                break;
            case 2:
                for (int y = 0; y < _rows; y++) _lines[y].Clear(_bg);
                _placements.Erase(_altActive, ScreenTopLine, scrollback: false);
                break;
            case 3:
                _scrollback.ClearKeepingPinnedRows();
                ViewOffset = 0;
                _placements.Erase(_altActive, ScreenTopLine, scrollback: true);
                break;
        }
        _pendingWrap = false;
    }

    private void Repeat(int n)
    {
        if (_lastPrinted < 0x20) return;
        n = Math.Min(n, _cols * _rows);
        for (int i = 0; i < n; i++) Print(_lastPrinted);
    }

    private void ScreenAlignment()
    {
        _top = 0;
        _bottom = _rows - 1;
        for (int y = 0; y < _rows; y++)
        {
            _lines[y].Touch();
            _lines[y].Cells.AsSpan().Fill(new Cell { Rune = 'E' });
            _lines[y].Wrapped = false;
        }
        _x = _y = 0;
        _pendingWrap = false;
    }

    // ---- SGR ----------------------------------------------------------------------------------

#pragma warning disable S3776 // one flat switch over SGR codes on the hottest path; splitting it adds calls per parameter
    private void Sgr(CsiParams p)
#pragma warning restore S3776
    {
        if (p.Count == 0) { _fg = _bg = _ulColor = 0; _attr = 0; return; }

        for (int i = 0; i < p.Count; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: _fg = _bg = _ulColor = 0; _attr = 0; break;
                case 1: _attr |= CellAttributes.Bold; break;
                case 2: _attr |= CellAttributes.Dim; break;
                case 3: _attr |= CellAttributes.Italic; break;
                case 4:
                    // 4:0 none, 4:1 single, 4:2 double, 4:3 curly, 4:4 dotted, 4:5 dashed; unknown styles are ignored.
                    if (i + 1 < p.Count && p.IsSub(i + 1))
                    {
                        int style = p[i + 1];
                        if (style <= (int)UnderlineStyle.Dashed) _attr = CellStyle.WithUnderline(_attr, (UnderlineStyle)style);
                    }
                    else _attr = CellStyle.WithUnderline(_attr, UnderlineStyle.Single);
                    break;
                case 5: case 6: _attr |= CellAttributes.Blink; break;
                case 7: _attr |= CellAttributes.Inverse; break;
                case 8: _attr |= CellAttributes.Hidden; break;
                case 9: _attr |= CellAttributes.Strike; break;
                case 21: _attr = CellStyle.WithUnderline(_attr, UnderlineStyle.Double); break; // ECMA-48 (kitty, VTE, wezterm)
                case 22: _attr &= ~(CellAttributes.Bold | CellAttributes.Dim); break;
                case 23: _attr &= ~CellAttributes.Italic; break;
                case 24: _attr = CellStyle.WithUnderline(_attr, UnderlineStyle.None); break;
                case 25: _attr &= ~CellAttributes.Blink; break;
                case 27: _attr &= ~CellAttributes.Inverse; break;
                case 28: _attr &= ~CellAttributes.Hidden; break;
                case 29: _attr &= ~CellAttributes.Strike; break;
                case >= 30 and <= 37: _fg = TermColor.Indexed(v - 30); break;
                case 38:
                    if (TryParseExtendedColor(p, ref i, out uint fg)) _fg = fg;
                    break;
                case 39: _fg = TermColor.Default; break;
                case >= 40 and <= 47: _bg = TermColor.Indexed(v - 40); break;
                case 48:
                    if (TryParseExtendedColor(p, ref i, out uint bg)) _bg = bg;
                    break;
                case 49: _bg = TermColor.Default; break;
                case 53: _attr |= CellAttributes.Overline; break;
                case 55: _attr &= ~CellAttributes.Overline; break;
                case 58:
                    if (TryParseExtendedColor(p, ref i, out uint ul)) _ulColor = ul;
                    break;
                case 59: _ulColor = TermColor.Default; break;
                case >= 90 and <= 97: _fg = TermColor.Indexed(v - 90 + 8); break;
                case >= 100 and <= 107: _bg = TermColor.Indexed(v - 100 + 8); break;
            }
#pragma warning disable S127 // SGR parameters have variable length, so the index is what walks them
            while (i + 1 < p.Count && p.IsSub(i + 1)) i++; // skip unconsumed sub-parameters
#pragma warning restore S127
        }
    }

    /// <summary>Parses 38;5;n / 38;2;r;g;b and the colon forms 38:5:n / 38:2:[cs]:r:g:b.</summary>
    private static bool TryParseExtendedColor(CsiParams p, ref int i, out uint color)
    {
        color = 0;
        if (i + 1 >= p.Count) return false;
        int kind = p[i + 1];

        if (p.IsSub(i + 1)) return TryParseColonColor(p, kind, ref i, ref color);

        if (kind == 5 && i + 2 < p.Count) { color = TermColor.Indexed(p[i + 2]); i += 2; return true; }
        if (kind == 2 && i + 4 < p.Count) { color = TermColor.Rgb(p[i + 2], p[i + 3], p[i + 4]); i += 4; return true; }
        i++;
        return false;
    }

    /// <summary>The colon form: consumes every sub-parameter whether or not it describes a color.</summary>
    private static bool TryParseColonColor(CsiParams p, int kind, ref int i, ref uint color)
    {
        int first = i + 1, last = first;
        while (last + 1 < p.Count && p.IsSub(last + 1)) last++;
        int values = last - first;
        i = last;
        if (kind == 5 && values >= 1) { color = TermColor.Indexed(p[first + 1]); return true; }
        if (kind != 2 || values < 3) return false;
        int o = values >= 4 ? first + 2 : first + 1; // optional color-space id
        color = TermColor.Rgb(p[o], p[o + 1], p[o + 2]);
        return true;
    }

    // ---- Modes --------------------------------------------------------------------------------

    private void SetAnsiMode(int mode, bool on)
    {
        switch (mode)
        {
            case 4: _insertMode = on; break;
            case 20: _newLineMode = on; break;
            default: Unhandled?.Mode(dec: false, mode, on); break;
        }
    }

    private void SetDecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1 or 1000 or 1002 or 1003 or 1004 or 1006 or 1007 or 2004: _input.Set(mode, on); break; // InputModes
            case 6: _originMode = on; CursorPosition(1, 1); break;
            case 7:
                _autoWrap = on;
                if (!on) _pendingWrap = false;
                break;
            case 12: break; // AT&T 610 blink: ignored, so xterm's cnorm (?12l) can't override cursor_blink_rate (vim sends it)
            case 25: CursorVisible = on; break;
            case 47 or 1047 or 1048 or 1049: SetScreenMode(mode, on); break;
            case 2026: SynchronizedOutput = on; break;
            case 2027: break; // grapheme clusters: always on (reported as permanently set)
            case 2031: _colors.SetColorSchemeUpdates(on); break;
            case 2048: SetInBandResize(on); break;
            default: Unhandled?.Mode(dec: true, mode, on); break;
        }
    }

    /// <summary>DECSET / DECRST 47, 1047, 1048 and 1049: the alternate screen and saved cursor.</summary>
    private void SetScreenMode(int mode, bool on)
    {
        switch (mode)
        {
            case 47: SwitchScreen(on, clear: false); break;
            case 1047: SwitchScreen(on, clear: !on); break;
            case 1048 when on: SaveCursor(); break;
            case 1048: RestoreCursor(); break;
            case 1049 when on && !_altActive:
                SaveCursor();
                SwitchScreen(true, clear: true);
                break;
            case 1049 when !on && _altActive:
                SwitchScreen(false, clear: false);
                RestoreCursor();
                break;
        }
    }

    private void ReportDecMode(int mode)
    {
        // 1 = set, 2 = reset, 0 = not recognised.
        int state = mode switch
        {
            1 or 1000 or 1002 or 1003 or 1004 or 1006 or 1007 or 2004 => ModeState(_input.IsSet(mode)),
            6 => ModeState(_originMode),
            7 => ModeState(_autoWrap),
            12 => ModeState(CursorBlink),
            25 => ModeState(CursorVisible),
            1049 or 1047 or 47 => ModeState(_altActive),
            2026 => ModeState(SynchronizedOutput),
            2027 => 3, // permanently set
            2031 => ModeState(ColorSchemeUpdates),
            2048 => ModeState(InBandResize),
            _ => 0,
        };
        Respond(Encoding.ASCII.GetBytes($"\e[?{mode};{state}$y"));
    }

    private static int ModeState(bool on) => on ? 1 : 2;

    private void SwitchScreen(bool alt, bool clear)
    {
        if (alt == _altActive)
        {
            if (alt && clear) ClearBuffer(_alt);
            return;
        }
        if (alt && clear) ClearBuffer(_alt);
        if (!alt && clear) ClearBuffer(_alt);
        if (!alt) _images.ClearAlt(); // leaving the alternate screen drops its images
        _altActive = alt;
        _lines = alt ? _alt : _main;
        ViewOffset = 0;
        _pendingWrap = false;
    }

    private void ClearBuffer(Row[] buffer)
    {
        foreach (var row in buffer) row.Clear(_bg);
    }

    private void SetCursorStyle(int style)
    {
        CursorShape = style switch
        {
            0 => DefaultCursorShape,
            3 or 4 => CursorShape.Underline,
            5 or 6 => CursorShape.Bar,
            _ => CursorShape.Block,
        };
        _cursorBlink = style switch { 0 => null, 1 or 3 or 5 => true, _ => false };
    }

    private void DeviceStatus(int code)
    {
        if (code == 5) Respond("\e[0n"u8);
        else if (code == 6)
        {
            int row = _originMode ? _y - _top + 1 : _y + 1;
            Respond(Encoding.ASCII.GetBytes($"\e[{row};{_x + 1}R"));
        }
        else Unhandled?.Sequence($"CSI {code}n");
    }

    private void SoftReset()
    {
        CursorVisible = true;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        _input.SoftReset();
        _top = 0;
        _bottom = _rows - 1;
        _fg = _bg = 0;
        _ulColor = 0;
        _attr = 0;
        _g0Graphics = _g1Graphics = _useG1 = false;
        _savedMain = _savedAlt = default;
        _pendingWrap = false;
    }

    private void FullReset()
    {
        SoftReset();
        SwitchScreen(false, clear: false);
        ClearBuffer(_main);
        ClearBuffer(_alt);
        _scrollback.Clear();
        ViewOffset = 0;
        _x = _y = 0;
        _tabs = NewTabs(_cols);
        _newLineMode = false;
        SynchronizedOutput = false;
        _input.Reset();
        CursorShape = DefaultCursorShape;
        _cursorBlink = null;
        Title = "erikterm";
        _titleStack.Clear();
        _colors.Reset(); // OSC 4 / 10 / 11 / 12 colors, DECSET 2031
        InBandResize = false;
        _linkId = 0;
        _signals.Reset();
        _tabColor.Reset();
        _images.Clear();
        _kitty.CancelTransmission();
    }

    /// <summary>
    /// A new program starts in this terminal (Restart Pane): what the old one set goes — the alternate screen, modes,
    /// colors, title, keyboard protocol, user variables, agent status, progress, tab color — but its output stays on the
    /// screen and in the scrollback, and the new program starts on a line of its own. RIS without the clearing.
    /// </summary>
    public void ResetForNewProgram()
    {
        if (_altActive)
        {
            SwitchScreen(false, clear: true);
            RestoreCursor();
        }
        SoftReset();
        _newLineMode = false;
        SynchronizedOutput = false;
        _input.Reset();
        CursorShape = DefaultCursorShape;
        _cursorBlink = null;
        Title = "erikterm";
        _titleStack.Clear();
        _colors.Reset();
        InBandResize = false;
        _linkId = 0;
        _userVars.Clear();
        _shell.ResetForNewProgram(); // the new program's shell may not send command lines
        _signals.Reset();
        _tabColor.Reset();
        _kitty.CancelTransmission();
        ViewOffset = 0;
        if (_x > 0) Feed("\r\n"u8);
        Dirty = true;
        ContentVersion++;
    }

    // ---- Images: the grid side (protocols: KittyGraphics, CellImages; placements: PlacementTracker) ----
    // Two kinds of image on screen. Cell tiles (iTerm2, sixel): the image is written into the grid, each covered cell
    // flagged CellAttributes.Image with the tile it shows; they scroll and get evicted with the text (on resize their
    // rows are kept whole, truncated, never re-wrapped), and printing or erasing over them erases that part of the image.
    // Kitty placements: drawn over (z >= 0) or under (z < 0) the text of a cell rectangle anchored to an absolute line,
    // so they scroll into the scrollback and leave with it; text does not erase them (PlacementTracker). Virtual
    // placements (U=1) only show through U+10EEEE placeholder characters, which are ordinary text. The alternate screen
    // has its own placements; leaving it deletes them and the images sent there.

    /// <summary>Images and placements; renderers read it under the terminal lock.</summary>
    public ImageStore Images => _images;

    /// <summary>Size of a cell in pixels, used to size images (set by the app; also reported by CSI 14/16/18 t).</summary>
    public int CellPixelWidth { get; set; } = 10;
    public int CellPixelHeight { get; set; } = 20;

    /// <summary>
    /// Allows kitty <c>t=f</c> / <c>t=t</c> / <c>t=s</c> transmissions that read files (regular files only). Off by
    /// default: any program that can write to the terminal (a file you <c>cat</c>, a remote host) could otherwise make
    /// it read local files. Inline data (<c>t=d</c>) always works. The app sets it from allow_kitty_file_transmission.
    /// </summary>
    public bool AllowImageFiles { get; set; }

    private int CellW => Math.Max(1, CellPixelWidth);
    private int CellH => Math.Max(1, CellPixelHeight);

    /// <summary>
    /// Writes an image into the grid at the cursor as a box of <paramref name="cols"/>×<paramref name="rows"/> cells
    /// (clipped at the right edge). The picture fills the content rectangle (cell units) inside the box.
    /// </summary>
    void IImageGrid.PlaceCellImage(TerminalImage image, int cols, int rows, float cx, float cy, float cw, float ch, ImageCursor cursor)
    {
        cols = Math.Clamp(cols, 1, ImageStore.MaxTileColumns);
        rows = Math.Clamp(rows, 1, ImageStore.MaxTileRows);
        int slot = _images.AllocateSlot(new InlineImage(image, cols, rows, cx, cy, cw, ch));

        if (_pendingWrap) { _pendingWrap = false; if (cursor != ImageCursor.Stay) WrapToNextLine(); }
        int startX = _x, startY = _y;
        int drawCols = Math.Min(cols, _cols - startX);
        for (int r = 0; r < rows; r++)
        {
            if (r > 0 && !AdvanceImageRow(cursor)) break;
            WriteImageTiles(slot, r, startX, drawCols);
        }
        MoveCursorAfterImage(cursor, startX, startY, cols);
        PruneImages();
    }

    /// <summary>Moves down one row for the next row of tiles; false when a non-scrolling image reached the bottom.</summary>
    private bool AdvanceImageRow(ImageCursor cursor)
    {
        if (cursor != ImageCursor.Stay) { Index(); return true; }
        if (_y + 1 >= _rows) return false; // never scrolls: the rest is clipped
        _y++;
        return true;
    }

    private void WriteImageTiles(int slot, int imageRow, int startX, int drawCols)
    {
        var row = _lines[_y];
        var cells = row.Cells;
        FixWideAt(cells, startX);
        if (startX + drawCols < cells.Length) FixWideAt(cells, startX + drawCols);
        for (int c = 0; c < drawCols; c++)
            cells[startX + c] = new Cell { Fg = ImageStore.PackTile(slot, imageRow, c), Bg = _bg, Flags = CellAttributes.Image };
        if (row.HasLinks) row.SetLink(startX, drawCols, 0);
    }

    private void MoveCursorAfterImage(ImageCursor cursor, int startX, int startY, int cols)
    {
        switch (cursor)
        {
            case ImageCursor.Stay:
                _x = startX;
                _y = startY;
                break;
            case ImageCursor.AfterLastRow: // iTerm2 / kitty: right of the image on its last row
                _x = startX + cols;
                if (_x >= _cols) { _x = _cols - 1; _pendingWrap = _autoWrap; }
                break;
            case ImageCursor.NextLine: // sixel: the line below the image, at the column it started in
                Index();
                _x = startX;
                break;
        }
    }

    /// <summary>Drops placements whose lines have left the scrollback.</summary>
    private void PruneImages()
    {
        if (_images.HasPlacements) _images.PruneBefore(FirstLine);
    }

    bool IImageGrid.PendingWrap => _pendingWrap;
    int IImageGrid.CellWidth => CellW;
    int IImageGrid.CellHeight => CellH;

    void IImageGrid.WrapPendingCursor()
    {
        if (!_pendingWrap) return;
        _pendingWrap = false;
        WrapToNextLine();
    }

    void IImageGrid.MoveCursorPastPlacement(KittyPlacement p)
    {
        int startX = _x;
        for (int r = 1; r < p.Rows; r++) Index();
        _x = startX + p.Columns;
        if (_x >= _cols) { _x = _cols - 1; _pendingWrap = _autoWrap; }
    }

    void IImageGrid.PruneImages() => PruneImages();

    private void ApcDispatch(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data[0] != 'G') { Unhandled?.Apc(data); return; }
        _kitty.Dispatch(data[1..]);
    }

    /// <summary>DCS strings: sixel graphics (<c>DCS P1;P2;P3 q … ST</c>), XTGETTCAP (<c>DCS + q</c>) and DECRQSS (<c>DCS $ q</c>).</summary>
    private void DcsDispatch(ReadOnlySpan<byte> data)
    {
        // Parameters, intermediates, final byte, then the data string.
        int i = 0;
        while (i < data.Length && data[i] is >= 0x30 and <= 0x3F) i++;
        int interStart = i;
        while (i < data.Length && data[i] is >= 0x20 and <= 0x2F) i++;
        if (i >= data.Length) { Unhandled?.Dcs(data); return; }
        byte final = data[i];
        bool noIntermediates = i == interStart;
        var body = data[(i + 1)..];
        if (final == 'q' && noIntermediates) _cellImages.PlaceSixel(body);
        else if (final == 'q' && i == interStart + 1 && data[interStart] == '+') CapabilityQuery.Answer(body, TerminalName, _replies); // XTGETTCAP
        else if (final == 'q' && i == interStart + 1 && data[interStart] == '$') RequestStatusString(body); // DECRQSS
        else Unhandled?.Dcs(data);
    }

    /// <summary>CSI 14 t / 16 t / 18 t: text area in pixels, cell size in pixels, text area in cells.</summary>
    private void ReportWindowSize(int what)
    {
        switch (what)
        {
            case 14: Respond(System.Text.Encoding.ASCII.GetBytes($"\e[4;{_rows * CellH};{_cols * CellW}t")); break;
            case 16: Respond(System.Text.Encoding.ASCII.GetBytes($"\e[6;{CellH};{CellW}t")); break;
            case 18: Respond(System.Text.Encoding.ASCII.GetBytes($"\e[8;{_rows};{_cols}t")); break;
            default: Unhandled?.Sequence($"CSI {what}t"); break;
        }
    }

    // ---- Reports and queries ------------------------------------------------------------------------
    // XTGETTCAP and DECRQSS (DCS), ANSI DECRQM, DECXCPR, DA3, in-band resize (DECSET 2048) and the XTWINOPS title
    // stack. CSI 21 t (report the window title) is deliberately not answered: a title is program-controlled text, and
    // echoing it back as input has been an injection vector in other terminals.

    /// <summary>The TERM the program runs with; XTGETTCAP <c>TN</c> answers it.</summary>
    public string TerminalName { get; set; } = TermCapabilities.BaseEntry;

    /// <summary>DECSET 2031: the program wants <c>CSI ? 997 ; 1|2 n</c> whenever the background turns dark or light.</summary>
    public bool ColorSchemeUpdates => _colors.ColorSchemeUpdates;

    /// <summary>DECSET 2048: the program wants <c>CSI 48 ; rows ; cols ; height ; width t</c> on every resize.</summary>
    public bool InBandResize { get; private set; }

    /// <summary>
    /// Call after changing <see cref="Palette"/> from outside (a color scheme, <c>:theme</c> preview, config reload):
    /// with DECSET 2031 on, a background that flipped between dark and light is reported to the program.
    /// </summary>
    public void ColorsChanged()
    {
        Dirty = true;
        _colors.PaletteChanged();
    }

    private void OscResetDynamic(DynamicColor which)
    {
        _colors.OscResetDynamic(which);
        Dirty = true;
    }

    // ---- In-band resize ---------------------------------------------------------------------------

    private void SetInBandResize(bool on)
    {
        InBandResize = on;
        if (on) ReportInBandSize(); // the current size right away (as in the spec, and what Neovim waits for)
    }

    private void ReportInBandSize() =>
        Respond(Encoding.ASCII.GetBytes($"\e[48;{_rows};{_cols};{_rows * CellH};{_cols * CellW}t"));

    // ---- Small CSI replies -------------------------------------------------------------------------

    /// <summary>ANSI DECRQM (<c>CSI Ps $ p</c>): 1 set, 2 reset, 0 unknown, 4 permanently reset.</summary>
    private void ReportAnsiMode(int mode)
    {
        int state = mode switch
        {
            4 => _insertMode ? 1 : 2,
            20 => _newLineMode ? 1 : 2,
            2 or 12 => 4, // keyboard lock, send/receive: never
            _ => 0,
        };
        Respond(Encoding.ASCII.GetBytes($"\e[{mode};{state}$y"));
    }

    /// <summary>DEC-private DSR (<c>CSI ? Ps n</c>).</summary>
    private void DecDeviceStatus(int code)
    {
        switch (code)
        {
            case 6: // DECXCPR: cursor position with the page
                int row = _originMode ? _y - _top + 1 : _y + 1;
                Respond(Encoding.ASCII.GetBytes($"\e[?{row};{_x + 1};1R"));
                break;
            case 15: Respond("\e[?13n"u8); break; // no printer
            case 996: _colors.ReportColorScheme(); break;
            default: Unhandled?.Sequence($"CSI ?{code}n"); break;
        }
    }

    // ---- Title stack (XTWINOPS 22 / 23) -------------------------------------------------------------

    private readonly Stack<string> _titleStack = new();
    private const int MaxTitleStack = 10;

    private void WindowOps(CsiParams p)
    {
        int op = p.Raw(0), which = p.Raw(1);
        switch (op)
        {
            case 22 when which is 0 or 2:
                if (_titleStack.Count >= MaxTitleStack) break;
                _titleStack.Push(Title);
                break;
            case 22: break; // icon title only: erikterm has none
            case 23 when which is 0 or 2:
                if (_titleStack.TryPop(out var title)) Title = title;
                break;
            case 23: break;
            // CSI 21 t (report title) stays unanswered on purpose: see the class summary.
            default: ReportWindowSize(op); break;
        }
    }

    /// <summary>DECRQSS (<c>DCS $ q Pt ST</c>): <c>DCS 1 $ r … Pt ST</c> for a setting erikterm has, else <c>DCS 0 $ r ST</c>.</summary>
    private void RequestStatusString(ReadOnlySpan<byte> setting)
    {
        string? value = setting switch
        {
            [(byte)'m'] => SgrStatus(),
            [(byte)'r'] => $"{_top + 1};{_bottom + 1}r",
            [(byte)' ', (byte)'q'] => $"{CursorStyleNumber()} q",
            [(byte)'"', (byte)'p'] => "62;1\"p", // VT220-level (as DA1 says), 7-bit controls
            [(byte)'"', (byte)'q'] => "0\"q",    // DECSCA: characters are never protected
            [(byte)'t'] => $"{_rows}t",          // DECSLPP
            [(byte)'*', (byte)'|'] => $"{_rows}*|", // DECSNLS
            _ => null, // includes s (DECSLRM): no left/right margins
        };
        Respond(Encoding.ASCII.GetBytes(value is null ? "\eP0$r\e\\" : $"\eP1$r{value}\e\\"));
    }

    private string SgrStatus()
    {
        var sb = new StringBuilder();
        SgrText.AppendParams(sb, _fg, _bg, _attr & CellAttributes.SgrMask, _ulColor, colon: true);
        return sb.Append('m').ToString();
    }

    private int CursorStyleNumber()
    {
        int steady = CursorBlink ? 0 : 1;
        return CursorShape switch
        {
            CursorShape.Underline => 3 + steady,
            CursorShape.Bar => 5 + steady,
            _ => 1 + steady,
        };
    }

    private void Respond(ReadOnlySpan<byte> data) => _replies.Send(data);

    // ---- Helpers ------------------------------------------------------------------------------

    private static Row[] NewBuffer(int cols, int rows)
    {
        var rowsArr = new Row[rows];
        for (int i = 0; i < rows; i++) rowsArr[i] = new Row(cols);
        return rowsArr;
    }

    private static bool[] NewTabs(int cols)
    {
        var tabs = new bool[cols];
        for (int i = 8; i < cols; i += 8) tabs[i] = true;
        return tabs;
    }

    /// <summary>DEC Special Graphics (ESC ( 0) for 0x5F..0x7E — line drawing.</summary>
    private static ReadOnlySpan<int> DecSpecialGraphics =>
    [
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518,
        0x2510, 0x250C, 0x2514, 0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534,
        0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
    ];
}
