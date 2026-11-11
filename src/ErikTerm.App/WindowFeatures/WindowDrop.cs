using System.Runtime.InteropServices;
using ErikTerm.App.Integrations;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Drag and drop (SDL_EVENT_DROP_*): files dropped on a pane are pasted there as shell-quoted paths separated by
/// spaces — a bracketed paste when the program asked for it, never with Enter — so Claude Code turns dropped images
/// into attachments and a shell gets arguments. While something is dragged over the window, the pane under the
/// pointer gets a dashed accent outline and a "Drop to paste path" hint. Dropped text (not file URIs) is pasted as
/// one line. Off with <c>drop_files = false</c>.
/// </summary>
internal sealed class WindowDrop(IFeatureWindow window, IPointerWindow pointer, IPaneInput input)
{
    private bool _dragging;
    private float _dropX = -1, _dropY = -1;
    private long _dropSeenAt;
    private Pane? _dropPane;
    private RectF _dropRect;
    private bool _dropGliding;
    private Tween _dropFade = new(0);
    private readonly List<string> _dropPaths = [];
    private string? _dropText;

    private const int DragStaleMs = 1500;

    /// <summary>Handle hook for SDL_EVENT_DROP_BEGIN/POSITION/FILE/TEXT/COMPLETE; <paramref name="density"/>: device pixels per point.</summary>
#pragma warning disable S6640 // Native interop: SDL hands the dropped path as a native UTF-8 pointer inside the event.
    public unsafe void OnDropEvent(ref Sdl.Event ev, float density, long now)
#pragma warning restore S6640
    {
        string? data = ev.Type is Sdl.EVENT_DROP_FILE or Sdl.EVENT_DROP_TEXT && ev.DropData is not null
            ? Marshal.PtrToStringUTF8((nint)ev.DropData) : null;
        HandleDrop(ev.Type, ev.DropX * density, ev.DropY * density, data, now);
    }

    private void HandleDrop(uint type, float x, float y, string? data, long now)
    {
        if (!window.Settings.DropFiles) { EndDrag(now); return; }
        switch (type)
        {
            case Sdl.EVENT_DROP_BEGIN:
                _dragging = true;
                _dropPaths.Clear();
                _dropText = null;
                _dropSeenAt = now;
                break;
            case Sdl.EVENT_DROP_POSITION:
                _dragging = true;
                SetDropPoint(x, y, now);
                break;
            case Sdl.EVENT_DROP_FILE:
                SetDropPoint(x, y, now);
                if (data is { Length: > 0 }) _dropPaths.Add(data);
                break;
            case Sdl.EVENT_DROP_TEXT:
                SetDropPoint(x, y, now);
                if (data is { Length: > 0 }) _dropText = _dropText is null ? data : _dropText + "\n" + data;
                break;
            case Sdl.EVENT_DROP_COMPLETE:
                var target = (_dropX >= 0 ? pointer.PaneAt(_dropX, _dropY) : null) ?? _dropPane ?? window.Focused;
                if (_dropPaths.Count > 0 || _dropText is not null) PasteDrop(target);
                EndDrag(now);
                break;
        }
        window.Redraw();
    }

    private void SetDropPoint(float x, float y, long now)
    {
        _dropSeenAt = now;
        if (x < 0 || y < 0) return;
        (_dropX, _dropY) = (x, y);
        var pane = pointer.PaneAt(x, y);
        if (pane is null || pane == _dropPane && _dropFade.Target > 0) return;
        _dropPane = pane;
        _dropFade.Set(1, now, window.Anim);
    }

    private void EndDrag(long now)
    {
        _dragging = false;
        _dropPaths.Clear();
        _dropText = null;
        _dropX = _dropY = -1;
        if (_dropFade.Target != 0) _dropFade.Set(0, now, window.Anim * 1.5f);
    }

    private void PasteDrop(Pane pane)
    {
        var paths = new List<string>(_dropPaths);
        string? flat = null;
        if (_dropText is not null)
        {
            var (uris, oneLine) = ShellQuote.FromDroppedText(_dropText);
            if (uris is not null) paths.AddRange(uris);
            else flat = oneLine;
        }
        string text = paths.Count > 0 ? ShellQuote.JoinPaths(paths) : flat ?? "";
        if (text.Length == 0) return;
        if (pane != window.Focused && window.Layout.Contains(pane)) window.Focus(pane);
        input.PasteText(pane, text, broadcast: true);
        int n = paths.Count;
        string headline = n switch
        {
            <= 0 => "Pasted dropped text",
            1 => "Pasted path",
            _ => $"Pasted {n} paths",
        };
        string? detail = n switch
        {
            1 => WindowGit.DisplayPath(paths[0]),
            > 1 => string.Join("  ", paths.Select(p => Path.GetFileName(p))),
            _ => null,
        };
        window.Toast(ToastKind.Success, headline, detail);
    }

    /// <summary>Tick hook: a drag that left without an event (some compositors) stops highlighting. <paramref name="layout"/>: the active tab's.</summary>
    public void Tick(long now, LayoutTree<Pane> layout)
    {
        if (_dragging && now - _dropSeenAt > DragStaleMs && _dropPaths.Count == 0 && _dropText is null) EndDrag(now);
        if (_dropPane is { } p && !layout.Contains(p)) { _dropPane = null; _dropFade.Snap(0); }
    }

    public int WakeMs(long now) => _dragging ? (int)Math.Max(1, _dropSeenAt + DragStaleMs - now + 1) : int.MaxValue;

    public bool IsAnimating(long now) => _dropFade.IsAnimating(now) || (_dropGliding && _dropFade.Target > 0);

    /// <summary>Overlay: a soft wash, a dashed accent outline and a hint pill on the pane under the drag.</summary>
    public void Draw(DrawList dl, Theme th, long now)
    {
        float v = _dropFade.Value(now);
        if (v <= 0.001f || _dropPane is not { } pane) return;
        RectF target = pane.Rect.Value(now);
        // Glide between panes as the pointer moves.
        _dropRect = _dropRect.W <= 0 || _dropFade.Target == 0 ? target : RectF.Lerp(_dropRect, target, 0.35f);
        _dropGliding = MathF.Abs(_dropRect.X - target.X) + MathF.Abs(_dropRect.Y - target.Y) + MathF.Abs(_dropRect.W - target.W) + MathF.Abs(_dropRect.H - target.H) > 0.5f;
        if (!_dropGliding) _dropRect = target;
        var r = new RectF(MathF.Round(_dropRect.X - th.S(3)), MathF.Round(_dropRect.Y - th.S(3)), MathF.Round(_dropRect.W + th.S(6)), MathF.Round(_dropRect.H + th.S(6)));

        dl.RoundRect(r, th.S(8), th.Accent, 0.07f * v);
        DashedOutline(dl, r, th.S(8), MathF.Max(1, MathF.Round(th.S(2))), th.S(9), th.S(6), th.Accent, 0.85f * v);

        float px = th.UiPx * 0.95f;
        const string hint = "Drop to paste path";
        float h = MathF.Round(px * 2.3f), padX = th.S(16), iconW = px * 1.8f;
        float w = MathF.Round(padX * 2 + iconW + dl.Measure(hint, px, FontStyle.Bold));
        if (w > r.W - th.S(16)) return;
        float rise = (1 - Ease.OutCubic(v)) * th.S(8);
        var pill = new RectF(MathF.Round(r.X + (r.W - w) / 2), MathF.Round(r.Y + (r.H - h) / 2 + rise), w, h);
        Widgets.FloatingPanel(dl, th, pill, h / 2, v);
        float b = Widgets.Baseline(dl, px, pill.Y, h);
        dl.Glyph(Icons.Paste, pill.X + padX, b, px * 1.05f, th.Accent, v);
        dl.Text(hint, pill.X + padX + iconW, b, px, th.PanelFg, v, FontStyle.Bold);
    }

    /// <summary>Dashes along a rectangle's straight edges, with solid rounded corners.</summary>
    private static void DashedOutline(DrawList dl, RectF r, float radius, float thick, float dash, float gap, uint rgb, float a)
    {
        float x0 = r.X + radius, x1 = r.Right - radius, y0 = r.Y + radius, y1 = r.Bottom - radius;
        for (float x = x0; x < x1; x += dash + gap)
        {
            float e = MathF.Min(x + dash, x1);
            dl.Rect(x, r.Y, e, r.Y + thick, rgb, a);
            dl.Rect(x, r.Bottom - thick, e, r.Bottom, rgb, a);
        }
        for (float y = y0; y < y1; y += dash + gap)
        {
            float e = MathF.Min(y + dash, y1);
            dl.Rect(r.X, y, r.X + thick, e, rgb, a);
            dl.Rect(r.Right - thick, y, r.Right, e, rgb, a);
        }
        // Corners: short arcs approximated by small dots.
        Span<(float Cx, float Cy, float Sx, float Sy)> corners = [(x0, y0, -1, -1), (x1, y0, 1, -1), (x0, y1, -1, 1), (x1, y1, 1, 1)];
        foreach (var (cx, cy, sx, sy) in corners)
            for (int i = 0; i <= 4; i++)
            {
                float t = i / 4f * MathF.PI / 2;
                float px = cx + sx * MathF.Sin(t) * (radius - thick / 2), py = cy + sy * MathF.Cos(t) * (radius - thick / 2);
                dl.RoundRect(new RectF(px - thick / 2, py - thick / 2, thick, thick), thick / 2, rgb, a);
            }
    }

    // ---- Debug script steps ----------------------------------------------------------------------

    /// <summary>
    /// ERIKTERM_DEBUG_SCRIPT steps: <c>dragover:COL,ROW</c> (drag over the focused pane's cell),
    /// <c>drop:COL,ROW:/a/b.png|/c d.txt</c> (drop files there), <c>droptext:COL,ROW:TEXT</c>, <c>dragleave</c>,
    /// <c>cellclick:COL,ROW</c> (left click on a cell; after ctrl:1 a Ctrl+click).
    /// Returns false for other step kinds.
    /// </summary>
    public bool RunScriptStep(string kind, string arg, long now)
    {
        switch (kind)
        {
            case "dragover":
            {
                var (x, y) = ScriptCell(arg);
                if (!_dragging) HandleDrop(Sdl.EVENT_DROP_BEGIN, 0, 0, null, now);
                HandleDrop(Sdl.EVENT_DROP_POSITION, x, y, null, now);
                _dropSeenAt = now + 60_000; // hold the highlight for screenshots
                return true;
            }
            case "drop" or "droptext":
                ScriptDrop(kind, arg, now);
                return true;
            case "cellclick": // a left click on a cell of the focused pane (with ctrl:1, a Ctrl+click on a link)
            {
                var (x, y) = ScriptCell(arg);
                pointer.ScriptClick(x, y, now);
                return true;
            }
            case "dragleave":
                HandleDrop(Sdl.EVENT_DROP_COMPLETE, -1, -1, null, now);
                return true;
        }
        return false;
    }

    private (float X, float Y) ScriptCell(string spec)
    {
        var parts = spec.Split(',');
        int col = int.Parse(parts[0]), row = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        var r = window.Focused.Target;
        return (r.X + (col + 0.5f) * window.CellWidth, r.Y + (row + 0.5f) * window.CellHeight);
    }

    private void ScriptDrop(string kind, string arg, long now)
    {
        int colon = arg.IndexOf(':');
        var (x, y) = ScriptCell(colon < 0 ? arg : arg[..colon]);
        string payload = colon < 0 ? "" : arg[(colon + 1)..];
        if (!_dragging) HandleDrop(Sdl.EVENT_DROP_BEGIN, 0, 0, null, now);
        if (kind == "drop")
            foreach (var p in payload.Split('|', StringSplitOptions.RemoveEmptyEntries)) HandleDrop(Sdl.EVENT_DROP_FILE, x, y, p, now);
        else HandleDrop(Sdl.EVENT_DROP_TEXT, x, y, System.Text.RegularExpressions.Regex.Unescape(payload), now);
        HandleDrop(Sdl.EVENT_DROP_COMPLETE, x, y, null, now);
    }
}
