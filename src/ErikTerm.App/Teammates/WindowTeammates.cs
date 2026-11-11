using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Tmux;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Teammates;

/// <summary>
/// Agent-team teammates as pills (tmux_agent_panes = "pills"). A coding agent opens a tmux split per teammate
/// (Tmux/TmuxTeammates.cs has the commands); erikterm puts the pane in the layout tree where the agent asked, but
/// parked: it takes no room (<see cref="ILayoutLeaf"/>), keeps running at the size it would have as that split (so
/// list-panes, capture-pane, send-keys, resize-pane … all answer as tmux would), and shows as a pill in a strip along
/// the bottom of its lead's pane — the lead's grid gives up those pixels and gets a real resize. Clicking a pill (or its
/// palette entry, or ToggleAgentPane(n)) opens the teammate as a split beside the lead; its own strip has its pill again,
/// whose ✕ parks it. kill-pane / the teammate exiting removes the pill. A teammate whose lead is gone (closed, moved to
/// another tab) opens as an ordinary split. Teammates aren't saved with the session.
/// <para>
/// Tabs are named by index, as in <see cref="IWindowPanes{TPane}"/>. TermWindow forwards its hook points here: layout,
/// the tmux requests, the pills' drawing and mouse, actions and the palette.
/// </para>
/// </summary>
internal sealed class WindowTeammates<TPane>(ITeammateWindow<TPane> window) where TPane : class, ITeammatePane<TPane>
{
    private readonly List<(TPane Pane, RectI Rect)> _parkedRects = [];
    private Teammate? _hoverPill;

    private static long Now => Environment.TickCount64;

    private bool Pills => window.Settings.TmuxAgentPanes == TmuxAgentPanes.Pills;

    // ==== Layout ===============================================================================

    // Cached delegates: Sync runs on every layout change.
    private static readonly Func<TPane, int> TeamPaneId = p => p.Id;
    private static readonly Func<TPane, Teammate?> PaneTeammate = p => p.Teammate;
    private static readonly Func<TPane, List<TPane>> PaneTeammates = p => p.Teammates;

    /// <summary>
    /// Rebuilds each lead's teammate list (layout order) for one tab. A teammate without its lead in the tab is an
    /// orphan and opens; with tmux_agent_panes = "split" every teammate shows. Runs before laying the tab out.
    /// </summary>
    public void Sync(LayoutTree<TPane> layout) => TeamRoster.Sync(layout.LeafList, TeamPaneId, PaneTeammate, PaneTeammates, Pills);

    /// <summary>Height of the strip at the bottom of a lead, which holds the pills of all its teammates, opened ones too.</summary>
    public int StripHeightFor(TPane pane, RectI r)
    {
        if (!Pills || pane.Teammates.Count == 0) return 0;
        int h = (int)MathF.Round(window.Theme.S(31));
        return h * 3 > r.H ? 0 : h; // a tiny pane keeps its rows
    }

    /// <summary>Parked panes: where they would be if they were shown (the layout with nothing hidden) — their reported tmux geometry and their PTY size.</summary>
    public void SizeParked(LayoutTree<TPane> layout, RectI bounds, bool background)
    {
        bool any = false;
        foreach (var p in layout.LeafList) any |= p.LayoutHidden;
        if (!any) return;
        layout.Layout(bounds, window.LayoutGap, _parkedRects, includeHidden: true);
        foreach (var (pane, r) in _parkedRects)
        {
            if (!pane.LayoutHidden) continue;
            pane.Target = r;
            pane.Rect.Snap(r);
            if (window.SizeTerminal(pane, r, 0)) window.ParkedResized(pane, background);
        }
        _parkedRects.Clear();
    }

    // ==== tmux requests ========================================================================

    /// <summary>
    /// Hook from IpcSplitPane: an agent opening a teammate's pane. It goes into the tree where asked, parked; nothing
    /// moves on screen and the focus stays. Null when it should be an ordinary split.
    /// </summary>
    public IpcResponse? AddSplit(IpcRequest r, int tabIndex, TPane target, SplitAxis axis, bool newFirst, double ratio)
    {
        if (!r.Tmux || !r.Teammate || !Pills) return null;
        var (first, _, second) = LayoutTree<TPane>.SplitRect(target.Target, axis, ratio, window.LayoutGap);
        var area = newFirst ? first : second;
        var pane = window.SpawnTeammate(r.Argv, r.Cwd ?? target.StartDirectory(window.Settings.DefaultCwd), area);
        pane.CreatedByTmux = true;
        var layout = window.TabLayout(tabIndex);
        pane.Teammate = new Teammate { LeadId = LeadFor(layout, target, r.CallerPaneId) };
        if (r.Argv is { } argv) ApplyArgs(pane, argv);
        layout.Split(target, pane, axis, newFirst, ratio);
        pane.Rect.Snap(area);
        pane.Focus.Snap(0);
        if (tabIndex == window.ActiveTabIndex) window.ApplyLayout(animate: true); // the lead's strip appears
        else window.ResizeBackgroundTabs();
        return new IpcResponse { Ok = true, PaneId = pane.Id };
    }

    /// <summary>The lead of a teammate split off <paramref name="target"/>: the calling pane (the agent's $TMUX_PANE), else the target's lead, else the target.</summary>
    private static int LeadFor(LayoutTree<TPane> layout, TPane target, int? callerId)
    {
        if (callerId is { } id)
            foreach (var p in layout.LeafList)
                if (p.Id == id && p.Teammate is null) return id;
        return target.Teammate is { LeadId: > 0 } t ? t.LeadId : target.Id;
    }

    /// <summary>
    /// Hook from IpcRespawnPane (before the new program starts): respawn-pane is where the agent starts the teammate. Its
    /// command line confirms a parked pane (name, team, color); a pane the split didn't flag is parked now; a flagged pane
    /// that gets something else after all opens.
    /// </summary>
    public void OnRespawn(IpcRequest r, int tabIndex, TPane pane, string[] argv)
    {
        var args = TmuxTeammates.Parse(argv);
        if (pane.Teammate is { } t)
        {
            if (args is not null) ApplyArgs(pane, argv);
            else if (!t.Confirmed)
            {
                pane.Teammate = null; // a placeholder `cat` pane after all — not a teammate
                AfterChange(tabIndex, animate: true);
            }
            return;
        }
        if (args is null || !r.Teammate || !pane.CreatedByTmux || !Pills) return;
        var layout = window.TabLayout(tabIndex);
        pane.Teammate = new Teammate { LeadId = LeadFor(layout, pane, r.CallerPaneId) };
        if (pane.Teammate.LeadId == pane.Id) { pane.Teammate = null; return; } // nobody to park it under
        ApplyArgs(pane, argv);
        bool active = tabIndex == window.ActiveTabIndex;
        if (active && window.Focused == pane && FirstShownExcept(layout, pane) is { } other) window.Focus(other);
        else if (!active && window.TabFocused(tabIndex) == pane && FirstShownExcept(layout, pane) is { } other2) window.SetTabFocused(tabIndex, other2);
        if (active && window.Zoomed == pane) window.SetZoom(null);
        AfterChange(tabIndex, animate: true);
    }

    private static TPane? FirstShownExcept(LayoutTree<TPane> layout, TPane except)
    {
        foreach (var p in layout.LeafList) if (p != except && !p.LayoutHidden) return p;
        return null;
    }

    private static void ApplyArgs(TPane pane, IReadOnlyList<string> argv)
    {
        if (pane.Teammate is not { } t || TmuxTeammates.Parse(argv) is not { } a) return;
        t.Confirmed = true;
        t.Name = a.Name;
        t.Team = a.Team ?? t.Team;
        t.AgentId = a.AgentId ?? t.AgentId;
        t.Color ??= a.Color;
    }

    /// <summary>set-pane-title (select-pane -T): names a teammate until its command line does.</summary>
    public void OnPaneTitle(TPane pane)
    {
        if (pane.Teammate is { Confirmed: false } t && pane.TmuxTitle is { Length: > 0 } title)
            t.Name = title.Length > 40 ? title[..40] : title;
    }

    /// <summary>set-pane-color: the color the agent gives a teammate (set-option -p pane-border-style fg=…).</summary>
    public IpcResponse IpcSetColor(TPane pane, IpcRequest r)
    {
        if (pane.Teammate is { } t && !string.IsNullOrEmpty(r.Color)) t.Color = r.Color.Length > 32 ? r.Color[..32] : r.Color;
        window.Redraw();
        return IpcResponse.Success();
    }

    private void AfterChange(int tabIndex, bool animate)
    {
        if (tabIndex == window.ActiveTabIndex) window.ApplyLayout(animate);
        else window.ResizeBackgroundTabs();
        window.Redraw();
    }

    /// <summary>
    /// select-layout from a lead with teammates arranges only the lead and its teammates (the smallest subtree with
    /// all of them), so the panes around it stay where they are. Null: apply it to the whole tab.
    /// </summary>
    public LayoutTree<TPane>.Node? TeamSubtree(LayoutTree<TPane> layout)
    {
        if (!Pills) return null;
        TPane? lead = null;
        foreach (var p in layout.LeafList)
            if (p.Teammates.Count > 0) { lead = p; break; }
        if (lead is null) return null;
        var members = new List<TPane>(lead.Teammates.Count + 1) { lead };
        members.AddRange(lead.Teammates);
        var node = layout.CommonAncestor(members);
        return node is null || ReferenceEquals(node, layout.Root) ? null : node;
    }

    // ==== Open / park ==========================================================================

    /// <summary>Shows a parked teammate as a split beside its lead (it grows out of its pill) and focuses it.</summary>
    public void Open(TPane pane)
    {
        if (pane.Teammate is not { Parked: true } t || !window.Layout.Contains(pane)) return;
        t.Parked = false;
        // Agents give teammates 70% of the width (tmux -l 70%, resize-pane -x 30% on the lead): next to the lead, a fair
        // half is nicer. Only the split between the two moves.
        if (window.FindPane(t.LeadId) is { } lead && window.Layout.CommonAncestor([lead, pane]) is { IsLeaf: false } split)
        {
            bool leadFirst = window.Layout.Find(lead) is { } ln && IsUnder(ln, split.First!);
            if (leadFirst ? split.Ratio < 0.5 : split.Ratio > 0.5) split.Ratio = 0.5;
        }
        if (window.Zoomed is not null) window.SetZoom(null);
        var from = t.PillRect.W > 0 ? t.PillRect : new RectF(pane.Target.X, pane.Target.Y, 1, 1);
        pane.Rect.Snap(from);
        pane.Appear.Snap(0);
        pane.Appear.Set(1, Now, window.Anim * 1.5f);
        pane.Focus.Snap(0);
        window.ApplyLayout(animate: true);
        window.Focus(pane);

        static bool IsUnder(LayoutTree<TPane>.Node n, LayoutTree<TPane>.Node ancestor)
        {
            for (var x = n; x is not null; x = x.Parent) if (ReferenceEquals(x, ancestor)) return true;
            return false;
        }
    }

    /// <summary>Hides an opened teammate again (it keeps running); the focus goes back to its lead.</summary>
    private void Park(TPane pane)
    {
        if (pane.Teammate is not { Parked: false, LeadId: > 0 } t || !Pills || !window.Layout.Contains(pane)) return;
        t.Parked = true;
        t.Hover.Snap(0);
        if (window.Zoomed == pane) window.DropZoom(Now);
        if (window.Focused == pane) window.Focus(window.FindPane(t.LeadId) is { } lead && window.Layout.Contains(lead) ? lead : TeamRoster.FirstShown(window.Layout) ?? pane);
        window.ApplyLayout(animate: true);
    }

    private void Toggle(TPane pane)
    {
        if (pane.LayoutHidden) Open(pane);
        else Park(pane);
    }

    /// <summary>The lead whose teammates the keyboard and palette act on: the focused pane's (or the focused teammate's) lead, else the tab's first.</summary>
    public TPane? CurrentLead()
    {
        var focused = window.Focused;
        if (focused.Teammates.Count > 0) return focused;
        var layout = window.Layout;
        if (focused.Teammate is { LeadId: > 0 } t && window.FindPane(t.LeadId) is { } lead && layout.Contains(lead)) return lead;
        foreach (var p in layout.LeafList) if (p.Teammates.Count > 0) return p;
        return null;
    }

    /// <summary>ToggleAgentPane(n): opens or parks the lead's n-th teammate (1-based); without n, parks the focused teammate or opens the first.</summary>
    public void RunToggleAction(string arg)
    {
        if (CurrentLead() is not { } lead)
        {
            window.Toast(ToastKind.Info, "No agent-team teammates here", "Claude Code's teammates show as pills below its pane");
            return;
        }
        if (int.TryParse(arg, out int n))
        {
            if (n >= 1 && n <= lead.Teammates.Count) Toggle(lead.Teammates[n - 1]);
            return;
        }
        var focused = window.Focused;
        if (focused.Teammate is { Parked: false, LeadId: > 0 }) Park(focused);
        else Toggle(lead.Teammates[0]);
    }

    /// <summary>Palette entries (hook from BuildCommands): one per teammate of the current lead.</summary>
    public void AddCommands(List<Command> list)
    {
        if (CurrentLead() is not { } lead) return;
        for (int i = 0; i < lead.Teammates.Count; i++)
        {
            var p = lead.Teammates[i];
            var t = p.Teammate!;
            string state = p.Agent.State == AgentState.None ? "" : $"  ({AgentStateText.Describe(p.Agent.State, p.Agent.Tracker.Kind)})";
            list.Add(new Command
            {
                Id = $"teammate:{p.Id}", Category = "Agents",
                Title = (t.Parked ? "Open teammate: " : "Park teammate: ") + t.Name + state,
                Icon = t.Parked ? Icons.SplitRight : Icons.Close,
                Shortcut = window.ShortcutFor(new ActionSpec("ToggleAgentPane", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                Run = () => { if (window.Layout.Contains(p)) Toggle(p); },
            });
        }
    }

    // ==== Drawing ==============================================================================

    private uint TeammateColor(Teammate t)
    {
        var s = window.Scheme;
        Span<uint> ansi = stackalloc uint[16];
        for (int i = 0; i < 8; i++) { ansi[i] = s.Ansi[i]; ansi[i + 8] = s.Brights[i]; }
        return TmuxTeammates.ColorValue(t.Color, ansi) ?? window.Theme.Accent;
    }

    /// <summary>Hook from DrawOverlays: the strips of the visible leads and opened teammates.</summary>
    public void DrawStrips(DrawList dl, long now)
    {
        if (!Pills) return;
        var th = window.Theme;
        foreach (var pane in window.VisiblePanes())
        {
            if (pane.StripPx <= 0) continue;
            var pr = pane.Rect.Value(now);
            float h = MathF.Min(pane.StripPx, pr.H);
            var strip = new RectF(pr.X, pr.Bottom - h, pr.W, h);
            float a = pane.Appear.Value(now);
            dl.Rect(strip, th.PanelBg, 0.55f * a);
            dl.Rect(strip with { H = MathF.Max(1, MathF.Round(th.S(1))) }, th.Split, 0.35f * a);
            dl.PushClip(strip);
            float x = strip.X + th.S(6) + MathF.Max(0, strip.W - th.S(12) - PillsWidth(dl, pane)); // right-aligned
            DrawPills(dl, pane.Teammates, x, strip, a, now);
            dl.PopClip();
        }
    }

    private void DrawPills(DrawList dl, List<TPane> list, float x, RectF strip, float alpha, long now)
    {
        var th = window.Theme;
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i].Teammate!;
            if (!DrawPill(dl, list[i], t, ref x, strip, alpha, now))
            {
                dl.Text(TeammateMorePills.Label(list.Count - i), x, Widgets.Baseline(dl, PillPx, strip.Y, strip.H), PillPx, th.Muted, alpha);
                for (int j = i; j < list.Count; j++) list[j].Teammate!.PillRect = default;
                break;
            }
        }
    }

    private float PillPx => window.Theme.UiPx * 0.78f;

    /// <summary>How wide this pane's pills are together, for right-aligning them (all of them; ones that don't fit start at the left).</summary>
    private float PillsWidth(DrawList dl, TPane pane)
    {
        float total = 0;
        var list = pane.Teammates;
        for (int i = 0; i < list.Count; i++) total += PillWidth(dl, list[i], list[i].Teammate!) + window.Theme.S(6);
        return MathF.Max(0, total - window.Theme.S(6));
    }

    private float PillWidth(DrawList dl, TPane pane, Teammate t)
    {
        var th = window.Theme;
        float px = PillPx;
        t.RefreshLabel(pane.Agent.State, pane.Agent.Tracker.Kind);
#pragma warning disable S1244 // A cache key: t.LabelPx is exactly the last px, and any difference means measure again.
        if (t.LabelPx != px)
#pragma warning restore S1244
        {
            t.LabelPx = px;
            t.LabelWidth = dl.Measure(t.Label, px);
        }
        return MathF.Round(th.S(9) + MathF.Round(th.S(7)) + th.S(6) + t.LabelWidth + th.S(7) + MathF.Round(px * 1.1f) + th.S(7));
    }

    /// <summary>One pill: status dot, name in the teammate's color, state, and ⊞ (open) / ✕ (park). False when it doesn't fit.</summary>
    private bool DrawPill(DrawList dl, TPane pane, Teammate t, ref float x, RectF strip, float alpha, long now)
    {
        var th = window.Theme;
        float px = PillPx;
        var state = pane.Agent.State;
        float w = PillWidth(dl, pane, t);
        float h = MathF.Min(MathF.Round(th.S(19)), MathF.Round(strip.H - th.S(7)));
        float dot = MathF.Round(th.S(7));
        float icon = MathF.Round(px * 1.1f);
        if (x + w > strip.Right - th.S(6))
        {
            t.PillRect = default;
            return false;
        }
        // More room above the pills than below: the line along the strip's top edge would otherwise crowd them.
        var r = new RectF(MathF.Round(x), MathF.Round(strip.Y + MathF.Min(th.S(8), MathF.Max(0, strip.H - h))), w, h);
        t.PillRect = r;
        x += w + th.S(6);

        uint tc = TeammateColor(t);
        bool open = !t.Parked;
        float hover = ReferenceEquals(_hoverPill, t) ? t.Hover.Value(now) : 0;
        uint bg = Colors.Mix(th.PanelBg, tc, open ? 0.26f + 0.08f * hover : 0.10f + 0.12f * hover);
        float borderMix = open ? 0.85f : 0.40f + 0.35f * hover;
        uint border = state == AgentState.Waiting ? th.Danger : Colors.Mix(th.PanelBg, tc, borderMix);
        dl.Panel(r, h / 2, bg, 0.96f * alpha, border, alpha);
        dl.PushClip(r);
        float cx = r.X + th.S(9) + dot / 2, cy = r.Y + h / 2;
        if (state == AgentState.None) dl.RoundRect(new RectF(MathF.Round(cx - dot / 2), MathF.Round(cy - dot / 2), dot, dot), dot / 2, tc, 0.9f * alpha);
        else AgentBadge.Dot(dl, th, state, cx, cy, dot, alpha, now);
        float tx = r.X + th.S(9) + dot + th.S(6);
        float b = Widgets.Baseline(dl, px, r.Y, h);
        // The name in the teammate's color, " · working" muted: one cached string, drawn in two colors.
        int nameLen = Math.Min(t.Name.Length, t.Label.Length);
        tx += DrawSpan(dl, t.Label, 0, nameLen, tx, b, px, Colors.Mix(th.PanelFg, tc, 0.55f), alpha);
        DrawSpan(dl, t.Label, nameLen, t.Label.Length - nameLen, tx, b, px, th.Muted, alpha);
        int glyph = open ? Icons.Close : Icons.SplitRight;
        float ga = open ? 0.55f + 0.45f * hover : 0.25f + 0.6f * hover;
        dl.Glyph(glyph, r.Right - th.S(7) - icon, b, px, open ? th.PanelFg : tc, ga * alpha);
        dl.PopClip();
        return true;
    }

    /// <summary>Draws text[start..start+length] without allocating.</summary>
    private static float DrawSpan(DrawList dl, string text, int start, int length, float x, float baseline, float px, uint rgb, float a)
    {
        float pen = x;
        int end = start + length;
        int i = start;
        while (i < end)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < end) cp = char.ConvertToUtf32(text[i], text[++i]);
            pen += dl.Glyph(cp, pen, baseline, px, rgb, a);
            i++;
        }
        return pen - x;
    }

    /// <summary>Hook from IsAnimating: pill hover fades.</summary>
    public bool IsAnimating(long now) => _hoverPill is { } t && t.Hover.IsAnimating(now);

    // ==== Input ================================================================================

    /// <summary>The pill under (x, y) on screen, and its pane.</summary>
    private (TPane Pane, Teammate Pill)? PillAt(float x, float y)
    {
        if (!Pills) return null;
        foreach (var pane in window.VisiblePanes())
        {
            if (pane.StripPx <= 0) continue;
            var t = pane.Target;
            if (y < t.Bottom - pane.StripPx || y >= t.Bottom || x < t.X || x >= t.Right) continue;
            if (PillIn(pane, x, y) is { } hit) return hit;
        }
        return null;
    }

    private static (TPane Pane, Teammate Pill)? PillIn(TPane pane, float x, float y)
    {
        foreach (var p in pane.Teammates)
        {
            var pill = p.Teammate!;
            if (pill.PillRect.W > 0 && pill.PillRect.Contains(x, y)) return (p, pill);
        }
        return null;
    }

    /// <summary>Whether (x, y) is on a teammate strip (clicks there don't reach the program or start a selection).</summary>
    private bool OnStrip(float x, float y)
    {
        if (!Pills) return false;
        foreach (var pane in window.VisiblePanes())
        {
            var t = pane.Target;
            if (pane.StripPx > 0 && x >= t.X && x < t.Right && y >= t.Bottom - pane.StripPx && y < t.Bottom) return true;
        }
        return false;
    }

    /// <summary>Hook from OnMouseMove: hovering a pill.</summary>
    public bool MouseMove(float x, float y, long now)
    {
        var hit = PillAt(x, y)?.Pill;
        if (!ReferenceEquals(hit, _hoverPill))
        {
            _hoverPill?.Hover.Set(0, now, window.Anim);
            _hoverPill = hit;
            if (hit is not null)
            {
                hit.Hover.Snap(0);
                hit.Hover.Set(1, now, window.Anim);
            }
            window.Redraw();
        }
        if (hit is not null) { window.PointerCursor(); return true; }
        if (OnStrip(x, y)) { window.DefaultCursor(); return true; }
        return false;
    }

    /// <summary>Hook from OnMouseDown: a click on a pill opens or parks that teammate.</summary>
    public bool MouseDown(byte button, float x, float y)
    {
        if (PillAt(x, y) is { } hit)
        {
            if (button == Sdl.BUTTON_LEFT) Toggle(hit.Pane);
            return true;
        }
        return OnStrip(x, y);
    }

    /// <summary>Debug script steps: <c>pillhover:N</c> moves the pointer onto the current lead's n-th pill (0: off it), <c>pillclick:N</c> clicks it.</summary>
    public bool RunScriptStep(string kind, string arg, long now, IScriptPointer pointer)
    {
        if (kind is not ("pillhover" or "pillclick")) return false;
        if (CurrentLead() is not { } lead || !int.TryParse(arg, out int n) || n < 0 || n > lead.Teammates.Count) return true;
        if (n == 0) { MouseMove(-1, -1, now); return true; }
        var r = lead.Teammates[n - 1].Teammate!.PillRect;
        if (r.W <= 0) return true;
        float x = r.X + r.W / 2, y = r.Y + r.H / 2;
        if (kind == "pillclick") pointer.ScriptClick(x, y, now); // moves there first, like the mouse would
        else pointer.ScriptMouseMove(x, y, now);
        return true;
    }
}

/// <summary>The "+N" labels for pills that don't fit; outside the generic class so there is one array.</summary>
internal static class TeammateMorePills
{
    private static readonly string[] Labels = ["+1", "+2", "+3", "+4", "+5", "+6", "+7", "+8", "+9"];

    public static string Label(int n) => n <= 9 ? Labels[n - 1] : "+…";
}
