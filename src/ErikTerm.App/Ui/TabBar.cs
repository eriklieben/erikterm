using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ui;

/// <summary>
/// The slim bar of tabs along the top: rounded pills (index, title, × on hover), the active one in the
/// accent color, and a "+" button. Tabs grow in and shrink out, slide when reordered, can be dragged,
/// middle-clicked closed and double-clicked to rename inline. The caller syncs the model every frame
/// with <see cref="BeginSync"/> / <see cref="Set"/> / <see cref="EndSync"/> and reacts to the callbacks.
/// Dragging a tab well away from the bar detaches it (when <see cref="CanDetach"/> allows): a ghost of it follows
/// the pointer and letting go reports <see cref="DropOutside"/> (tear-off into a window of its own, or onto another
/// window's bar, which shows where it would land with <see cref="SetDropIndicator"/>).
/// A tab color (Tab Color, OSC 6) takes the accent's place for that tab: a tint, its number, and the active tab's outline.
/// When tabs can't all fit at the minimum width the bar becomes a window onto them (ErikTerm.Ui/TabStrip.cs): the active
/// tab stays in view, the wheel scrolls it, and "⌄ N more" beside the "+" opens the tab list.
/// OSC 9;4 progress shows as a hairline along the bottom of the tab (ProgressBar.cs).
/// </summary>
internal sealed class TabBar
{
    private sealed class Item
    {
        public int Id;
        public string Title = "";
        public string Number = "";
        public bool Active, Activity, Seen, Closing, Placed;
        public AgentState Agent; // coding-agent status dot (Agents/WindowAgents.cs)
        public Tween X = new(0), W = new(0), Hover = new(0), CloseHover = new(0), Lit = new(0);
        public RectF Rect, CloseRect;
        public string Shown = "", ShownSource = "";
        public float ShownWidth = -1;
        public Integrations.RepoBadgeModel? Badge; // git/GitHub badge (Ui/RepoBadge.cs)
        public uint? Color;              // tab color (Tabs/WindowTabColors.cs)
        public ProgressReport Progress;  // OSC 9;4, combined over the tab's panes (Windowing/WindowProgress.cs)
        public bool Attention;           // an agent waiting or something unread (the "more" button's dot)
        public bool Container;           // a pane's shell runs in a container (WindowFeatures/WindowContainers.cs): the badge before the title
    }

    private readonly List<Item> _items = [];
    private int _cursor, _liveCount;
    private Tween _visible = new(0), _plusX = new(0), _plusHover = new(0);
    private Tween _moreX = new(0), _moreFade = new(0), _moreHover = new(0);
    private bool _shown, _plusPlaced, _morePlaced;
    private float _animMs = 160;
    private RectF _bar, _plus, _more, _view;
    private static readonly string[] Numbers = ["1", "2", "3", "4", "5", "6", "7", "8", "9"];

    // Overflow: which tab comes first in the window onto them, and what last decided it.
    private int _first, _fit;
    private bool _overflowing, _moreAttention;
    private int _revealedId = -1, _revealedIndex = -1;
    private int _moreCount = -1;       // the hidden tabs _moreLabel counts
    private string _moreLabel = "";
    private int _moreReserveFor = -1;  // the tab count (and size, below) _moreReserve was measured for
    private float _moreReservePx, _moreReserve;
    private float _wheelCarry;         // fractions of a wheel notch (trackpads) not yet a step
    private long _edgeScrollAt;        // dragging a tab against the edge scrolls the bar at this rate

    // Pointer state.
    private float _mouseX, _mouseY;
    private bool _mouseInside;
    private int _pressId = -1;
    private float _pressX, _pressY, _grabDx, _grabDy;
    private bool _dragging, _detached;
    private int _dropIndex = -1; // another window's tab hovering over this bar: where it would go

    // Inline rename.
    private int _editId = -1;
    private string _editText = "";
    private bool _editSelected; // the whole title is selected: typing replaces it
    private long _caretResetAt;

    public Action<int>? Activate, Close;
    public Action? New;
    public Action<int, int>? Move;           // tab id, new index
    public Action<int, string?>? Rename;     // null = back to the automatic title
    /// <summary>Whether a dragged tab may leave the bar (another window to go to, or more than one tab here).</summary>
    public Func<bool>? CanDetach;
    /// <summary>A detached tab moved / was dropped (tab id, pointer in window pixels; may be outside the window).</summary>
    public Action<int, float, float>? DragOutside, DropOutside;
    /// <summary>Right click on a tab (tab id, pointer).</summary>
    public Action<int, float, float>? Menu;
    /// <summary>"⌄ N more" was clicked: the tab list, dropping down from this rectangle.</summary>
    public Action<RectF>? More;

    /// <summary>Indeterminate progress glides (else it stands still: animations off, or the window unfocused).</summary>
    public bool AnimateProgress;

    public bool IsEditing => _editId >= 0;
    public bool IsDragging => _dragging;
    public bool IsDetached => _detached;
    /// <summary>Where the last dragged tab was grabbed, relative to its top-left corner (pixels).</summary>
    public (float X, float Y) GrabOffset => (_grabDx, _grabDy);
    public bool IsVisible(long now) => _shown || _visible.Value(now) > 0.001f;

    public bool IsAnimating(long now)
    {
        if (_visible.IsAnimating(now) || _plusX.IsAnimating(now) || _plusHover.IsAnimating(now) || _dragging || _editId >= 0) return true;
        if (_moreX.IsAnimating(now) || _moreFade.IsAnimating(now) || _moreHover.IsAnimating(now)) return true;
        foreach (var it in _items)
            if (it.X.IsAnimating(now) || it.W.IsAnimating(now) || it.Hover.IsAnimating(now) || it.CloseHover.IsAnimating(now) || it.Lit.IsAnimating(now) || it.Closing)
                return true;
        return false;
    }

    /// <summary>Bar height in pixels (excluding the gap below it).</summary>
    public static float Height(Theme th) => MathF.Round(th.UiPx * 2.0f);

    // ---- Model sync -------------------------------------------------------------------------

    public void BeginSync(bool shown, Theme th, long now)
    {
        _animMs = th.AnimationMs;
        if (shown != _shown)
        {
            _shown = shown;
            _visible.Set(shown ? 1 : 0, now, _animMs * (shown ? 1.3f : 0.8f));
        }
        foreach (var it in _items) it.Seen = false;
        _cursor = 0;
        _liveCount = 0;
    }

    /// <summary>Declares the next tab in order. Title strings should be cached by the caller.</summary>
    public void Set(int id, string title, bool active, bool activity, long now, AgentState agent = AgentState.None, Integrations.RepoBadgeModel? badge = null,
        uint? color = null, ProgressReport progress = default, bool attention = false, bool container = false)
    {
        while (_cursor < _items.Count && _items[_cursor].Closing && _items[_cursor].Id != id) _cursor++;
        int index = -1;
        for (int i = _cursor; i < _items.Count; i++)
            if (_items[i].Id == id && !_items[i].Closing) { index = i; break; }
        Item it;
        if (index < 0)
        {
            it = new Item { Id = id };
            _items.Insert(_cursor, it);
        }
        else
        {
            it = _items[index];
            if (index != _cursor) { _items.RemoveAt(index); _items.Insert(_cursor, it); }
        }
        _cursor++;
        it.Seen = true;
        it.Title = title;
        it.Number = _liveCount < Numbers.Length ? Numbers[_liveCount] : "";
        _liveCount++;
        if (active != it.Active || !it.Placed) it.Lit.Set(active ? 1 : 0, now, _animMs);
        it.Active = active;
        it.Activity = activity;
        it.Agent = agent;
        it.Badge = badge;
        it.Color = color;
        it.Progress = progress;
        it.Attention = attention;
        it.Container = container;
    }

    public void EndSync(long now)
    {
        foreach (var it in _items)
        {
            if (it.Seen || it.Closing) continue;
            it.Closing = true;
            it.W.Set(0, now, _animMs * 1.1f);
            if (_editId == it.Id) _editId = -1;
        }
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Closing && !_items[i].W.IsAnimating(now)) _items.RemoveAt(i);
    }

    // ---- Layout -----------------------------------------------------------------------------

    private void Layout(DrawList dl, Theme th, RectF bar, long now)
    {
        _bar = bar;
        float gap = th.S(4);
        float plusW = MathF.Round(bar.H - th.S(4));
        float avail = bar.W - plusW - gap;
        float dur = _animMs * 1.2f;
        float px = th.UiPx * 0.88f;

        // The button's width is what its label needs for every tab being hidden, so the slot doesn't jump with the count.
        float reserve = MoreButtonWidth(dl, th, px, _liveCount);
        var strip = TabStrip.Layout(_liveCount, avail, gap, th.S(64), th.S(230), reserve, _first);
        float tabW = strip.TabWidth;
        _fit = strip.FitCount;
        _overflowing = strip.Overflowing;
        _first = strip.First;
        ScrollToActive(strip, bar, now);

        UpdateMoreFade(now);
        float x = PlaceTabs(bar.X - _first * (tabW + gap), tabW, gap, dur, now);

        // Tabs may end up partly under the viewport edge: the rest slides behind it.
        float contentRight = bar.X + strip.ViewportWidth;
        _view = new RectF(bar.X, bar.Y, strip.ViewportWidth, bar.H);
        PlaceButtons(bar, _overflowing ? contentRight + gap : x, reserve, plusW, gap, dur, now);
        UpdateTabRects(th, bar, _overflowing ? contentRight : x - gap, now);

        UpdateMoreLabel();
    }

    private void UpdateMoreFade(long now)
    {
        float target = _overflowing ? 1 : 0;
        if (!_morePlaced) _moreFade.Snap(target);
#pragma warning disable S1244 // Target only ever holds a value set here, so exact comparison is the point.
        else if (_moreFade.Target != target) _moreFade.Set(target, now, _animMs);
#pragma warning restore S1244
    }

    /// <summary>Sends every tab to its slot, left to right from <paramref name="x"/>; returns the pen after the last live one.</summary>
    private float PlaceTabs(float x, float tabW, float gap, float dur, long now)
    {
        foreach (var it in _items)
        {
            if (it.Closing)
            {
                if (!it.Placed) { it.X.Snap(x); it.Placed = true; }
                continue;
            }
            PlaceLiveTab(it, x, tabW, dur, now);
            x += tabW + gap;
        }
        return x;
    }

    /// <summary>A new tab grows in where it stands; one already placed glides to its slot and width.</summary>
    private static void PlaceLiveTab(Item it, float x, float tabW, float dur, long now)
    {
        if (!it.Placed)
        {
            it.X.Snap(x);
            it.W.Snap(0);
            it.W.Set(tabW, now, dur);
            it.Placed = true;
            return;
        }
#pragma warning disable S1244 // Target only ever holds a value set here, so exact comparison is the point.
        if (it.X.Target != x) it.X.Set(x, now, dur);
        if (it.W.Target != tabW) it.W.Set(tabW, now, dur);
#pragma warning restore S1244
    }

    private void PlaceButtons(RectF bar, float buttonsX, float reserve, float plusW, float gap, float dur, long now)
    {
        float moreX = buttonsX;
        if (_overflowing) buttonsX += reserve + gap;
        GlideTo(ref _moreX, ref _morePlaced, moreX, dur, now);
        _more = new RectF(MathF.Round(_moreX.Value(now)), bar.Y + (bar.H - plusW) / 2, reserve, plusW);

        GlideTo(ref _plusX, ref _plusPlaced, buttonsX, dur, now);
        _plus = new RectF(MathF.Round(_plusX.Value(now)), bar.Y + (bar.H - plusW) / 2, plusW, plusW);
    }

    private static void GlideTo(ref Tween tween, ref bool placed, float target, float dur, long now)
    {
        if (!placed) { tween.Snap(target); placed = true; }
#pragma warning disable S1244 // Target only ever holds a value set here, so exact comparison is the point.
        else if (tween.Target != target) tween.Set(target, now, dur);
#pragma warning restore S1244
    }

    /// <summary>Each tab's rectangle for this frame; a dragged tab follows the pointer, up to <paramref name="right"/>.</summary>
    private void UpdateTabRects(Theme th, RectF bar, float right, long now)
    {
        foreach (var it in _items)
        {
            float w = it.W.Value(now);
            float ix = it.X.Value(now);
            if (_dragging && !_detached && it.Id == _pressId)
                ix = Math.Clamp(_mouseX - _grabDx, bar.X, MathF.Max(bar.X, right - w));
            it.Rect = new RectF(MathF.Round(ix), bar.Y + th.S(3), MathF.Round(w), bar.H - th.S(6));
            float cs = MathF.Round(it.Rect.H - th.S(10));
            it.CloseRect = new RectF(it.Rect.Right - cs - th.S(5), it.Rect.Y + (it.Rect.H - cs) / 2, cs, cs);
        }
    }

    /// <summary>The text of "⌄ N more" is rebuilt only when N changes, so the idle frame stays allocation-free.</summary>
    private void UpdateMoreLabel()
    {
        int hidden = _overflowing ? Math.Max(0, _liveCount - _fit) : 0;
        if (hidden != _moreCount)
        {
            _moreCount = hidden;
            _moreLabel = hidden > 0 ? TabStrip.MoreLabel(hidden) : "";
        }
        _moreAttention = false;
        if (hidden == 0) return;
        Span<bool> flags = stackalloc bool[Math.Min(_liveCount, 256)];
        int n = 0;
        foreach (var it in _items)
        {
            if (it.Closing || n >= flags.Length) continue;
            flags[n++] = it.Attention;
        }
        _moreAttention = TabStrip.HiddenAttention(flags[..n], _first, _fit) > 0;
    }

    /// <summary>The button's width for the widest it could say (all tabs hidden); measured again only when the tab count or size changes.</summary>
    private float MoreButtonWidth(DrawList dl, Theme th, float px, int count)
    {
#pragma warning disable S1244 // Cache key: _moreReservePx is exactly the last px, and any change means measure again.
        if (count == _moreReserveFor && px == _moreReservePx) return _moreReserve;
#pragma warning restore S1244
        _moreReserveFor = count;
        _moreReservePx = px;
        float text = dl.Measure(TabStrip.MoreLabel(Math.Max(1, count)), px * 0.9f);
        var glyph = dl.Atlas.Get(Icons.ChevronDown, FontStyle.Regular, px);
        return _moreReserve = MathF.Round(th.S(8) + glyph.Advance + th.S(4) + text + th.S(9));
    }

    /// <summary>The window follows the active tab when it changes or moves; the wheel and a resize leave it where it is.</summary>
    private void ScrollToActive(TabStripLayout strip, RectF bar, long now)
    {
        int activeIndex = -1, activeId = -1, i = 0;
        foreach (var it in _items)
        {
            if (it.Closing) continue;
            if (it.Active) { activeIndex = i; activeId = it.Id; }
            i++;
        }

        if (_overflowing && _dragging && !_detached) DragEdgeScroll(bar, strip, now);
        else if (_overflowing && activeIndex >= 0 && (activeId != _revealedId || activeIndex != _revealedIndex))
            _first = TabStrip.Reveal(_first, activeIndex, _liveCount, _fit);
        if (activeIndex >= 0 && !_dragging) (_revealedId, _revealedIndex) = (activeId, activeIndex);
    }

    /// <summary>A tab held against the edge of an overflowing bar scrolls it, a tab at a time.</summary>
    private void DragEdgeScroll(RectF bar, TabStripLayout strip, long now)
    {
        const long IntervalMs = 240;
        if (now < _edgeScrollAt) return;
        float edge = strip.TabWidth * 0.35f;
        int delta = 0;
        if (_mouseX < bar.X + edge) delta = -1;
        else if (_mouseX > bar.X + strip.ViewportWidth - edge) delta = 1;
        if (delta == 0) return;
        _first = TabStrip.ClampFirst(_first + delta, _liveCount, _fit);
        _edgeScrollAt = now + IntervalMs;
    }

    // ---- Drawing ----------------------------------------------------------------------------

    public void Draw(DrawList dl, Theme th, RectF bar, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        float slide = (1 - Ease.OutCubic(v)) * th.S(-8);
        Layout(dl, th, bar.Offset(0, slide), now);
        UpdateHover(now);

        float px = th.UiPx * 0.88f;
        var m = dl.MetricsFor(px);
        bool clip = _overflowing;
        if (clip) dl.PushClip(_view);
#pragma warning disable S3267 // Runs every frame: no LINQ iterator or closure here.
        foreach (var it in _items)
            if (TabIsDrawn(it, th, clip)) DrawTab(dl, th, it, px, m, v, now);
#pragma warning restore S3267
        if (clip) dl.PopClip();

        if (_dropIndex >= 0) DrawDropIndicator(dl, th, v);
        if (_detached && _items.Find(i => i.Id == _pressId) is { } ghost) DrawGhost(dl, th, ghost, px, m, v);

        DrawPlus(dl, th, px, m, v, now);
        DrawMore(dl, th, px, m, v, now);
    }

    /// <summary>What the parts of one tab share while it is drawn.</summary>
    private readonly record struct TabPaint(float Px, FontSet.Metrics Metrics, float X, float Baseline, uint Accent, float Lit, float Hover, float Alpha, long Now);

    private bool TabIsDrawn(Item it, Theme th, bool clip)
    {
        var r = it.Rect;
        if (r.W < th.S(6)) return false;
        bool scrolledOut = r.Right <= _view.X || r.X >= _view.Right;
        bool held = _dragging && it.Id == _pressId;
        return !clip || !scrolledOut || held;
    }

    private static float TabAlpha(Item it, Theme th, float v, long now)
    {
        if (it.Closing) return v * Math.Clamp(it.Rect.W / th.S(120), 0, 1);
        return v * Math.Clamp(it.W.Value(now) / MathF.Max(1, it.W.Target > 0 ? it.W.Target : th.S(120)), 0, 1);
    }

    private void DrawTab(DrawList dl, Theme th, Item it, float px, FontSet.Metrics m, float v, long now)
    {
        var r = it.Rect;
        float a = TabAlpha(it, th, v, now);
        float lit = it.Lit.Value(now), hover = it.Hover.Value(now);
        bool dragged = _dragging && it.Id == _pressId;
        float radius = th.S(8);
        if (dragged && _detached)
        {
            // Its slot stays as a faint outline; the ghost (DrawGhost) follows the pointer.
            dl.RoundRect(r, radius, th.Accent, 0.45f * a);
            dl.RoundRect(r.Inset(MathF.Max(1, MathF.Round(th.S(1)))), radius - 1, th.Background, a);
            return;
        }

        if (dragged) dl.Shadow(r.Offset(0, th.S(4)), radius, th.S(12), 0.45f * a);
        uint accent = it.Color ?? th.Accent;
        DrawTabPill(dl, th, it, radius, accent, lit, hover, a);

        dl.PushClip(r);
        float baseline = MathF.Round(r.Y + (r.H - m.Height) / 2 + m.Ascender);
        float x = r.X + th.S(10);
        if (it.Number.Length > 0)
        {
            uint numberRgb = Colors.Mix(th.Muted, accent, it.Color is null ? lit : MathF.Max(lit, 0.8f));
            x += dl.Text(it.Number, x, baseline, px * 0.9f, numberRgb, a, FontStyle.Bold) + th.S(7);
        }
        if (it.Container) x += ContainerIcon.Draw(dl, th, x, baseline, px, lit, hover, a) + th.S(6);
        var paint = new TabPaint(px, m, x, baseline, accent, lit, hover, a, now);
        if (_editId == it.Id) DrawRenameField(dl, th, r, paint);
        else DrawTabTitle(dl, th, it, paint);
        if (it.Progress.IsActive)
        {
            // OSC 9;4: a hairline along the bottom, inside the rounded corners.
            float barH = MathF.Max(1, MathF.Round(th.S(2)));
            var track = new RectF(r.X + radius, r.Bottom - barH - MathF.Round(th.S(1.5f)), r.W - 2 * radius, barH);
            ProgressBar.Draw(dl, th, track, it.Progress, a, now, AnimateProgress, it.Color);
        }
        dl.PopClip();
    }

    /// <summary>Inactive: a faint pill that firms up on hover (tinted with its color, if it has one). Active: tinted with the accent (or its color) and outlined.</summary>
    private static void DrawTabPill(DrawList dl, Theme th, Item it, float radius, uint accent, float lit, float hover, float a)
    {
        var r = it.Rect;
        if (it.Color is { } tint) dl.RoundRect(r, radius, tint, (0.13f + 0.07f * hover) * (1 - lit) * a);
        else dl.RoundRect(r, radius, th.Foreground, (0.045f + 0.06f * hover) * (1 - lit) * a);
        if (lit > 0.001f)
            dl.Panel(r, radius, Colors.Mix(th.PanelBg, accent, it.Color is null ? 0.20f : 0.28f), 0.92f * lit * a, accent, (it.Color is null ? 0.55f : 0.8f) * lit * a);
    }

    private void DrawRenameField(DrawList dl, Theme th, RectF r, in TabPaint p)
    {
        float tw = dl.Measure(_editText, p.Px);
        float x = p.X, baseline = p.Baseline, a = p.Alpha;
        float shift = MathF.Max(0, tw - (r.Right - th.S(12) - x));
        dl.RoundRect(new RectF(x - th.S(4), r.Y + th.S(3), r.Right - th.S(6) - x + th.S(4), r.H - th.S(6)), th.S(5), th.PanelBg, 0.9f * a);
        if (_editSelected) dl.RoundRect(new RectF(x - shift - th.S(1), r.Y + th.S(6), tw + th.S(2), r.H - th.S(12)), th.S(3), th.Accent, 0.35f * a);
        dl.Text(_editText, x - shift, baseline, p.Px, th.PanelFg, a);
        if ((p.Now - _caretResetAt) % 1060 < 600)
            dl.Rect(new RectF(x - shift + tw + th.S(1), baseline - p.Metrics.Ascender + th.S(1), MathF.Max(1, th.S(2)), p.Metrics.Height - th.S(2)), th.Accent, a);
    }

    private static void DrawTabTitle(DrawList dl, Theme th, Item it, in TabPaint p)
    {
        var r = it.Rect;
        float reserve = it.CloseRect.W + th.S(6);
        float rightGap = it.Agent != AgentState.None ? th.S(14) : 0;
        if (p.Hover > 0.01f) rightGap = reserve;
        float textRight = r.Right - th.S(10) - rightGap;
        uint fg = Colors.Mix(Colors.Mix(th.PanelFg, th.Muted, 0.45f), th.PanelFg, MathF.Max(p.Lit, p.Hover * 0.6f));

        if (it.Badge is { } badge) textRight = DrawTabBadge(dl, th, it, badge, textRight, p);
        float maxW = MathF.Round(MathF.Max(0, textRight - p.X));
#pragma warning disable S1244 // Cache key: ShownWidth is exactly the last maxW, and any change means ellipsize again.
        if (maxW != it.ShownWidth || !ReferenceEquals(it.Title, it.ShownSource))
#pragma warning restore S1244
        {
            it.Shown = dl.Ellipsize(it.Title, p.Px, maxW);
            it.ShownWidth = maxW;
            it.ShownSource = it.Title;
        }
        dl.Text(it.Shown, p.X, p.Baseline, p.Px, fg, p.Alpha);
        DrawTabCorner(dl, th, it, p);
    }

    /// <summary>Git branch / changes / PR checks, right-aligned before the × or activity dot. Returns where the title now has to end.</summary>
    private static float DrawTabBadge(DrawList dl, Theme th, Item it, Integrations.RepoBadgeModel badge, float textRight, in TabPaint p)
    {
        float dot = it.Agent == AgentState.None && it.Activity && !it.Active && p.Hover <= 0.01f ? th.S(14) : 0; // the agent dot has its room in textRight
        float room = textRight - dot - p.X;
        float used = RepoBadge.DrawCompact(dl, th, badge, textRight - dot, p.Baseline, p.Px * 0.92f, p.Alpha * (0.75f + 0.25f * MathF.Max(p.Lit, p.Hover)),
            MathF.Max(room * 0.45f, room - dl.Measure(it.Title, p.Px) - th.S(12)), p.Now, animate: true);
        return used > 0 ? textRight - (used + dot + th.S(8)) : textRight;
    }

    /// <summary>× on hover; an agent's dot or an activity dot in its place otherwise.</summary>
    private static void DrawTabCorner(DrawList dl, Theme th, Item it, in TabPaint p)
    {
        var r = it.Rect;
        if (p.Hover > 0.01f)
        {
            float closeH = it.CloseHover.Value(p.Now);
            var cr = it.CloseRect;
            dl.RoundRect(cr, cr.W / 2, th.Danger, 0.22f * closeH * p.Alpha);
            var g = dl.Atlas.Get(Icons.Close, FontStyle.Regular, p.Px * 0.9f);
            float gb = MathF.Round(cr.Y + (cr.H - p.Metrics.Height) / 2 + p.Metrics.Ascender);
            dl.Glyph(Icons.Close, cr.X + (cr.W - g.Advance) / 2, gb, p.Px * 0.9f, Colors.Mix(th.Muted, th.PanelFg, closeH), p.Hover * p.Alpha);
        }
        else if (it.Agent != AgentState.None)
        {
            // An agent's state: waiting (red), working (pulsing yellow), done and unseen (blue), idle (green).
            float d = MathF.Round(th.S(7));
            AgentBadge.Dot(dl, th, it.Agent, r.Right - th.S(12), r.Y + r.H / 2, d, p.Alpha, p.Now);
        }
        else if (it.Activity && !it.Active)
        {
            float d = MathF.Round(th.S(6));
            dl.RoundRect(new RectF(r.Right - th.S(12) - d / 2, r.Y + (r.H - d) / 2, d, d), d / 2, p.Accent, p.Alpha);
        }
    }

    /// <summary>"+": a new tab.</summary>
    private void DrawPlus(DrawList dl, Theme th, float px, FontSet.Metrics m, float v, long now)
    {
        float ph = _plusHover.Value(now);
        dl.RoundRect(_plus, th.S(8), th.Foreground, (0.03f + 0.08f * ph) * v);
        var pg = dl.Atlas.Get(Icons.Add, FontStyle.Regular, px);
        dl.Glyph(Icons.Add, _plus.X + (_plus.W - pg.Advance) / 2, MathF.Round(_plus.Y + (_plus.H - m.Height) / 2 + m.Ascender), px,
            Colors.Mix(th.Muted, th.PanelFg, ph), v);
    }

    /// <summary>"⌄ N more": the tabs out of view, with a dot when one of them wants attention.</summary>
    private void DrawMore(DrawList dl, Theme th, float px, FontSet.Metrics m, float v, long now)
    {
        float fade = _moreFade.Value(now);
        if (fade <= 0.002f || _moreLabel.Length == 0) return;
        float a = v * fade;
        float hover = _moreHover.Value(now);
        dl.RoundRect(_more, th.S(8), th.Foreground, (0.03f + 0.08f * hover) * a);
        float baseline = MathF.Round(_more.Y + (_more.H - m.Height) / 2 + m.Ascender);
        var g = dl.Atlas.Get(Icons.ChevronDown, FontStyle.Regular, px);
        float x = _more.X + th.S(8);
        uint rgb = Colors.Mix(th.Muted, th.PanelFg, hover);
        dl.Glyph(Icons.ChevronDown, x, baseline, px, rgb, a);
        x += g.Advance + th.S(4);
        dl.Text(_moreLabel, x, baseline, px * 0.9f, rgb, a);
        if (_moreAttention)
        {
            float d = MathF.Round(th.S(7));
            dl.RoundRect(new RectF(_more.Right - d - th.S(3), _more.Y + th.S(3), d, d), d / 2, AgentBadge.Color(th, AgentState.Waiting), a);
        }
    }

    /// <summary>The detached tab, following the pointer (inside this window; App's popup ghost shows it outside).</summary>
    private void DrawGhost(DrawList dl, Theme th, Item it, float px, FontSet.Metrics m, float v)
    {
        var r = new RectF(MathF.Round(_mouseX - _grabDx), MathF.Round(_mouseY - _grabDy), it.Rect.W, it.Rect.H);
        DrawPill(dl, th, r, it.Number, it.Shown.Length > 0 ? it.Shown : it.Title, px, m, v);
    }

    /// <summary>A lifted tab: the ghost while dragging (here, and in the popup that follows the pointer outside the window).</summary>
    public static void DrawPill(DrawList dl, Theme th, RectF r, string number, string title, float px, FontSet.Metrics m, float v)
    {
        float radius = th.S(8);
        dl.Shadow(r.Offset(0, th.S(6)), radius, th.S(16), 0.5f * v);
        dl.Panel(r, radius, Colors.Mix(th.PanelBg, th.Accent, 0.20f), 0.95f * v, th.Accent, 0.8f * v);
        dl.PushClip(r);
        float baseline = MathF.Round(r.Y + (r.H - m.Height) / 2 + m.Ascender);
        float x = r.X + th.S(10);
        if (number.Length > 0) x += dl.Text(number, x, baseline, px * 0.9f, th.Accent, v, FontStyle.Bold) + th.S(7);
        dl.Text(title, x, baseline, px, th.PanelFg, v);
        dl.PopClip();
    }

    /// <summary>The detached tab: number, title, size and where it was grabbed (for the popup ghost).</summary>
    public (string Number, string Title, float W, float H, float GrabX, float GrabY)? Ghost =>
        _detached && _items.Find(i => i.Id == _pressId) is { } it ? (it.Number, it.Title, it.Rect.W, it.Rect.H, _grabDx, _grabDy) : null;

    private void DrawDropIndicator(DrawList dl, Theme th, float v)
    {
        // A bar in the gap before the tab at _dropIndex (or after the last one).
        float x = _bar.X, top = _bar.Y + th.S(3), h = _bar.H - th.S(6);
        int n = 0;
        foreach (var it in _items)
        {
            if (it.Closing) continue;
            if (n++ == _dropIndex) { x = it.Rect.X - th.S(2); break; }
            x = it.Rect.Right + th.S(2);
        }
        float w = MathF.Max(2, MathF.Round(th.S(3)));
        if (_overflowing) x = Math.Clamp(x, _view.X, _view.Right);
        dl.RoundRect(new RectF(MathF.Round(x - w / 2), top - th.S(2), w, h + th.S(4)), w / 2, th.Accent, v);
    }

    /// <summary>Where a tab dropped at <paramref name="x"/> would go: the number of tabs whose centre is left of it.</summary>
    public int InsertionIndex(float x)
    {
        int index = 0;
        foreach (var it in _items)
            if (!it.Closing && it.Rect.X + it.Rect.W / 2 < x) index++;
        return index;
    }

    /// <summary>Shows (index &gt;= 0) or hides (-1) where a tab from another window would land. True when it changed.</summary>
    public bool SetDropIndicator(int index)
    {
        if (index == _dropIndex) return false;
        _dropIndex = index;
        return true;
    }

    private void UpdateHover(long now)
    {
        var (hoverId, closeId) = TabUnderPointer();
        bool pointerFree = _mouseInside && !_dragging;
        bool plus = pointerFree && _plus.Contains(_mouseX, _mouseY);
        bool more = pointerFree && MoreShown && _more.Contains(_mouseX, _mouseY);
        float d = _animMs * 0.7f;
        foreach (var it in _items)
        {
            AimHover(ref it.Hover, it.Id == hoverId || (_dragging && it.Id == _pressId) ? 1 : 0, d, now);
            AimHover(ref it.CloseHover, it.Id == closeId ? 1 : 0, d, now);
        }
        AimHover(ref _plusHover, plus ? 1 : 0, d, now);
        AimHover(ref _moreHover, more ? 1 : 0, d, now);
    }

    /// <summary>The tab under the pointer and the tab whose × is under it; -1 for none, or while a tab is being dragged.</summary>
    private (int HoverId, int CloseId) TabUnderPointer()
    {
        int hoverId = -1, closeId = -1;
        if (!_mouseInside || _dragging) return (hoverId, closeId);
        foreach (var it in _items)
        {
            if (it.Closing || !it.Rect.Contains(_mouseX, _mouseY) || !InView(_mouseX)) continue;
            hoverId = it.Id;
            if (it.CloseRect.Contains(_mouseX, _mouseY)) closeId = it.Id;
        }
        return (hoverId, closeId);
    }

    private static void AimHover(ref Tween tween, float target, float d, long now)
    {
#pragma warning disable S1244 // Target only ever holds a value set here, so exact comparison is the point.
        if (tween.Target != target) tween.Set(target, now, d);
#pragma warning restore S1244
    }

    // ---- Pointer ----------------------------------------------------------------------------

    private bool InView(float x) => !_overflowing || (x >= _view.X && x < _view.Right);
    private bool MoreShown => _overflowing && _moreLabel.Length > 0;

    private Item? ItemAt(float x, float y)
    {
        if (!InView(x)) return null;
        foreach (var it in _items)
            if (!it.Closing && it.Rect.Contains(x, y)) return it;
        return null;
    }

    /// <summary>The wheel over an overflowing bar shifts the window; false lets the pane underneath have it.</summary>
    public bool HandleWheel(int lines, float precise, float x = -1, float y = -1)
    {
        if (!_overflowing || !(x < 0 || Contains(x, y))) return false;
        _wheelCarry += precise != 0 ? precise : lines;
        int steps = (int)_wheelCarry;
        if (steps == 0) return true;
        _wheelCarry -= steps;
        _first = TabStrip.ClampFirst(_first - steps, _liveCount, _fit);
        return true;
    }

    public bool Contains(float x, float y) => _shown && _bar.Contains(x, y);

    /// <summary>
    /// True when (x, y) is on the bar but on none of its controls — the leftover strip that a borderless window
    /// can be dragged by (Chrome/WindowHitTest.cs). Dragging a tab or renaming one owns the pointer instead, or the
    /// window would move out from under the gesture.
    /// </summary>
    public bool IsEmptyArea(float x, float y) =>
        Contains(x, y) && !_dragging && _editId < 0 && ItemAt(x, y) is null && !_plus.Contains(x, y) && !(MoreShown && _more.Contains(x, y));

    /// <summary>Centre of the "more" button (debug scripts click it); null while the bar isn't overflowing.</summary>
    public (float X, float Y)? MoreCenter => MoreShown ? (_more.X + _more.W / 2, _more.Y + _more.H / 2) : null;

    /// <summary>Centre of the n-th tab as last drawn (debug scripts hover it).</summary>
    public (float X, float Y)? CenterOf(int index)
    {
        int n = 0;
        foreach (var it in _items)
        {
            if (it.Closing) continue;
            if (n++ == index) return (it.Rect.X + it.Rect.W * 0.4f, it.Rect.Y + it.Rect.H / 2);
        }
        return null;
    }

    /// <summary>Returns true when the pointer is over the bar or dragging a tab.</summary>
    public bool HandleMouseMove(float x, float y)
    {
        _mouseX = x;
        _mouseY = y;
        _mouseInside = Contains(x, y);
        if (_pressId >= 0 && !_dragging && (MathF.Abs(x - _pressX) > 6 || MathF.Abs(y - _pressY) > 6)) BeginDrag();
        if (!_dragging) return _mouseInside;

        UpdateDetached(x, y);
        if (_detached) DragOutside?.Invoke(_pressId, x, y);
        else ReorderDragged(x);
        return true;
    }

    private void BeginDrag()
    {
        _dragging = true;
        if (_items.Find(i => i.Id == _pressId) is { } grabbed) (_grabDx, _grabDy) = (_pressX - grabbed.Rect.X, _pressY - grabbed.Rect.Y);
    }

    /// <summary>Pulled well away from the bar (a bar's height above or below it, or past its ends): detach; back near it: reattach.</summary>
    private void UpdateDetached(float x, float y)
    {
        if (!_detached && IsAwayFromBar(x, y) && (CanDetach?.Invoke() ?? false)) _detached = true;
        else if (_detached && IsNearBar(x, y)) _detached = false;
    }

    private bool IsAwayFromBar(float x, float y)
    {
        float slop = _bar.H;
        return y < _bar.Y - slop || y > _bar.Bottom + slop * 1.2f || x < _bar.X - slop || x > _bar.Right + slop;
    }

    private bool IsNearBar(float x, float y)
    {
        float slop = _bar.H;
        return y >= _bar.Y - slop / 2 && y <= _bar.Bottom + slop / 2 && x >= _bar.X - slop / 2 && x <= _bar.Right + slop / 2;
    }

    /// <summary>Reorder once the dragged tab's centre passes a neighbour's centre.</summary>
    private void ReorderDragged(float x)
    {
        var live = _items.Where(i => !i.Closing).ToList();
        int from = live.FindIndex(i => i.Id == _pressId);
        if (from < 0) return;
        float center = x - _grabDx + live[from].Rect.W / 2;
        int to = 0;
        for (int i = 0; i < live.Count; i++)
            if (i != from && live[i].X.Target + live[i].W.Target / 2 < center) to++;
        if (to != from) Move?.Invoke(_pressId, to);
    }

    public void HandleMouseLeave() => _mouseInside = false;

    /// <summary>Handles a click on the bar; false when it wasn't on the bar.</summary>
    public bool HandleMouseDown(byte button, int clicks, float x, float y, long now)
    {
        if (!Contains(x, y))
        {
            if (_editId >= 0) CommitEdit();
            return false;
        }
        var it = ItemAt(x, y);
        if (_editId >= 0 && it?.Id != _editId) CommitEdit();
        if (button != Sdl.BUTTON_LEFT) ClickWithOtherButton(button, it, x, y);
        else if (it is null) ClickEmptyBar(clicks, x, y);
        else ClickTab(it, clicks, x, y, now);
        return true;
    }

    /// <summary>The middle button closes a tab, the right one opens its menu.</summary>
    private void ClickWithOtherButton(byte button, Item? it, float x, float y)
    {
        if (it is null) return;
        if (button == Sdl.BUTTON_MIDDLE) Close?.Invoke(it.Id);
        else if (button == Sdl.BUTTON_RIGHT) Menu?.Invoke(it.Id, x, y);
    }

    private void ClickEmptyBar(int clicks, float x, float y)
    {
        if (MoreShown && _more.Contains(x, y)) { More?.Invoke(_more); return; }
        if (_plus.Contains(x, y) || clicks >= 2) New?.Invoke();
    }

    private void ClickTab(Item it, int clicks, float x, float y, long now)
    {
        if (it.CloseRect.Contains(x, y)) { Close?.Invoke(it.Id); return; }
        if (clicks >= 2) { BeginRename(it.Id, it.Title, now); return; }
        Activate?.Invoke(it.Id);
        _pressId = it.Id;
        _pressX = x;
        _pressY = y;
    }

    public bool HandleMouseUp(float x, float y, long now)
    {
        bool was = _pressId >= 0;
        int id = _pressId;
        if (_detached)
        {
            _dragging = _detached = false;
            _pressId = -1;
            DropOutside?.Invoke(id, x, y); // may move the tab to another window
            return true;
        }
        if (_dragging && _items.Find(i => i.Id == _pressId) is { } it)
        {
            // Settle from where it was dropped into its slot.
            float target = it.X.Target;
            it.X.Snap(it.Rect.X);
            it.X.Set(target, now, _animMs);
        }
        _dragging = false;
        _pressId = -1;
        return was;
    }

    // ---- Inline rename ----------------------------------------------------------------------

    public void BeginRename(int id, string current, long now)
    {
        _editId = id;
        _editText = current;
        _editSelected = current.Length > 0;
        _caretResetAt = now;
    }

    private void CommitEdit()
    {
        int id = _editId;
        _editId = -1;
        string text = _editText.Trim();
        Rename?.Invoke(id, text.Length == 0 ? null : text);
    }

    public void HandleText(string text, long now)
    {
        if (_editId < 0) return;
        if (_editSelected) { _editText = ""; _editSelected = false; }
        _editText += text.Replace("\n", "").Replace("\r", "");
        _caretResetAt = now;
    }

    /// <summary>Keys while renaming (always consumed).</summary>
    public void HandleKey(uint key, ushort mod, long now)
    {
        _caretResetAt = now;
        switch (key)
        {
            case Sdl.K_ESCAPE: _editId = -1; break;
            case Sdl.K_LEFT or Sdl.K_RIGHT or Sdl.K_HOME or Sdl.K_END: _editSelected = false; break; // caret to the end
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: CommitEdit(); break;
            case Sdl.K_BACKSPACE:
                bool all = (mod & Sdl.KMOD_CTRL) != 0 || _editSelected;
                _editSelected = false;
                if (all) _editText = "";
                else if (_editText.Length > 0) _editText = _editText[..^(char.IsLowSurrogate(_editText[^1]) && _editText.Length > 1 ? 2 : 1)];
                break;
        }
    }
}
