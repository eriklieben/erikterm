using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.Core.Accessibility;
using ErikTerm.Ui;
using ErikTerm.Ui.Accessibility;

namespace ErikTerm.App.Accessibility;

/// <summary>A visible pane as screen readers see it.</summary>
/// <param name="Grid">Where its grid is in the window, in window pixels.</param>
internal sealed record PaneView(int Id, string Name, RectI Grid, bool Focused, TerminalRows Rows, Pane Pane);

/// <summary>The window as screen readers see it: published by the main thread, read by UI Automation's threads.</summary>
internal sealed record WindowView(string Name, PaneView[] Panes, int CellWidth, int CellHeight, float Density, bool WindowFocused)
{
    public static readonly WindowView Empty = new("erikterm", [], 1, 1, 1, false);

    public PaneView? Focused => Array.Find(Panes, p => p.Focused);
}

/// <summary>
/// One window's UI Automation tree (Windows only): the root is the window, each visible pane is an element with the
/// text pattern, and events keep screen readers up to date. Nothing happens until UI Automation first asks for the
/// window (WM_GETOBJECT, <see cref="UiaWindowHook"/>), and after that only while a client listens
/// (UiaClientsAreListening): with no screen reader running, a tick costs one check every half second.
/// <para>
/// Threads: the main thread publishes an immutable <see cref="WindowView"/>, raises events and carries out what the
/// providers asked for (focus, select, scroll: <see cref="Post"/>). Providers run on whatever thread UI Automation
/// calls them on; they only read the published view and the terminals, under the terminal lock, a row at a time.
/// </para>
/// </summary>
internal sealed class WindowAccessibility : IDisposable
{
    /// <summary>How often the main thread checks whether anyone listens; the answer changes when a screen reader starts.</summary>
    private const int ListenCheckMs = 500;
    /// <summary>How often names and rectangles are published again while a client listens.</summary>
    private const int ViewRefreshMs = 250;
    /// <summary>Text-changed events and the output diff run at most this often per window.</summary>
    private const int TextCheckMs = 50;

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly IAccessibleWindow _window;
    private readonly ConcurrentQueue<Action> _requests = new();
    private readonly Action _wake;
    private readonly Lock _sync = new();
    private readonly Dictionary<int, PaneProvider> _providers = [];
    private readonly Dictionary<int, PaneSeen> _seen = [];
    private readonly Dictionary<Pane, TerminalRows> _rows = [];
    private readonly OutputAnnouncer _announcer = new();
    private volatile WindowView _view = WindowView.Empty;
    private RootProvider? _root;
    private bool _active, _listening, _disposed;
    private long _listenCheckedAt = long.MinValue / 2, _viewAt = long.MinValue / 2, _textAt = long.MinValue / 2;
    private int _focusedId = -1;
    private bool _windowFocused;
    private string _paneIds = "";

    private WindowAccessibility(nint hwnd, IAccessibleWindow window, Lock gate, Action wake)
    {
        Hwnd = hwnd;
        _window = window;
        Gate = gate;
        _wake = wake;
    }

    /// <summary>Makes the window answer UI Automation; null off Windows or when the window has no HWND.</summary>
    public static WindowAccessibility? Attach(IAccessibleWindow window, Lock gate, Action wake)
    {
        if (!OperatingSystem.IsWindows()) return null;
        nint hwnd = Sdl.Hwnd(window.SdlWindow);
        if (hwnd == 0) return null;
        var accessibility = new WindowAccessibility(hwnd, window, gate, wake);
        return UiaWindowHook.Install(accessibility) ? accessibility : null;
    }

    public nint Hwnd { get; }

    /// <summary>The lock the terminals are fed under.</summary>
    public Lock Gate { get; }

    public WindowView View => _view;

    // ---- Called by the providers (any thread) ---------------------------------------------------

    public PaneView? PaneView(int id) => Array.Find(_view.Panes, p => p.Id == id);

    /// <summary>The element of a visible pane (made on first use).</summary>
    public PaneProvider? Provider(int paneId)
    {
        if (PaneView(paneId) is null) return null;
        lock (_sync)
        {
            if (_disposed) return null;
            if (!_providers.TryGetValue(paneId, out var p)) _providers[paneId] = p = new PaneProvider(this, paneId);
            return p;
        }
    }

    public RootProvider Root
    {
        get
        {
            lock (_sync) return _root ??= new RootProvider(this);
        }
    }

    /// <summary>Runs <paramref name="request"/> on the main thread at its next tick.</summary>
    public void Post(Action request)
    {
        _requests.Enqueue(request);
        _wake();
    }

    /// <summary>A rectangle in window pixels, in screen pixels (what UI Automation measures in).</summary>
    public UiaRect ToScreen(float x, float y, float w, float h)
    {
        float d = _view.Density;
        var origin = new Uia.Point { X = (int)MathF.Round(x / d), Y = (int)MathF.Round(y / d) };
        Uia.ClientToScreen(Hwnd, ref origin);
        return new UiaRect { Left = origin.X, Top = origin.Y, Width = w / d, Height = h / d };
    }

    /// <summary>A point in screen pixels, in window pixels.</summary>
    public (float X, float Y) FromScreen(double x, double y)
    {
        var p = new Uia.Point { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        Uia.ScreenToClient(Hwnd, ref p);
        float d = _view.Density;
        return (p.X * d, p.Y * d);
    }

    /// <summary>Asks the main thread to focus a pane (IRawElementProviderFragment.SetFocus).</summary>
    public void RequestFocus(int paneId) => Post(() =>
    {
        if (PaneView(paneId) is { } pane) _window.FocusPane(pane.Pane);
    });

    /// <summary>Asks the main thread to make a span the pane's selection; an empty span clears it (ITextRangeProvider.Select).</summary>
    public void RequestSelect(int paneId, TextSpan span) => Post(() =>
    {
        if (PaneView(paneId) is not { } pane) return;
        lock (Gate) pane.Pane.Selection = span.IsEmpty ? null : SelectionOf(span, pane.Rows.Terminal.Columns);
        _window.Redraw();
    });

    /// <summary>Asks the main thread to scroll a line into the pane's view (ITextRangeProvider.ScrollIntoView).</summary>
    public void RequestScroll(int paneId, long line, bool alignToTop) => Post(() =>
    {
        if (PaneView(paneId) is not { } pane) return;
        lock (Gate)
        {
            var t = pane.Rows.Terminal;
            if (line >= t.ViewTopLine && line < t.ViewTopLine + t.Rows) return;
            t.ScrollViewToLine(alignToTop ? line : line - t.Rows + 1);
        }
        pane.Pane.Scroll.Snap();
        _window.Redraw();
    });

    /// <summary>erikterm's selection for a span: cell mode, whose columns are boundaries, so the end is exclusive like the span's.</summary>
    private static Selection SelectionOf(TextSpan span, int columns)
    {
        var (headLine, headCol) = (span.End.Line, span.End.Col);
        if (headCol == 0 && headLine > span.Start.Line) (headLine, headCol) = (headLine - 1, columns);
        return new Selection
        {
            AnchorLine = span.Start.Line, AnchorCol = span.Start.Col,
            HeadLine = headLine, HeadCol = headCol, Mode = SelectionMode.Cell,
        };
    }

    // ---- Main thread ----------------------------------------------------------------------------

    /// <summary>WM_GETOBJECT for the window's root: hands UI Automation the tree, and from now on keeps it current.</summary>
    public nint OnGetObject(nint wParam, nint lParam)
    {
        if (_disposed) return 0;
        if (!_active)
        {
            _active = true;
            _view = BuildView();
            _viewAt = Environment.TickCount64;
        }
        nint p = Uia.ComPointer(Root, Uia.IidSimple);
        nint result = Uia.UiaReturnRawElementProvider(Hwnd, wParam, lParam, p);
        Marshal.Release(p);
        return result;
    }

    /// <summary>Hook from TermWindow.Tick.</summary>
    public void Tick(long now)
    {
        while (_requests.TryDequeue(out var request)) request();
        if (!_active || _disposed) return;
        if (now - _listenCheckedAt >= ListenCheckMs)
        {
            _listenCheckedAt = now;
            bool listening = Uia.UiaClientsAreListening();
            if (listening && !_listening) ResetSeen();
            _listening = listening;
        }
        if (!_listening) return;
        // Focus moving is told at once; names and rectangles are refreshed a few times a second.
        if (now - _viewAt >= ViewRefreshMs || FocusMoved()) RefreshView(now);
        TickText(now);
        if (_announcer.Take(now) is { } a && _view.Focused is { } focused && Provider(focused.Id) is { } provider)
            Uia.RaiseNotification(provider, a.Text, a.Replaceable);
    }

    /// <summary>Text the user typed into the focused pane: its echo isn't announced (the screen reader speaks keys itself).</summary>
    public void Typed(string text, long now)
    {
        if (_active && _listening) _announcer.Typed(text, now);
    }

    /// <summary>How soon <see cref="Tick"/> has something to do (int.MaxValue: nothing).</summary>
    public int WakeMs(long now)
    {
        if (!_active || !_listening) return int.MaxValue;
        int ms = _announcer.WakeMs(now);
        if (AnyContentChanged()) ms = Math.Min(ms, (int)Math.Max(1, _textAt + TextCheckMs - now));
        return ms;
    }

    private bool FocusMoved() =>
        _window.IsWindowFocused != _windowFocused || _window.FocusedPane.Id != _focusedId;

    private WindowView BuildView()
    {
        var panes = _window.VisiblePanes;
        var focused = _window.FocusedPane;
        var views = new PaneView[panes.Count];
        for (int i = 0; i < panes.Count; i++)
        {
            var pane = panes[i];
            if (!_rows.TryGetValue(pane, out var rows)) _rows[pane] = rows = new TerminalRows(pane.Term, Gate);
            views[i] = new PaneView(pane.Id, PaneName(pane), pane.Target, ReferenceEquals(pane, focused), rows, pane);
        }
        foreach (var gone in _rows.Keys.Where(p => Array.FindIndex(views, v => ReferenceEquals(v.Pane, p)) < 0).ToList()) _rows.Remove(gone);
        string title = _window.ActiveTabTitle is { Length: > 0 } t ? t : PaneName(focused);
        var (cw, ch) = _window.CellSize;
        return new WindowView($"erikterm — {title}", views, Math.Max(1, cw), Math.Max(1, ch), _window.Density, _window.IsWindowFocused);
    }

    /// <summary>A pane's name: the title tmux gave it, else what a tab would show — the program, or a shell's folder.</summary>
    private static string PaneName(Pane pane)
    {
        if (pane.TmuxTitle is { Length: > 0 } tmux) return tmux;
        string label = pane.Label;
        if (!TabTitles.IsShell(label)) return label;
        return TabTitles.Auto(label, pane.Term.Title, PaneCwd.FromOsc7(pane.Term.WorkingDirectoryUri), Home);
    }

    private void RefreshView(long now)
    {
        _viewAt = now;
        var view = BuildView();
        _view = view;
        string ids = string.Join(',', view.Panes.Select(p => p.Id));
        if (ids != _paneIds)
        {
            _paneIds = ids;
            DropGoneProviders(view);
            Uia.RaiseChildrenInvalidated(Root);
        }
        int focusedId = view.Focused?.Id ?? -1;
        if (focusedId == _focusedId && view.WindowFocused == _windowFocused) return;
        (_focusedId, _windowFocused) = (focusedId, view.WindowFocused);
        _announcer.Clear();
        if (focusedId >= 0) Seen(focusedId).Tracker.Reset();
        if (view.WindowFocused && focusedId >= 0 && Provider(focusedId) is { } provider) Uia.RaiseEvent(provider, Uia.FocusChangedEvent);
    }

    /// <summary>TextChanged for panes with new output, caret and selection moves, and new output to announce.</summary>
    private void TickText(long now)
    {
        if (now - _textAt < TextCheckMs || (!AnyContentChanged() && !CaretOrSelectionMoved())) return;
        _textAt = now;
        foreach (var pane in _view.Panes) TickPaneText(pane, now);
    }

    private void TickPaneText(PaneView pane, long now)
    {
        var seen = Seen(pane.Id);
        OutputChange? change = null;
        bool textChanged, caretMoved;
        lock (Gate)
        {
            var t = pane.Rows.Terminal;
            textChanged = t.ContentVersion != seen.Version;
            seen.Version = t.ContentVersion;
            var caret = CaretKey(pane);
            caretMoved = caret != seen.Caret;
            seen.Caret = caret;
            if (textChanged && pane.Focused && _view.WindowFocused) change = seen.Tracker.Update(t);
        }
        if ((!textChanged && !caretMoved) || Provider(pane.Id) is not { } provider) return;
        if (textChanged) Uia.RaiseEvent(provider, Uia.TextChangedEvent);
        if (caretMoved) Uia.RaiseEvent(provider, Uia.TextSelectionChangedEvent);
        if (change is { } c) _announcer.Output(c.Text, c.AlternateScreen, c.RewriteOnly, now);
    }

    private bool AnyContentChanged()
    {
        foreach (var pane in _view.Panes)
            if (pane.Rows.Terminal.ContentVersion != Seen(pane.Id).Version) return true;
        return false;
    }

    private bool CaretOrSelectionMoved() => _view.Focused is { } focused && CaretKey(focused) != Seen(focused.Id).Caret;

    /// <summary>Where the caret is and what is selected, to notice either moving (a torn read only costs one extra event).</summary>
    private static (long, int, long, int, long, int) CaretKey(PaneView pane)
    {
        var t = pane.Rows.Terminal;
        var s = pane.Pane.Selection;
        return (t.ScreenTopLine + t.CursorY, t.CursorX, s?.AnchorLine ?? -1, s?.AnchorCol ?? -1, s?.HeadLine ?? -1, s?.HeadCol ?? -1);
    }

    private PaneSeen Seen(int paneId)
    {
        if (!_seen.TryGetValue(paneId, out var s)) _seen[paneId] = s = new PaneSeen();
        return s;
    }

    /// <summary>A client started listening: whatever happened before isn't news to it.</summary>
    private void ResetSeen()
    {
        _seen.Clear();
        _announcer.Clear();
        _focusedId = -1;
        _paneIds = "";
        _viewAt = long.MinValue / 2;
        foreach (var pane in _view.Panes)
        {
            var seen = Seen(pane.Id);
            lock (Gate) (seen.Version, seen.Caret) = (pane.Rows.Terminal.ContentVersion, CaretKey(pane));
        }
    }

    private void DropGoneProviders(WindowView view)
    {
        List<PaneProvider> gone = [];
        lock (_sync)
        {
            foreach (var (id, provider) in _providers)
                if (Array.FindIndex(view.Panes, p => p.Id == id) < 0) gone.Add(provider);
            foreach (var provider in gone) _providers.Remove(provider.PaneId);
        }
        foreach (var provider in gone)
        {
            _seen.Remove(provider.PaneId);
            Uia.Disconnect(provider);
        }
    }

    /// <summary>The window is going: every element is disconnected, UI Automation is told, and the window gets its own procedure back.</summary>
    public void Dispose()
    {
        List<object> providers;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            providers = [.. _providers.Values];
            if (_root is not null) providers.Add(_root);
            _providers.Clear();
        }
        _view = WindowView.Empty;
        if (_active)
        {
            foreach (var provider in providers) Uia.Disconnect(provider);
            Uia.UiaReturnRawElementProvider(Hwnd, 0, 0, 0);
        }
        UiaWindowHook.Remove(Hwnd);
    }

    /// <summary>What the main thread last told clients about a pane.</summary>
    private sealed class PaneSeen
    {
        public long Version = -1;
        public (long, int, long, int, long, int) Caret;
        public readonly OutputTracker Tracker = new();
    }
}
