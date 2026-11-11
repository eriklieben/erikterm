using System.Text;

namespace ErikTerm.Core;

/// <summary>The output of one shell command, found through OSC 133 marks (absolute line numbers, inclusive).</summary>
public readonly record struct CommandBlock(long PromptLine, long OutputStart, long OutputEnd, int ExitCode, string Command)
{
    public int LineCount => (int)(OutputEnd - OutputStart + 1);
}

/// <summary>
/// A command that ran and finished (OSC 133;C, then D): the command line the shell sent with C (null when it sent
/// none: <see cref="Terminal.CommandText"/> reads it off the screen), its exit status (D's parameter, 0 when absent)
/// and how long it ran. <see cref="PromptLine"/> / <see cref="OutputLine"/> are absolute lines (prompt, start of output).
/// </summary>
public readonly record struct FinishedCommand(string? CommandLine, int ExitCode, long DurationMs, long PromptLine, long OutputLine)
{
    /// <summary>
    /// The shell would keep it out of its history: typed with a leading space (HISTCONTROL=ignorespace,
    /// HIST_IGNORE_SPACE, fish), or sent without a command line by a shell that otherwise sends one (erikterm's bash
    /// script leaves out what history didn't record). Recent-command lists skip it.
    /// </summary>
    public bool Private { get; init; }

    /// <summary>The command line spanned several lines (they were joined with spaces in <see cref="CommandLine"/>).</summary>
    public bool MultiLine { get; init; }
}

/// <summary>What <see cref="SemanticPrompts"/> reads from the terminal: rows and text by absolute line.</summary>
internal interface ITerminalLines
{
    long FirstLine { get; }
    long EndLine { get; }
    Row? GetLine(long line);
    string GetText(long startLine, int startCol, long endLine, int endCol, bool block = false);
}

/// <summary>
/// OSC 133 semantic prompts: the marks on the rows, the command that is running and the ones that finished, and finding
/// prompts and command output again. The terminal decides when marks count (not on the alternate screen) and clears what
/// belonged to the program when a prompt comes back.
/// </summary>
internal sealed class SemanticPrompts(ITerminalLines lines)
{
    // ---- OSC 133 semantic prompts -------------------------------------------------------------

    private bool _commandRunning;    // C seen since the last A
    private long _commandStartedAt, _commandOutputLine; // CommandClock() and the line at the C mark
    private string? _runningCommandLine;                // C's cmdline / cmdline_url
    private bool _runningCommandPrivate, _runningCommandMultiLine;
    private bool _shellSendsCommandLines;               // a C carried a command line: one without means it was held back

    /// <summary>A command started (OSC 133;C) and hasn't finished (D) or been replaced by a new prompt (A).</summary>
    public bool CommandRunning => _commandRunning;

    /// <summary>Absolute line of the latest prompt (OSC 133;A), or -1; reflow moves it with its row.</summary>
    public long PromptLine { get; set; } = -1;

    /// <summary>Commands started so far (OSC 133;C): lets callers tell command output from a prompt redraw.</summary>
    public long CommandsStarted { get; private set; }

    /// <summary>Exit status of the last command that reported one (OSC 133;D), or null.</summary>
    public int? LastExitCode { get; private set; }

    /// <summary>Commands that finished so far (OSC 133;D after C); <see cref="LastFinishedCommand"/> is the latest.</summary>
    public long CommandsFinished { get; private set; }

    /// <summary>The latest command that finished (valid once <see cref="CommandsFinished"/> &gt; 0).</summary>
    public FinishedCommand LastFinishedCommand { get; private set; }

    private readonly FinishedCommand[] _finishedLog = new FinishedCommand[FinishedLogSize];
    private const int FinishedLogSize = 16;

    /// <summary>
    /// The <paramref name="index"/>-th command that finished (0 = the first), for callers that look less often than
    /// commands can finish; only the latest few are kept.
    /// </summary>
    public bool TryGetFinishedCommand(long index, out FinishedCommand command)
    {
        command = default;
        if (index < 0 || index >= CommandsFinished || index < CommandsFinished - FinishedLogSize) return false;
        command = _finishedLog[index % FinishedLogSize];
        return true;
    }

    /// <summary>Monotonic milliseconds for command durations (tests replace it).</summary>
    public Func<long> CommandClock { get; set; } = static () => Environment.TickCount64;

    /// <summary>
    /// The command line of a finished command: the one the shell sent, else the text typed after its prompt
    /// (OSC 133;B up to where output began), or "" when that has scrolled out of the history.
    /// </summary>
    public string CommandText(in FinishedCommand command) =>
        command.CommandLine ?? (command.PromptLine >= lines.FirstLine && command.OutputLine > command.PromptLine
            ? CommandTextBetween(command.PromptLine, command.OutputLine)
            : "");

    /// <summary>
    /// OSC 133 ; A|B|C|D [; params] (not empty) on the cursor's <paramref name="row"/>, absolute <paramref name="line"/>,
    /// column <paramref name="x"/>. True when a prompt started: whatever the program set is over.
    /// </summary>
    public bool Mark(ReadOnlySpan<byte> arg, Row row, long line, int x)
    {
        row.Touch(); // marks are drawn in the gutter
        switch ((char)arg[0])
        {
            case 'A':
            case 'N': // "new command" (iTerm2/kitty): a prompt start that also ends any previous command
                row.Marks |= SemanticMarks.Prompt;
                PromptLine = line;
                _commandRunning = false;
                return true;
            case 'P': // prompt continuation (secondary prompts): not a navigation target
                break;
            case 'B':
                row.Marks |= SemanticMarks.Input;
                row.CommandColumn = x;
                break;
            case 'C':
                row.Marks |= SemanticMarks.Output;
                OnCommandStart(arg, line);
                break;
            case 'D':
                row.Marks |= SemanticMarks.End;
                if (_commandRunning) OnCommandEnd(arg); // not running: e.g. an empty command line, nothing ran
                break;
        }
        return false;
    }

    /// <summary>A new program starts (Restart Pane): no command of the old one is running, and its shell may not send command lines.</summary>
    public void ResetForNewProgram()
    {
        _commandRunning = false;
        _runningCommandLine = null;
        _shellSendsCommandLines = false;
    }

    private void OnCommandStart(ReadOnlySpan<byte> arg, long line)
    {
        _commandRunning = true;
        CommandsStarted++;
        _commandStartedAt = CommandClock();
        _commandOutputLine = line;
        // The command line itself, so it's known even when a transient prompt redrew away the B mark.
        _runningCommandLine = CommandLineParam(arg, out bool leadingSpace, out _runningCommandMultiLine);
        _runningCommandPrivate = leadingSpace
            || _runningCommandLine is null && (_shellSendsCommandLines || TypedWithLeadingSpace(PromptLine, line));
        if (_runningCommandLine is not null) _shellSendsCommandLines = true;
        if (_runningCommandLine is { } cmd && lines.GetLine(PromptLine) is { } promptRow && (promptRow.Marks & SemanticMarks.Prompt) != 0)
            promptRow.CommandLine = cmd;
    }

    private void OnCommandEnd(ReadOnlySpan<byte> arg)
    {
        _commandRunning = false;
        int code = ExitCodeParam(arg);
        LastExitCode = code;
        if (lines.GetLine(PromptLine) is { } prompt && (prompt.Marks & SemanticMarks.Prompt) != 0)
        {
            prompt.ExitCode = code;
            prompt.Touch();
        }
        LastFinishedCommand = new FinishedCommand(_runningCommandLine, code, Math.Max(0, CommandClock() - _commandStartedAt),
            PromptLine, _commandOutputLine) { Private = _runningCommandPrivate, MultiLine = _runningCommandMultiLine };
        _finishedLog[CommandsFinished % FinishedLogSize] = LastFinishedCommand;
        CommandsFinished++;
        _runningCommandLine = null;
    }

    /// <summary>The exit status from <c>D;status[;…]</c>; 0 when absent or not a number.</summary>
    private static int ExitCodeParam(ReadOnlySpan<byte> arg)
    {
        if (arg.Length <= 2 || arg[1] != (byte)';') return 0;
        var p = arg[2..];
        int end = p.IndexOf((byte)';');
        if (end >= 0) p = p[..end];
        return int.TryParse(p, out int code) ? code : 0;
    }

    private const int MaxCommandLine = 4096;

    /// <summary>
    /// <c>cmdline_url=</c> (percent-encoded UTF-8; kitty, oh-my-posh, erikterm's scripts) or <c>cmdline=</c> (raw)
    /// from an OSC 133;C, or null.
    /// </summary>
    internal static string? CommandLineParam(ReadOnlySpan<byte> arg) => CommandLineParam(arg, out _, out _);

    /// <summary>
    /// <see cref="CommandLineParam(ReadOnlySpan{byte})"/>, and whether the line as sent began with white space (the shell
    /// keeps such commands out of its history) or had more than one line.
    /// </summary>
    internal static string? CommandLineParam(ReadOnlySpan<byte> arg, out bool leadingSpace, out bool multiLine)
    {
        leadingSpace = multiLine = false;
        foreach (var range in arg.Split((byte)';'))
        {
            var p = arg[range];
            string? raw = null;
            if (p.StartsWith("cmdline_url="u8)) raw = Encoding.UTF8.GetString(PercentDecode(p["cmdline_url=".Length..]));
            // Raw form: the rest of the sequence, which may itself contain ';'.
            else if (p.StartsWith("cmdline="u8)) raw = Encoding.UTF8.GetString(arg[(range.Start.Value + "cmdline=".Length)..]);
            if (raw is null) continue;
            leadingSpace = raw.Length > 0 && raw[0] is ' ' or '\t';
            multiLine = raw.AsSpan().TrimEnd().IndexOfAny('\n', '\r') >= 0;
            return Clip(raw);
        }
        return null;

        // One line for display: newlines (multi-line commands) and other controls become spaces.
        static string? Clip(string s)
        {
            if (s.Length > MaxCommandLine) s = s[..MaxCommandLine];
            s = string.Create(s.Length, s, (dst, src) => { for (int i = 0; i < src.Length; i++) dst[i] = char.IsControl(src[i]) ? ' ' : src[i]; }).Trim();
            return s.Length == 0 ? null : s;
        }
    }

    private static byte[] PercentDecode(ReadOnlySpan<byte> s)
    {
        var bytes = new byte[s.Length];
        int n = 0;
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
            {
                bytes[n++] = (byte)(Hex(s[i + 1]) << 4 | Hex(s[i + 2]));
                i += 3;
            }
            else bytes[n++] = s[i++];
        }
        return bytes[..n];

        static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
        static int Hex(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;
    }

    /// <summary>The nearest prompt line before (dir &lt; 0) or after (dir &gt; 0) <paramref name="fromLine"/>.</summary>
    public long? FindPrompt(long fromLine, int dir)
    {
        int step = dir < 0 ? -1 : 1;
        for (long line = fromLine + step; line >= lines.FirstLine && line < lines.EndLine; line += step)
            if (lines.GetLine(line) is { } row && (row.Marks & SemanticMarks.Prompt) != 0) return line;
        return null;
    }

    /// <summary>True when any row carries OSC 133 marks (the shell has integration).</summary>
    public bool HasPromptMarks => PromptLine >= lines.FirstLine;

    /// <summary>
    /// The command whose prompt or output contains <paramref name="line"/>. A line on the current
    /// (still editable) prompt, or on a prompt whose command printed nothing, refers to the previous
    /// command — so "the command at the cursor" means the last one that ran.
    /// </summary>
    public CommandBlock? CommandAt(long line, long cursorLine)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            long prompt = FindPromptAtOrBefore(line);
            if (prompt < 0) return null;
            long next = FindPrompt(prompt, 1) ?? -1;
            long limit = next - 1;
            if (next < 0) limit = cursorLine - (_commandRunning ? 0 : 1);

            if (CommandBlockAt(prompt, limit) is { } block) return block;
            line = prompt - 1; // nothing ran here: try the command before
        }
        return null;
    }

    /// <summary>The command at a prompt, when it printed something between its output mark and <paramref name="limit"/>.</summary>
    private CommandBlock? CommandBlockAt(long prompt, long limit)
    {
        long output = FindOutputStart(prompt, limit);
        if (output < 0) return null;
        long end = limit;
        while (end >= output && RowContent.HasNoText(lines.GetLine(end))) end--;
        if (end < output) return null;
        var promptRow = lines.GetLine(prompt)!;
        return new CommandBlock(prompt, output, end, promptRow.ExitCode, promptRow.CommandLine ?? CommandTextBetween(prompt, output));
    }

    /// <summary>
    /// The first Output mark after a prompt's row, or -1: Enter always moves down before 133;C, and an Output mark on the
    /// prompt row itself is the previous command's (no output, so its C and this A share the row).
    /// </summary>
    private long FindOutputStart(long prompt, long limit)
    {
        for (long l = prompt + 1; l <= limit; l++)
            if (lines.GetLine(l) is { } r && (r.Marks & SemanticMarks.Output) != 0) return l;
        return -1;
    }

    private long FindPromptAtOrBefore(long line)
    {
        line = Math.Min(line, lines.EndLine - 1);
        if (lines.GetLine(line) is { } row && (row.Marks & SemanticMarks.Prompt) != 0) return line;
        return FindPrompt(line, -1) ?? -1;
    }

    /// <summary>
    /// The command typed at a prompt (OSC 133;B up to where output began) starts with a space: the shell keeps it out of
    /// its history (HISTCONTROL=ignorespace), so erikterm's lists do too.
    /// </summary>
    private bool TypedWithLeadingSpace(long prompt, long output)
    {
        if (prompt < 0) return false;
        for (long l = prompt; l < output; l++)
        {
            if (lines.GetLine(l) is not { } r || (r.Marks & SemanticMarks.Input) == 0) continue;
            var cells = r.Cells;
            int c = Math.Max(0, r.CommandColumn);
            if (c >= cells.Length || cells[c].Rune != ' ') return false;
            for (int i = c + 1; i < cells.Length; i++) if (cells[i].Rune > ' ') return true; // a space, then the command
            return false;
        }
        return false;
    }

    /// <summary>The command typed at a prompt: text after OSC 133;B up to where output began.</summary>
    private string CommandTextBetween(long prompt, long output)
    {
        for (long l = prompt; l < output; l++)
        {
            if (lines.GetLine(l) is not { } r || (r.Marks & SemanticMarks.Input) == 0) continue;
            long last = l;
            while (last + 1 < output && lines.GetLine(last)?.Wrapped == true) last++;
            return lines.GetText(l, Math.Max(0, r.CommandColumn), last, int.MaxValue / 2, block: false).Trim();
        }
        return "";
    }
}
