using ErikTerm.Pty;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.Compare;

/// <summary>
/// The real terminal behind <see cref="ITuiTerminal"/>: full-screen setup and teardown around a
/// <see cref="ConsoleTerminal"/>, and a process-wide guard that restores the terminal on signals, exit and crashes.
/// </summary>
internal sealed class TuiConsole : ITuiTerminal, IDisposable
{
    private readonly ConsoleTerminal _console;
    private bool _fullScreen;
    private readonly List<System.Runtime.InteropServices.PosixSignalRegistration> _signals = [];
    private Action? _onStop;
    private volatile int _stopSignal;

    public TuiConsole(ConsoleTerminal console)
    {
        _console = console;
        AppDomain.CurrentDomain.UnhandledException += OnCrash;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
    }

    public int StopSignal => _stopSignal;

    /// <summary>SIGINT/SIGTERM/SIGHUP: restore and leave via the loop instead of dying with the terminal in raw mode.</summary>
    public void CatchSignals(Action stop)
    {
        _onStop = stop;
        foreach (var signal in new[] { System.Runtime.InteropServices.PosixSignal.SIGINT, System.Runtime.InteropServices.PosixSignal.SIGTERM, System.Runtime.InteropServices.PosixSignal.SIGHUP })
            _signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(signal, c =>
            {
                c.Cancel = true;
                _stopSignal = c.Signal switch
                {
                    System.Runtime.InteropServices.PosixSignal.SIGINT => 130,
                    System.Runtime.InteropServices.PosixSignal.SIGTERM => 143,
                    < 0 => 129,
                    _ => 128 + (int)c.Signal,
                };
                _onStop?.Invoke();
            }));
    }

    public (int Cols, int Rows) Size() => _console.Size();
    public int Read(Span<byte> buffer, int timeoutMs) => _console.Read(buffer, timeoutMs);
    public void Write(ReadOnlySpan<byte> bytes) => _console.Write(bytes);
    public void Wake() => _console.Wake();
    public bool TakeResized() => _console.TakeResized();

    public void EnterFullScreen()
    {
        _console.EnterRawMode();
        _console.Write(TuiTheme.EnterSequence);
        _fullScreen = true;
    }

    public void LeaveFullScreen()
    {
        if (!_fullScreen) { _console.Restore(); return; }
        _fullScreen = false;
        _console.Write(TuiTheme.LeaveSequence);
        _console.Restore();
    }

    private void OnCrash(object? sender, UnhandledExceptionEventArgs e) => LeaveFullScreen();
    private void OnExit(object? sender, EventArgs e) => LeaveFullScreen();

    public void Dispose()
    {
        LeaveFullScreen();
        foreach (var s in _signals) s.Dispose();
        _signals.Clear();
        AppDomain.CurrentDomain.UnhandledException -= OnCrash;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        _console.Dispose();
    }
}

/// <summary>Runs a screen on the terminal: the shared wrapper of <c>compare</c> and <c>mergetool</c>.</summary>
internal static class TuiHost
{
    /// <summary>Opens the terminal, runs the program, restores it; an exception is rethrown after the terminal is back.</summary>
    public static int Run(Func<TuiConsole, int> program, string command)
    {
        var console = ConsoleTerminal.TryOpen(out string? error);
        if (console is null)
        {
            Console.Error.WriteLine($"erikterm {command}: {error}");
            return 2;
        }
        using var tui = new TuiConsole(console);
        return program(tui);
    }
}
