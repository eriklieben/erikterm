using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Broadcast input (iTerm2's Broadcast Input, Windows Terminal's toggleBroadcastInput): what is typed or pasted in the
/// focused pane also goes to the tab's other panes — all of them, or the ones picked with the pane picker. Each pane's
/// program gets the key in its own encoding (application cursor mode, kitty protocol) and pastes in its own bracketing;
/// everything goes through the normal input queue. Parked or open teammates, exited panes and other tabs never receive
/// anything. A rim and a "Broadcast" pill mark every receiving pane; ways out: the pill, ToggleBroadcastInput
/// (Ctrl+Shift+Alt+I), the palette. Broadcasting ends by itself when fewer than two panes are left.
/// </summary>
internal sealed class WindowBroadcast(IWindowHost window, Lock gate, TabList<Tab> tabs, ContextMenu menu, PaneSelect paneSelect)
{
    private readonly List<Pane> _copies = [];
    private readonly List<RectF> _pills = [];
    private static readonly Func<Pane, bool> EligibleFn = Eligible;
    private string _label = "";
    private int _labelCount = -1;
    private bool _overPill;

    private static long Now => Environment.TickCount64;

    private static bool Eligible(Pane p) => !p.LayoutHidden && p.Teammate is null && !p.Exited && !p.Held;

    /// <summary>Panes that get a copy of the focused pane's input (a reused list; empty when not broadcasting).</summary>
    public List<Pane> Targets()
    {
        _copies.Clear();
        if (tabs.Active is { Broadcast: not BroadcastMode.Off } tab)
            BroadcastRouting.Targets(tab.Broadcast, window.Layout.LeafList, window.Focused, EligibleFn, tab.BroadcastPanes, _copies);
        return _copies;
    }

    public void Text(string text)
    {
        var targets = Targets();
        for (int i = 0; i < targets.Count; i++) window.SendInput(targets[i], text);
    }

    /// <summary>The key, encoded for each receiving pane's own modes. <paramref name="text"/>: the key's text (kitty's flag 16, or plain text keys).</summary>
    public void Key(uint key, uint scancode, ushort mod, KeyEventKind kind, string? text = null)
    {
        var targets = Targets();
        for (int i = 0; i < targets.Count; i++)
        {
            var pane = targets[i];
            bool appCursor;
            KittyKeyboardModes kitty;
            lock (gate) (appCursor, kitty) = (pane.Term.CursorKeysApplication, pane.Term.KeyboardFlags);
            if ((KeyEncoder.Encode(key, mod, appCursor, kitty, kind, KeyInputRouter.AlternatesFor(scancode, kitty), text) ?? text) is { Length: > 0 } seq) window.SendInput(pane, seq);
        }
    }

    // ---- Turning it on and off -------------------------------------------------------------------

    /// <summary>ToggleBroadcastInput: off when on; else all the tab's panes, or (Selected) the ones picked.</summary>
    public void Toggle(string arg)
    {
        if (tabs.Active is not { } tab) return;
        if (tab.Broadcast != BroadcastMode.Off) { Stop(tab); return; }
        if (arg.Equals("Selected", StringComparison.OrdinalIgnoreCase)) { OpenPicker(tab); return; }
        if (BroadcastRouting.GroupSize(BroadcastMode.AllPanes, window.Layout.LeafList, EligibleFn, null) < 2)
        {
            window.Toast(ToastKind.Info, "Nothing to broadcast to", "This tab has only one pane that takes input");
            return;
        }
        tab.Broadcast = BroadcastMode.AllPanes;
        tab.BroadcastPanes = null;
        window.Redraw();
    }

    private void OpenPicker(Tab tab)
    {
        var panes = window.PaneRects.Select(p => p.Pane).Where(Eligible).ToList();
        if (panes.Count < 2) { window.Toast(ToastKind.Info, "Nothing to broadcast to", "This tab has only one pane that takes input"); return; }
        long now = Now;
        menu.Close(now);
        paneSelect.OpenMulti(panes, window.Theme, now, chosen =>
        {
            if (chosen.Count < 2) { window.Toast(ToastKind.Info, "Pick at least two panes"); return; }
            tab.Broadcast = BroadcastMode.Selected;
            tab.BroadcastPanes = [.. chosen];
            if (!chosen.Contains(window.Focused)) window.Focus(chosen[0]);
            window.Redraw();
        });
        window.Toast(ToastKind.Info, "Pick the panes to broadcast to", "Press their letters, then Enter (Esc cancels)");
    }

    private void Stop(Tab tab)
    {
        tab.Broadcast = BroadcastMode.Off;
        tab.BroadcastPanes = null;
        window.Redraw();
    }

    /// <summary>Hook from Tick: panes closed until nothing is left to broadcast to.</summary>
    public void Tick()
    {
        if (tabs.Active is not { Broadcast: not BroadcastMode.Off } tab) return;
        if (BroadcastRouting.GroupSize(tab.Broadcast, window.Layout.LeafList, EligibleFn, tab.BroadcastPanes) >= 2) return;
        Stop(tab);
        window.Toast(ToastKind.Info, "Broadcast input off", "No other pane left to send to");
    }

    public void AddCommands(List<Command> list)
    {
        if (tabs.Active is not { } tab) return;
        bool on = tab.Broadcast != BroadcastMode.Off;
        bool enough = BroadcastRouting.GroupSize(BroadcastMode.AllPanes, window.Layout.LeafList, EligibleFn, null) >= 2;
        var all = new ActionSpec("ToggleBroadcastInput", null);
        list.Add(new Command
        {
            Id = "ToggleBroadcastInput", Category = "Pane", Title = on ? "Stop Broadcasting Input" : "Broadcast Input to All Panes in Tab",
            Icon = Icons.Broadcast, Action = all, Shortcut = window.ShortcutFor(all), IsEnabled = () => on || enough,
        });
        if (!on)
            list.Add(new Command
            {
                Id = "ToggleBroadcastInput(Selected)", Category = "Pane", Title = "Broadcast Input to Selected Panes…",
                Icon = Icons.Broadcast, Action = new ActionSpec("ToggleBroadcastInput", "Selected"), IsEnabled = () => enough,
            });
    }

    // ---- The indicator ---------------------------------------------------------------------------

    /// <summary>Hook from DrawOverlays: a rim and a pill on every pane that takes part. No allocation: the label changes with the pane count only.</summary>
    public void Draw(DrawList dl, Theme theme, LayoutTree<Pane> layout, long now)
    {
        _pills.Clear();
        if (tabs.Active is not { Broadcast: not BroadcastMode.Off } tab) return;
        int n = BroadcastRouting.GroupSize(tab.Broadcast, layout.LeafList, EligibleFn, tab.BroadcastPanes);
        if (n < 2) return;
        if (n != _labelCount)
        {
            _labelCount = n;
            _label = $"Broadcast · {n} panes";
        }
        var visible = window.VisiblePanes();
        for (int i = 0; i < visible.Count; i++)
        {
            var pane = visible[i];
            if (!Eligible(pane) || tab.Broadcast == BroadcastMode.Selected && tab.BroadcastPanes?.Contains(pane) != true) continue;
            DrawPane(dl, theme, pane.Rect.Value(now));
        }
    }

    private void DrawPane(DrawList dl, Theme theme, RectF r)
    {
        uint c = theme.Warning;
        float t = MathF.Max(1, MathF.Round(theme.S(2)));
        dl.RoundedOutline(r, theme.S(AgentBadge.RimRadius), t, c, 0.9f);

        float px = theme.UiPx * 0.8f;
        var m = dl.MetricsFor(px);
        float h = MathF.Round(m.Height + theme.S(10));
        float icon = px * 1.5f, close = px * 1.5f;
        float w = MathF.Round(theme.S(10) + icon + dl.Measure(_label, px, FontStyle.Bold) + theme.S(6) + close + theme.S(8));
        if (w > r.W - theme.S(16) || r.H < theme.S(40)) return; // too small for a pill: the rim says it
        var pill = new RectF(MathF.Round(r.X + (r.W - w) / 2), MathF.Round(r.Y + theme.S(8)), w, h);
        _pills.Add(pill);
        dl.Shadow(pill.Offset(0, theme.S(3)), h / 2, theme.S(8), 0.30f);
        dl.Panel(pill, h / 2, c, 0.95f, Colors.Mix(c, 0x000000, 0.25f), 1);
        float x = pill.X + theme.S(10), b = MathF.Round(pill.Y + (h - m.Height) / 2 + m.Ascender);
        uint fg = Colors.Luminance(c) > 0.5f ? 0x101010u : 0xFFFFFFu;
        x += dl.Glyph(Icons.Broadcast, x, b, px, fg, 1) + px * 0.5f;
        x += dl.Text(_label, x, b, px, fg, 1, FontStyle.Bold) + theme.S(6);
        dl.Glyph(Icons.Close, x, b, px, fg, 0.9f);
    }

    /// <summary>Hook from OnMouseMove: the pills are buttons.</summary>
    public bool MouseMove(float x, float y)
    {
        bool over = false;
        for (int i = 0; i < _pills.Count; i++) over |= _pills[i].Contains(x, y);
        if (over) window.SetCursor(window.CursorPointer);
        else if (_overPill) window.SetCursor(window.CursorDefault);
        _overPill = over;
        return over;
    }

    /// <summary>Hook from OnMouseDown: a click on a pill stops broadcasting.</summary>
    public bool MouseDown(float x, float y)
    {
        for (int i = 0; i < _pills.Count; i++)
        {
            if (!_pills[i].Contains(x, y) || tabs.Active is not { } tab) continue;
            Stop(tab);
            return true;
        }
        return false;
    }
}
