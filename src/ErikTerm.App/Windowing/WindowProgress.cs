using ErikTerm.App.Rendering;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// OSC 9;4 progress (ConEmu / Windows Terminal: <c>ESC ] 9 ; 4 ; state ; percent ST</c>, state 0 remove, 1 normal,
/// 2 error, 3 indeterminate, 4 paused), per pane. Each tab shows its panes' reports combined (<see cref="ProgressAggregate"/>)
/// as a hairline along the bottom of its tab; when the tab bar can't tell the panes apart — several on screen — or isn't
/// shown, each reporting pane also gets a hairline along its top edge. Error is red, paused yellow, indeterminate a
/// gliding segment, drawn at <see cref="ProgressBar.FrameMs"/> only while one is on screen in a focused window (still
/// otherwise, and with animations off), so an idle window stays at 0 fps. A report goes when the program clears it, at
/// the next shell prompt (OSC 133;A), on RIS and when the pane's program exits.
/// </summary>
internal sealed class WindowProgress(IWindowHost window, Lock gate, TabList<Tab> tabs, WindowTabColors<Tab> tabColors)
{
    private bool _pulse;
    private long _frameAt;

    /// <summary>Indeterminate progress glides only with animations on and the window focused.</summary>
    public bool AnimateProgress => window.AnimationMs > 0 && window.IsWindowFocused;

    /// <summary>Panes draw their own bar when the tab bar can't tell them apart (several on screen) or isn't shown.</summary>
    private bool PaneBarsShown => window.Zoomed is null && window.ShownPaneCount > 1 || !window.TabBarShown;

    /// <summary>Hook from Tick: every pane's report (none once its program exited), combined per tab; frames for a glide.</summary>
    public void Tick(long now)
    {
        window.SyncTabs();
        bool changed = false, gliding = false;
        bool paneBars = PaneBarsShown, tabBar = window.TabBarShown;
        lock (gate)
        {
            var items = tabs.Items;
            for (int t = 0; t < items.Count; t++) // every tick: by index, no boxed enumerator
                if (items[t].Layout is not null) TickTab(items[t], paneBars, tabBar, ref changed, ref gliding);
        }
        if (changed) window.Redraw();
        _pulse = gliding && AnimateProgress;
        if (_pulse && now >= _frameAt)
        {
            _frameAt = now + ProgressBar.FrameMs;
            window.Redraw();
        }
    }

    private void TickTab(Tab tab, bool paneBars, bool tabBar, ref bool changed, ref bool gliding)
    {
        bool active = ReferenceEquals(tab, tabs.Active);
        var combined = new ProgressAggregate();
        foreach (var p in tab.Layout.LeafList)
        {
            var (state, value) = p.Term.Progress;
            var report = p.Exited ? default : ProgressReport.From(state, value);
            if (report != p.Progress) { p.Progress = report; changed = true; }
            combined.Add(report);
            if (active && paneBars && report.State == ProgressState.Indeterminate && !p.LayoutHidden) gliding = true;
        }
        var result = combined.Result;
        if (result != tab.Progress) { tab.Progress = result; changed = true; }
        if (tabBar && result.State == ProgressState.Indeterminate) gliding = true;
    }

    /// <summary>Hook from the event loop: the next frame of a gliding indeterminate bar.</summary>
    public int WakeMs(long now) => _pulse ? (int)Math.Clamp(_frameAt - now, 1, ProgressBar.FrameMs) : int.MaxValue;

    /// <summary>Hook from DrawOverlays, above the panes: the pane bars, along each reporting pane's top edge.</summary>
    public void Draw(DrawList dl, Theme theme, long now)
    {
        if (!PaneBarsShown) return;
        uint? color = tabs.Active is { } tab ? tabColors.ColorOf(tab) : null; // Tabs/WindowTabColors.cs
        float sweep = ProgressBar.Sweep(now, AnimateProgress);
        float h = MathF.Max(1, MathF.Round(theme.S(2)));
        foreach (var pane in window.VisiblePanes())
        {
            if (!pane.Progress.IsActive) continue;
            var r = pane.Rect.Value(now);
            float alpha = pane.Appear.Value(now);
            ProgressBar.Draw(dl, theme, new RectF(r.X, MathF.Round(r.Y), r.W, h), pane.Progress, alpha, sweep, color);
        }
    }
}
