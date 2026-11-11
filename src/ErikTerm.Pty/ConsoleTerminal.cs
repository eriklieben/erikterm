using System.Runtime.InteropServices;

namespace ErikTerm.Pty;

/// <summary>
/// The controlling terminal of a full-screen text program (<c>erikterm compare</c>, <c>erikterm mergetool</c>): raw
/// mode, size, blocking read with a wake-up, write. Linux and macOS through termios with a self-pipe, Windows through
/// console modes and <c>ReadConsoleInput</c>. <see cref="Restore"/> is idempotent and safe from any thread, so a
/// signal or crash handler can call it as well as the normal exit path.
/// </summary>
public abstract class ConsoleTerminal : IDisposable
{
    private volatile int _resized;

    /// <summary>Returns null with a message when stdin or stdout isn't a terminal.</summary>
    public static ConsoleTerminal? TryOpen(out string? error)
    {
        try
        {
            ConsoleTerminal terminal = OperatingSystem.IsWindows() ? new WindowsConsoleTerminal() : new UnixConsoleTerminal();
            if (terminal.IsTerminal) { error = null; return terminal; }
            terminal.Dispose();
            error = "this command needs a terminal: stdin and stdout must not be redirected";
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or InvalidOperationException)
        {
            error = $"can't open the terminal: {ex.Message}";
            return null;
        }
    }

    public abstract bool IsTerminal { get; }

    /// <summary>Terminal size in cells, or (80, 24) when it can't be read.</summary>
    public abstract (int Cols, int Rows) Size();

    /// <summary>Switches to raw input and output, saving the current settings for <see cref="Restore"/>.</summary>
    public abstract void EnterRawMode();

    /// <summary>Puts back the settings saved by <see cref="EnterRawMode"/>. Idempotent; safe from signal handlers.</summary>
    public abstract void Restore();

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> (-1 forever) for input. Returns the byte count; 0 for timeout, a
    /// <see cref="Wake"/> or a resize; -1 when input is gone (hung up).
    /// </summary>
    public abstract int Read(Span<byte> buffer, int timeoutMs);

    public abstract void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Makes a blocked <see cref="Read"/> return 0. Callable from other threads.</summary>
    public abstract void Wake();

    /// <summary>Set by the window-size-change notification; cleared when read.</summary>
    public bool TakeResized() => Interlocked.Exchange(ref _resized, 0) != 0;

    protected void MarkResized() => _resized = 1;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>Linux and macOS: fds 0 and 1 unless told otherwise (tests), termios as opaque bytes (<see cref="TermiosConstants"/>), a self-pipe for wake-ups.</summary>
#pragma warning disable S6640 // termios and the self-pipe are libc calls with pointer arguments
internal sealed unsafe class UnixConsoleTerminal : ConsoleTerminal
#pragma warning restore S6640
{
    private readonly int InFd, OutFd;
    private readonly byte[] _saved;
    private volatile bool _raw;
    private readonly int _pipeRead = -1, _pipeWrite = -1;
    private readonly TermiosConstants _c = TermiosConstants.Current;
    private readonly bool _isTerminal;
    private PosixSignalRegistration? _winch;

    public UnixConsoleTerminal(int inFd = 0, int outFd = 1)
    {
        InFd = inFd;
        OutFd = outFd;
        _saved = new byte[_c.Size];
        _isTerminal = Native.isatty(InFd) == 1 && Native.isatty(OutFd) == 1;
        if (!_isTerminal) return;
        int* fds = stackalloc int[2];
        if (Native.pipe(fds) != 0) throw new IOException("pipe() failed");
        _pipeRead = fds[0];
        _pipeWrite = fds[1];
        Native.fcntl(_pipeRead, Native.F_SETFD, Native.FD_CLOEXEC);
        Native.fcntl(_pipeWrite, Native.F_SETFD, Native.FD_CLOEXEC);
    }

    public override bool IsTerminal => _isTerminal;

    public override (int Cols, int Rows) Size()
    {
        ushort* winsize = stackalloc ushort[4];
        return Native.ioctl(OutFd, _c.TIOCGWINSZ, winsize) == 0 && winsize[0] > 0 && winsize[1] > 0
            ? (winsize[1], winsize[0])
            : (80, 24);
    }

    public override void EnterRawMode()
    {
        if (_raw) return;
        fixed (byte* p = _saved)
        {
            if (Native.tcgetattr(InFd, p) != 0) throw new IOException("tcgetattr failed");
            byte[] raw = (byte[])_saved.Clone();
            _c.MakeRaw(raw);
            fixed (byte* q = raw)
                if (Native.tcsetattr(InFd, 0 /* TCSANOW */, q) != 0) throw new IOException("tcsetattr failed");
        }
        _raw = true;
        if (!OperatingSystem.IsWindows()) _winch ??= PosixSignalRegistration.Create(PosixSignal.SIGWINCH, c => { c.Cancel = true; MarkResized(); Wake(); });
    }

    public override void Restore()
    {
        if (!_raw) return;
        _raw = false;
        fixed (byte* p = _saved) _ = Native.tcsetattr(InFd, 0, p);
    }

    public override int Read(Span<byte> buffer, int timeoutMs)
    {
        Native.PollFd* fds = stackalloc Native.PollFd[2];
        fds[0] = new Native.PollFd { Fd = InFd, Events = Native.POLLIN };
        fds[1] = new Native.PollFd { Fd = _pipeRead, Events = Native.POLLIN };
        int r = Native.poll(fds, 2, timeoutMs);
        if (r < 0) return Marshal.GetLastPInvokeError() == Native.EINTR ? 0 : -1;
        if (r == 0) return 0;
        if ((fds[1].Revents & Native.POLLIN) != 0)
        {
            byte* drain = stackalloc byte[64];
            _ = Native.read(_pipeRead, drain, 64);
        }
        if ((fds[0].Revents & Native.POLLIN) != 0)
        {
            fixed (byte* p = buffer)
            {
                nint n = Native.read(InFd, p, buffer.Length);
                if (n > 0) return (int)n;
                if (n == 0) return -1;
                return Marshal.GetLastPInvokeError() == Native.EINTR ? 0 : -1;
            }
        }
        return (fds[0].Revents & (Native.POLLHUP | Native.POLLERR | Native.POLLNVAL)) != 0 ? -1 : 0;
    }

    public override void Write(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                nint n = Native.write(OutFd, p + offset, bytes.Length - offset);
                if (n < 0)
                {
                    int e = Marshal.GetLastPInvokeError();
                    if (e == Native.EINTR) continue;
                    if (e == Native.EAGAIN) { Thread.Sleep(1); continue; }
                    return; // the terminal went away: nothing useful to do
                }
                offset += (int)n;
            }
        }
    }

    public override void Wake()
    {
        if (_pipeWrite < 0) return;
        byte b = 1;
        _ = Native.write(_pipeWrite, &b, 1);
    }

    protected override void Dispose(bool disposing)
    {
        Restore();
        _winch?.Dispose();
        if (_pipeRead >= 0) _ = Native.close(_pipeRead);
        if (_pipeWrite >= 0) _ = Native.close(_pipeWrite);
        base.Dispose(disposing);
    }
}

#pragma warning disable S6640 // the libc declarations here take raw pointers
internal static unsafe partial class Native
#pragma warning restore S6640
{
    public const int F_SETFD = 2, FD_CLOEXEC = 1;

    [LibraryImport(Libc)]
    public static partial int isatty(int fd);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int tcgetattr(int fd, byte* termios);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int tcsetattr(int fd, int optionalActions, byte* termios);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int pipe(int* fds);

    // fcntl is variadic: pass the int argument the way each ABI wants it (macOS arm64 puts it on the stack).
    public static int fcntl(int fd, int cmd, int arg) =>
        PtyConstants.VariadicOnStack ? fcntl_stack(fd, cmd, 0, 0, 0, 0, 0, 0, arg) : fcntl_reg(fd, cmd, arg);

    [LibraryImport(Libc, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int fcntl_reg(int fd, int cmd, int arg);

    [LibraryImport(Libc, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int fcntl_stack(int fd, int cmd, int a2, int a3, int a4, int a5, int a6, int a7, int arg);
}
