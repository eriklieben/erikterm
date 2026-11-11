using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Search;

/// <summary>What <see cref="WindowPaneSearch{TPane}"/> needs from its window: TermWindow implements it, tests use a fake.</summary>
internal interface IPaneSearchWindow<TPane> : IWindowChrome where TPane : class
{
    Dictionary<KeyChord, ActionSpec> Keys { get; }
    /// <summary>Every pane of every window, the ones closest to the user first (App.PaneSearchTargets).</summary>
    List<PaneSearchTarget<TPane>> PaneSearchTargets();
    /// <summary>The focused pane's one-line selection, to start from; null when there is none.</summary>
    string? SelectionForSearch();
    /// <summary>Text of the clipboard (or primary selection, by the action's argument), for pasting into the field.</summary>
    string PasteText(string? arg);
    /// <summary>The list is opening: the pop-ups it replaces close.</summary>
    void CloseOverlaysForPaneSearch(long now);
    /// <summary>Shows the pane, in whichever window has it, with the match scrolled into view and outlined; false when the pane is gone.</summary>
    bool RevealSearchHit(TPane pane, PaneSearchQuery query, SearchMatch match);
}

/// <summary>
/// Search across all panes: one field, the scrollback and screen of every pane in every tab and window, matches grouped
/// by pane, and a jump that focuses the pane and scrolls to the match. The scan runs on a worker
/// (<see cref="PaneSearchScan{TPane}"/>); a new query cancels the one before it, and this class only ever polls the
/// worker's published results. TermWindow forwards its hook points here: tick, drawing, keys, text and mouse.
/// </summary>
internal sealed class WindowPaneSearch<TPane>(IPaneSearchWindow<TPane> window, Lock gate, Action<Action>? background = null, PaneSearchLimits? limits = null)
    where TPane : class
{
    private readonly PaneSearchOverlay<TPane> _overlay = new();
    private readonly Action<Action> _background = background ?? (work => Task.Run(work));
    private readonly PaneSearchLimits _limits = limits ?? PaneSearchLimits.Default;

    private PaneSearchScan<TPane>? _scan;
    private CancellationTokenSource? _cancel;
    private PaneSearchQuery _query;
    private int _seenRevision = -1;

    public bool IsOpen => _overlay.IsOpen;
    public bool IsAnimating(long now) => _overlay.IsAnimating(now);
    public void Draw(DrawList dl, Theme overlayTheme, RectF area, long now) => _overlay.Draw(dl, overlayTheme, area, now);

    /// <summary>Hook from Execute: SearchAllPanes.</summary>
    public void Open(long now)
    {
        window.CloseOverlaysForPaneSearch(now);
        _overlay.Open(window.Theme, now);
        if (window.SelectionForSearch() is { } selection) _overlay.SetQuery(selection);
        StartScan();
    }

    public void Close(long now)
    {
        _overlay.Close(now);
        CancelScan();
    }

    /// <summary>Hook from Tick: picks up what the worker has found since the last frame.</summary>
    public void Tick()
    {
        if (!_overlay.IsOpen || _scan is not { } scan || scan.Revision == _seenRevision) return;
        _seenRevision = scan.Revision;
        _overlay.SetResults(scan.Snapshot());
        window.Redraw();
    }

    /// <summary>Hook from OnKeyDown. True: the list is open and took the key.</summary>
    public bool KeyDown(uint key, ushort mod, long now)
    {
        if (!_overlay.IsOpen) return false;
        var result = _overlay.HandleKey(key, mod, now);
        if (result != PaneSearchKey.NotHandled) window.SuppressText = true;
        switch (result)
        {
            case PaneSearchKey.Close: Close(now); return true;
            case PaneSearchKey.Activate: Activate(now); return true;
            case PaneSearchKey.Handled: AfterInput(); return true;
        }
        if (KeyNames.Chord(key, mod) is { } chord && window.Keys.TryGetValue(chord, out var action))
        {
            window.SuppressText = true;
            if (action.Name == "SearchAllPanes") Close(now);
            else if (action.Name == "PasteFrom") { _overlay.Paste(window.PasteText(action.Arg), now); AfterInput(); }
        }
        return true; // printable keys arrive as text input; nothing reaches the terminal behind the list
    }

    /// <summary>Hook from OnTextInput.</summary>
    public bool TextInput(string text, long now)
    {
        if (!_overlay.IsOpen) return false;
        _overlay.HandleText(text, now);
        AfterInput();
        return true;
    }

    public bool MouseMove(float x, float y)
    {
        if (!_overlay.IsOpen) return false;
        _overlay.HandleMouseMove(x, y);
        window.Redraw();
        return true;
    }

    public bool MouseDown(float x, float y, long now)
    {
        if (!_overlay.IsOpen) return false;
        switch (_overlay.HandleMouseDown(x, y, now))
        {
            case PaneSearchKey.Close: Close(now); break;
            case PaneSearchKey.Activate: Activate(now); break;
            default: AfterInput(); break;
        }
        window.Redraw();
        return true;
    }

    public bool Wheel(int lines)
    {
        if (!_overlay.IsOpen) return false;
        _overlay.HandleWheel(lines);
        window.Redraw();
        return true;
    }

    private void AfterInput()
    {
        if (_overlay.ConsumeQueryChange()) StartScan();
        window.Redraw();
    }

    private void CancelScan()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
        _scan = null;
    }

    private void StartScan()
    {
        CancelScan();
        _query = _overlay.Query;
        _seenRevision = -1;
        if (_query.Pattern.Length == 0)
        {
            _overlay.SetResults(PaneSearchResults<TPane>.Empty);
            return;
        }
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        var scan = new PaneSearchScan<TPane>(gate, window.PaneSearchTargets(), _query, _limits, cancel.Token);
        _scan = scan;
        _background(scan.Run);
    }

    private void Activate(long now)
    {
        if (_overlay.SelectedHit is not { } selected) return;
        var (group, hit) = selected;
        PaneSearchJump check;
        lock (gate) check = PaneSearchScan<TPane>.Check(group.Target.Term, hit);
        if (check == PaneSearchJump.Stale)
        {
            Refresh("That match is gone: its line scrolled away or changed.");
            return;
        }

        var pane = group.Target.Pane;
        var query = _query;
        Close(now);
        if (!window.RevealSearchHit(pane, query, hit.Match))
        {
            window.Toast(ToastKind.Info, "That pane is gone");
            return;
        }
        if (check == PaneSearchJump.Hidden)
            window.Toast(ToastKind.Info, "The match is behind a full-screen program", "Quit it to scroll back to the match.");
    }

    private void Refresh(string message)
    {
        window.Toast(ToastKind.Info, message, "The list has been refreshed.");
        _overlay.SelectFirstOnNextResults();
        StartScan();
    }
}
