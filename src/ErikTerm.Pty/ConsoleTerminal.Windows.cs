using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ErikTerm.Pty;

/// <summary>
/// Windows console: virtual-terminal output and input modes, key and resize records from <c>ReadConsoleInputW</c>.
/// Only the characters of key-down records are forwarded (with virtual-terminal input they already are the
/// escape sequences); a waitable event plays the part of the self-pipe.
/// </summary>
[SupportedOSPlatform("windows")]
#pragma warning disable S6640 // the console APIs take pointers to native input records and mode values
internal sealed unsafe partial class WindowsConsoleTerminal : ConsoleTerminal
#pragma warning restore S6640
{
    private const string Lib = "kernel32.dll";
    private const int StdIn = -10, StdOut = -11;
    private const uint ENABLE_PROCESSED_OUTPUT = 0x1, ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4, DISABLE_NEWLINE_AUTO_RETURN = 0x8;
    private const uint ENABLE_WINDOW_INPUT = 0x8, ENABLE_VIRTUAL_TERMINAL_INPUT = 0x200, ENABLE_EXTENDED_FLAGS = 0x80;
    private const uint INFINITE = 0xFFFFFFFF, WAIT_OBJECT_0 = 0;
    private const ushort KEY_EVENT = 1, WINDOW_BUFFER_SIZE_EVENT = 4;

    private readonly nint _in, _out, _wake;
    private uint _savedIn, _savedOut;
    private volatile bool _raw;
    private readonly bool _isTerminal;

    public WindowsConsoleTerminal()
    {
        _in = GetStdHandle(StdIn);
        _out = GetStdHandle(StdOut);
        _isTerminal = GetConsoleMode(_in, out _) != 0 && GetConsoleMode(_out, out _) != 0;
        _wake = CreateEventW(0, 0, 0, null);
    }

    public override bool IsTerminal => _isTerminal;

    public override (int Cols, int Rows) Size()
    {
        if (GetConsoleScreenBufferInfo(_out, out var info) == 0) return (80, 24);
        int cols = info.WindowRight - info.WindowLeft + 1, rows = info.WindowBottom - info.WindowTop + 1;
        return cols > 0 && rows > 0 ? (cols, rows) : (80, 24);
    }

    public override void EnterRawMode()
    {
        if (_raw) return;
        if (GetConsoleMode(_in, out _savedIn) == 0 || GetConsoleMode(_out, out _savedOut) == 0) throw new IOException("GetConsoleMode failed");
        SetConsoleMode(_in, ENABLE_WINDOW_INPUT | ENABLE_VIRTUAL_TERMINAL_INPUT | ENABLE_EXTENDED_FLAGS);
        SetConsoleMode(_out, ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN);
        _raw = true;
    }

    public override void Restore()
    {
        if (!_raw) return;
        _raw = false;
        SetConsoleMode(_in, _savedIn);
        SetConsoleMode(_out, _savedOut);
    }

    public override int Read(Span<byte> buffer, int timeoutMs)
    {
        nint* handles = stackalloc nint[2];
        handles[0] = _in;
        handles[1] = _wake;
        uint wait = WaitForMultipleObjects(2, handles, 0, timeoutMs < 0 ? INFINITE : (uint)timeoutMs);
        if (wait != WAIT_OBJECT_0) return 0;

        InputRecord* records = stackalloc InputRecord[32];
        if (ReadConsoleInputW(_in, records, 32, out uint count) == 0) return -1;
        Span<char> chars = stackalloc char[64];
        int charCount = 0;
        for (uint i = 0; i < count; i++)
        {
            if (records[i].EventType == WINDOW_BUFFER_SIZE_EVENT) { MarkResized(); continue; }
            if (records[i].EventType != KEY_EVENT || records[i].KeyDown == 0 || records[i].UnicodeChar == 0) continue;
            chars[charCount++] = records[i].UnicodeChar;
        }
        // Encoded together, so a surrogate pair split across two records stays one character.
        return charCount == 0 ? 0 : System.Text.Encoding.UTF8.GetBytes(chars[..charCount], buffer);
    }

    public override void Write(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                if (WriteFile(_out, p + offset, (uint)(bytes.Length - offset), out uint n, 0) == 0 || n == 0) return;
                offset += (int)n;
            }
        }
    }

    public override void Wake() => SetEvent(_wake);

    protected override void Dispose(bool disposing)
    {
        Restore();
        if (_wake != 0) CloseHandle(_wake);
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(14)] public char UnicodeChar;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public short SizeX, SizeY, CursorX, CursorY;
        public ushort Attributes;
        public short WindowLeft, WindowTop, WindowRight, WindowBottom;
        public short MaxX, MaxY;
    }

    [LibraryImport(Lib)] private static partial nint GetStdHandle(int std);
    [LibraryImport(Lib)] private static partial int GetConsoleMode(nint handle, out uint mode);
    [LibraryImport(Lib)] private static partial int SetConsoleMode(nint handle, uint mode);
    [LibraryImport(Lib)] private static partial int GetConsoleScreenBufferInfo(nint handle, out ConsoleScreenBufferInfo info);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateEventW(nint attributes, int manualReset, int initialState, string? name);
    [LibraryImport(Lib)] private static partial int SetEvent(nint handle);
    [LibraryImport(Lib)] private static partial int CloseHandle(nint handle);
    [LibraryImport(Lib)] private static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);
    [LibraryImport(Lib)] private static partial int ReadConsoleInputW(nint handle, InputRecord* records, uint length, out uint read);
    [LibraryImport(Lib)] private static partial int WriteFile(nint handle, byte* buffer, uint length, out uint written, nint overlapped);
}
