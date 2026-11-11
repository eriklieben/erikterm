using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.App.Windowing;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Panes;

/// <summary>
/// Scrolling a pane's history with the mouse. The overlay scrollbar under the mouse: it fades in when the pointer comes
/// near a pane's right edge, a click on a tick (prompt, failed command, search match, vim mark: ScrollMarks) scrolls
/// there, and the thumb can be dragged (a click elsewhere on the track jumps there first). Programs on the alternate
/// screen, or with mouse reporting on, keep that strip of the pane. Also the wheel's smooth / pixel scrolling and the
/// scrollbar's timed fade.
/// </summary>
internal sealed class WindowScrollbar(IWindowHost window, Lock gate, PaneRenderer renderer, WindowSearch search)
{
    private Pane? _drag;
    private float _grab;   // pointer offset from the thumb's top while dragging

    /// <summary>A thumb is held: mouse-up ends the drag (and is taken by it).</summary>
    public bool EndDrag()
    {
        if (_drag is null) return false;
        _drag = null;
        return true;
    }

    /// <summary>The pane closed or left the window (or the tab switched): a drag of its thumb ends.</summary>
    public void Forget(Pane? pane)
    {
        if (pane is null || _drag == pane) _drag = null;
    }

    /// <summary>An open search with matches keeps the scrollbar (and its ticks) visible.</summary>
    public bool ShowsMarks(Pane pane)
    {
        var bar = search.Bar;
        return bar.IsActive && ReferenceEquals(bar.Pane, pane) && bar.Engine.Matches.Count > 0;
    }

    /// <summary>The strip the scrollbar reacts in: its own width, plus a little to the left of the thumb and the ticks.</summary>
    private bool InStrip(Pane pane, float x, float y)
    {
        var r = pane.Target;
        return y >= r.Y && y < r.Bottom - pane.BottomPx && x < r.Right && x >= r.Right - window.Theme.S(14);
    }

    private bool HasScrollbar(Pane pane) =>
        pane.Term.ScrollbackCount > 0 && !pane.Term.IsAlternateScreen && !window.WantsMouse(pane);

    private ScrollbarGeometry GeometryOf(Pane pane)
    {
        var r = pane.Target;
        RectF rect = new(r.X, r.Y, r.W, Math.Max(0, r.H - pane.BottomPx));
        return ScrollbarGeometry.Of(rect, pane.Term.ScrollbackCount, pane.Term.Rows, pane.Term.ViewOffset, window.Theme.Scale);
    }

    /// <summary>Hook from OnMouseMove: near the edge the scrollbar shows. True when the pointer is in its strip.</summary>
    public bool Hover(float x, float y, long now)
    {
        if (window.PaneAt(x, y) is not { } pane || !InStrip(pane, x, y)) return false;
        lock (gate) if (!HasScrollbar(pane)) return false;
        pane.ScrollActivityAt = now;
        window.SetCursor(window.CursorDefault);
        return true;
    }

    /// <summary>Hook from OnMouseDown. True when the press was on a visible scrollbar: a tick scrolls there, the rest grabs the thumb.</summary>
    public bool MouseDown(float x, float y, long now)
    {
        if (window.PaneAt(x, y) is not { } pane || !InStrip(pane, x, y) || pane.Scrollbar.Value(now) < 0.2f) return false;
        ScrollMarkHit hit;
        bool onTick;
        ScrollbarGeometry g;
        lock (gate)
        {
            if (!HasScrollbar(pane)) return false;
            var t = pane.Term;
            g = GeometryOf(pane);
            Span<long> marks = stackalloc long[PaneRenderer.ScrollbarMarkCapacity];
            var (matches, _, markCount) = renderer.TickSources(pane, marks);
            int row = (int)MathF.Floor(y - g.TrackY), slop = (int)MathF.Round(window.Theme.S(4));
            onTick = pane.ScrollMarks.Find(row, slop, t.FirstLine, t.EndLine - t.FirstLine, matches, marks[..markCount], out hit);
            if (!onTick)
            {
                // On the thumb it keeps its place under the pointer; elsewhere on the track it jumps there, centred.
                _grab = y >= g.ThumbY && y < g.ThumbY + g.ThumbH ? y - g.ThumbY : g.ThumbH / 2;
                t.ScrollView(g.OffsetForThumbAt(y - _grab, t.ScrollbackCount) - t.ViewOffset);
            }
        }
        pane.ScrollActivityAt = now;
        window.Focus(pane);
        if (onTick) ScrollToMark(pane, hit.Line, hit.Kind, now);
        else _drag = pane;
        window.Redraw();
        return true;
    }

    /// <summary>Hook from OnMouseMove while the thumb is held.</summary>
    public bool MouseDrag(float y, long now)
    {
        if (_drag is not { } pane) return false;
        lock (gate)
        {
            var t = pane.Term;
            t.ScrollView(GeometryOf(pane).OffsetForThumbAt(y - _grab, t.ScrollbackCount) - t.ViewOffset);
        }
        pane.Scroll.Snap();
        pane.ScrollActivityAt = now;
        window.Redraw();
        return true;
    }

    /// <summary>A prompt comes to the top and flashes; a search match becomes the current match, centred; a vim mark goes a third down.</summary>
    private void ScrollToMark(Pane pane, long line, ScrollMarkKinds kind, long now)
    {
        if (kind == ScrollMarkKinds.Search)
        {
            SearchMatch? match;
            var bar = search.Bar;
            lock (gate)
            {
                var matches = bar.Engine.Matches;
                int i = bar.Engine.LowerBound(line);
                match = i < matches.Count ? matches[i] : null;
            }
            if (match is { } m)
            {
                bar.SetCurrent(m, now);
                search.AnimateViewTo(pane, m);
            }
            return;
        }
        lock (gate)
        {
            var t = pane.Term;
            t.ScrollViewToLine(kind == ScrollMarkKinds.Mark ? line - t.Rows / 3 : line);
        }
        pane.Scroll.Snap();
        pane.Links.FlashLine = line;
        pane.Links.Flash.Snap(1);
        pane.Links.Flash.Set(0, now, 700);
    }

    // ---- Smooth scrolling ----------------------------------------------------------------------

    /// <summary>Hook from OnWheel (history scrolling, not mouse reporting / alternate scroll).</summary>
    public void ScrollHistory(Pane pane, int notches, float delta, long now)
    {
        bool smooth = window.SmoothScrolling;
        lock (gate) pane.Scroll.Wheel(notches, delta, smooth, now);
        window.Redraw();
    }

    /// <summary>Hook from Tick: a paused trackpad gesture settles on a whole line.</summary>
    public void TickSmoothScroll(Pane pane, long now)
    {
        if (pane.Scroll.Fraction == 0) return;
        lock (gate) pane.Scroll.Tick(now);
        pane.ScrollActivityAt = now;
    }

    /// <summary>Hook from the event loop: wake when a scrollbar should start fading out (no frames until then).</summary>
    public static int WakeMs(LayoutTree<Pane> layout, long now)
    {
        int ms = int.MaxValue;
        foreach (var p in layout.LeafList)
        {
            long left = p.ScrollActivityAt + 900 - now;
            if (left > 0 && p.Scrollbar.Target > 0) ms = (int)Math.Min(ms, left + 1);
        }
        return ms;
    }
}
