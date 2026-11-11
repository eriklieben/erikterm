using System.Collections.Concurrent;

namespace ErikTerm.Ui.Compare;

/// <summary>What a screen needs from the terminal; the real one is in the app, the tests script a fake.</summary>
public interface ITuiTerminal
{
    (int Cols, int Rows) Size();
    /// <summary>Blocks up to <paramref name="timeoutMs"/> (-1 forever). Byte count, 0 for timeout or a wake, -1 when input is gone.</summary>
    int Read(Span<byte> buffer, int timeoutMs);
    void Write(ReadOnlySpan<byte> bytes);
    void Wake();
    bool TakeResized();
    /// <summary>Raw mode, alternate screen, mouse, cursor hidden (and the reverse).</summary>
    void EnterFullScreen();
    void LeaveFullScreen();
}

/// <summary>Runs work off the input loop and calls back on the loop's thread.</summary>
public interface IBackground
{
    void Run<T>(Func<T> work, Action<T> done);
}

/// <summary>A full-screen program: drawn from its state, driven by events.</summary>
public interface ITuiScreen
{
    bool Done { get; }
    int ExitCode { get; }
    void Render(Frame frame);
    void OnKey(KeyEvent key);
    void OnMouse(MouseEvent mouse, Frame? lastFrame);
    void OnThemeColor(uint backgroundColor);
    /// <summary>The terminal gained or lost focus (mode 1004).</summary>
    void OnFocus(bool focused) { }
    /// <summary>Asks for bracketed paste, so that pasted text arrives through <see cref="OnPaste"/> instead of as key presses.</summary>
    bool WantsPaste => false;
    void OnPaste(string text) { }
}

/// <summary>The event loop shared by the screens: input, background completions, resize, rendering only when something changed.</summary>
public sealed class TuiLoop : IBackground
{
    private readonly ITuiTerminal _terminal;
    private readonly ConcurrentQueue<Action> _completed = new();
    private readonly FrameEncoder _encoder = new();
    private ITuiScreen? _screen;
    private volatile bool _stop;

    public TuiLoop(ITuiTerminal terminal) => _terminal = terminal;

    /// <summary>Makes the loop leave and restore the terminal (signals, a dropped connection).</summary>
    public void Stop()
    {
        _stop = true;
        _terminal.Wake();
    }

    /// <summary>Forces a full redraw next frame (the terminal content was disturbed by an external program).</summary>
    public void Invalidate() => _encoder.Reset();

    public void Attach(ITuiScreen screen) => _screen = screen;

    public void Run<T>(Func<T> work, Action<T> done)
    {
        Task.Run(() =>
        {
            try
            {
                T result = work();
                _completed.Enqueue(() => done(result));
            }
            catch (Exception ex)
            {
                var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                _completed.Enqueue(captured.Throw); // surfaces on the loop thread, which restores the terminal
            }
            _terminal.Wake();
        });
    }

    public void Run()
    {
        if (_screen is null) throw new InvalidOperationException("no screen attached");
        var screen = _screen;
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();
        Span<byte> buffer = stackalloc byte[4096];
        Frame? frame = null;
        bool dirty = true;
        _terminal.EnterFullScreen();
        _terminal.Write("\e]11;?\e\\"u8);
        if (screen.WantsPaste) _terminal.Write("\e[?2004h"u8);
        while (!screen.Done && !_stop)
        {
            dirty |= RunCompleted();
            if (_terminal.TakeResized()) { dirty = true; _encoder.Reset(); }
            if (screen.Done || _stop) break;
            if (dirty)
            {
                frame = DrawFrame(screen);
                dirty = false;
            }

            int n = _terminal.Read(buffer, decoder.EscapePending ? 40 : -1);
            if (n < 0) break;
            Decode(decoder, buffer, n, events);
            dirty |= Dispatch(events, screen, frame);
        }
        _terminal.LeaveFullScreen();
    }

    /// <summary>Runs the background completions that arrived; true when there were any.</summary>
    private bool RunCompleted()
    {
        bool any = false;
        while (_completed.TryDequeue(out var action)) { action(); any = true; }
        return any;
    }

    private Frame DrawFrame(ITuiScreen screen)
    {
        var (cols, rows) = _terminal.Size();
        var frame = new Frame(cols, rows);
        screen.Render(frame);
        _terminal.Write(_encoder.Encode(frame));
        return frame;
    }

    /// <summary>A read of 0 is a timeout or a wake: only a buffered lone ESC can complete then.</summary>
    private static void Decode(TuiInputDecoder decoder, Span<byte> buffer, int n, List<TuiEvent> events)
    {
        events.Clear();
        if (n > 0) decoder.Feed(buffer[..n], events);
        else decoder.FlushEscape(events);
    }

    private static bool Dispatch(List<TuiEvent> events, ITuiScreen screen, Frame? frame)
    {
        foreach (var e in events)
        {
            if (e.Key is { } key) screen.OnKey(key);
            if (e.Mouse is { } mouse) screen.OnMouse(mouse, frame);
            if (e.BackgroundColor is { } color) screen.OnThemeColor(color);
            if (e.Focused is { } focused) screen.OnFocus(focused);
            if (e.Paste is { } paste) screen.OnPaste(paste);
        }
        return events.Count > 0;
    }
}

/// <summary>The dark and light palettes.</summary>
public sealed record TuiTheme(
    uint Base, uint Mantle, uint Crust, uint Surface0, uint Surface1, uint Text, uint Subtext, uint Overlay,
    uint Blue, uint Peach, uint Green, uint Red, uint Lavender, uint Mauve,
    uint OursBg, uint OursHi, uint TheirsBg, uint TheirsHi, uint AutoBg, uint ConflictBg, uint ConflictHi, uint QuietBg, uint CustomBg, uint GapFg, uint OnAccent)
{
    public static readonly TuiTheme Dark = new(
        Base: 0x24273A, Mantle: 0x1E2030, Crust: 0x181926, Surface0: 0x363A4F, Surface1: 0x494D64, Text: 0xCAD3F5,
        Subtext: 0xA5ADCB, Overlay: 0x8087A2, Blue: 0x8AADF4, Peach: 0xF5A97F, Green: 0xA6DA95, Red: 0xED8796,
        Lavender: 0xB7BDF8, Mauve: 0xC6A0F6,
        OursBg: 0x2C3B5E, OursHi: 0x44619C, TheirsBg: 0x4B3A32, TheirsHi: 0x8A5F45, AutoBg: 0x2F4A3A,
        ConflictBg: 0x4A2D38, ConflictHi: 0x7A3B47, QuietBg: 0x343850, CustomBg: 0x403A63, GapFg: 0x363A4F, OnAccent: 0x181926);

    public static readonly TuiTheme Light = new(
        Base: 0xEFF1F5, Mantle: 0xE6E9EF, Crust: 0xDCE0E8, Surface0: 0xCCD0DA, Surface1: 0xBCC0CC, Text: 0x4C4F69,
        Subtext: 0x6C6F85, Overlay: 0x8C8FA1, Blue: 0x1E66F5, Peach: 0xFE640B, Green: 0x40A02B, Red: 0xD20F39,
        Lavender: 0x7287FD, Mauve: 0x8839EF,
        OursBg: 0xD6E2FB, OursHi: 0xA9C2F7, TheirsBg: 0xFDE3D3, TheirsHi: 0xF8BD99, AutoBg: 0xD3ECD1,
        ConflictBg: 0xF7D3DA, ConflictHi: 0xEFA4B3, QuietBg: 0xE4E6EE, CustomBg: 0xE2D9F7, GapFg: 0xCCD0DA, OnAccent: 0xEFF1F5);

    /// <summary>Light when the terminal's background (an OSC 11 reply) is bright.</summary>
    public static TuiTheme ForBackground(uint rgb)
    {
        uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 > 0.5 ? Light : Dark;
    }

    public static ReadOnlySpan<byte> EnterSequence => "\e[?1049h\e[?25l\e[?1000h\e[?1002h\e[?1006h\e[?1004h\e[2J"u8;
    public static ReadOnlySpan<byte> LeaveSequence => "\e[?2004l\e[?1004l\e[?1006l\e[?1002l\e[?1000l\e[?25h\e[?2026l\e[0m\e[?1049l"u8;
}
