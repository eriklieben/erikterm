using ErikTerm.App.Integrations;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// The git/GitHub badge: compact inside a tab ( main ↑1 ●3  #42 ✓) and as a pill in a pane's top-right
/// corner with the full breakdown. Stateless drawing from a <see cref="RepoBadgeModel"/>; allocation-free.
/// The pill is its own component so other pane pills (agent status) can sit beside it: callers pass the right edge
/// and get the width back.
/// </summary>
internal static class RepoBadge
{
    public const int BranchIcon = 0xE0A0;      // Powerline branch (every Nerd Font)
    public const int PullRequestIcon = 0xEA64; // codicon git-pull-request
    public const int PassIcon = 0xEBA4;        // codicon pass
    public const int FailIcon = 0xEA87;        // codicon error
    public const int CommitIcon = 0xEAFC;      // codicon git-commit
    public const int GitHubIcon = 0xEA84;      // codicon github
    public const int ChangesDot = 0x25CF;      // the dot before a count of changed files

    /// <summary>Whether drawing this badge animates (a pending-checks spinner).</summary>
    public static bool Spins(RepoBadgeModel? m) => m is { Checks: CheckState.Pending };

    // ---- Tab (compact) -------------------------------------------------------------------------

    /// <summary>
    /// Draws the compact badge right-aligned at <paramref name="right"/>, as much of it as fits in
    /// <paramref name="maxWidth"/> (dropping the branch first, then the counts). Returns the width drawn (0 = none).
    /// </summary>
    public static float DrawCompact(DrawList dl, Theme th, RepoBadgeModel m, float right, float baseline, float px, float a, float maxWidth, long now, bool animate)
    {
        if (maxWidth <= 0) return 0;
        for (int level = 0; level < 3; level++)
        {
            float w = Compact(dl, th, m, 0, baseline, px, a, level, now, animate, draw: false);
            if (w <= 0) return 0;
            if (w <= maxWidth)
            {
                Compact(dl, th, m, MathF.Round(right - w), baseline, px, a, level, now, animate, draw: true);
                return w;
            }
        }
        return 0;
    }

    /// <summary>The width of the whole compact badge (level 0), for the tab switcher's rows.</summary>
    public static float CompactWidth(DrawList dl, Theme th, RepoBadgeModel m, float px) =>
        Compact(dl, th, m, 0, 0, px, 1, 0, 0, animate: false, draw: false);

    /// <summary>level 0: everything; 1: no branch; 2: the single most important glyph.</summary>
    private static float Compact(DrawList dl, Theme th, RepoBadgeModel m, float x, float baseline, float px, float a, int level, long now, bool animate, bool draw)
    {
        float start = x, gap = th.S(6);
        float small = px * 0.92f;
        if (m.WouldConflict)
        {
            x += Piece(dl, Icons.Warning, "", th.Danger, x, baseline, small, a, draw);
            if (level == 2) return x - start;
            x += gap;
        }
        if (level == 2) return CompactSingleGlyph(dl, th, m, x, baseline, px, a, now, animate, draw);
        x = CompactCounts(dl, th, m, x, baseline, small, a, gap, branch: level == 0, draw, out bool any);
        if (m.PrText is not null || m.Checks != CheckState.None)
        {
            if (any) x += gap + th.S(2);
            x += Checks(dl, th, m, x, baseline, px, a, now, animate, draw);
        }
        return x - start;
    }

    /// <summary>Level 2's one glyph: the checks if there are any, else the changes, else the branch icon.</summary>
    private static float CompactSingleGlyph(DrawList dl, Theme th, RepoBadgeModel m, float x, float baseline, float px, float a, long now, bool animate, bool draw)
    {
        if (m.PrText is not null || m.Checks != CheckState.None) return Checks(dl, th, m, x, baseline, px, a, now, animate, draw);
        if (m.DirtyText is not null) return Dirty(dl, th, m, x, baseline, px * 0.92f, a, draw);
        return draw ? dl.Glyph(BranchIcon, x, baseline, px, th.Muted, a) : dl.Measure("", px);
    }

    /// <summary>The branch (when asked for), ahead/behind and the changes. Returns the pen; <paramref name="any"/> says whether something was added.</summary>
    private static float CompactCounts(DrawList dl, Theme th, RepoBadgeModel m, float x, float baseline, float small, float a, float gap, bool branch, bool draw, out bool any)
    {
        any = false;
        if (branch)
        {
            x += Piece(dl, BranchIcon, m.BranchShort, Colors.Mix(th.PanelFg, th.Muted, 0.35f), x, baseline, small, a, draw, th.Muted, th.S(3));
            any = true;
        }
        if (m.AheadText is not null || m.BehindText is not null)
        {
            if (any) x += gap;
            x = AheadBehind(dl, th, m, x, baseline, small, a, draw);
            any = true;
        }
        if (m.DirtyText is not null)
        {
            if (any) x += gap;
            x += Dirty(dl, th, m, x, baseline, small, a, draw);
            any = true;
        }
        return x;
    }

    /// <summary>"↑1 ↓2"; returns the pen after it.</summary>
    private static float AheadBehind(DrawList dl, Theme th, RepoBadgeModel m, float x, float baseline, float small, float a, bool draw)
    {
        if (m.AheadText is not null) x += Piece(dl, 0, m.AheadText, th.Accent, x, baseline, small, a, draw);
        if (m.BehindText is not null) x += (m.AheadText is not null ? th.S(2) : 0) + Piece(dl, 0, m.BehindText, th.Accent, x + (m.AheadText is not null ? th.S(2) : 0), baseline, small, a, draw);
        return x;
    }

    /// <summary>The dot and the count of changed files.</summary>
    private static float Dirty(DrawList dl, Theme th, RepoBadgeModel m, float x, float baseline, float px, float a, bool draw) =>
        Piece(dl, ChangesDot, m.DirtyText!, m.Conflicted ? th.Danger : th.Warning, x, baseline, px, a, draw, iconGap: th.S(3));

    /// <summary>"#42 ✓" (or just the checks state when there's no PR).</summary>
    private static float Checks(DrawList dl, Theme th, RepoBadgeModel m, float x, float baseline, float px, float a, long now, bool animate, bool draw)
    {
        float start = x;
        float small = px * 0.92f;
        if (m.PrText is not null)
        {
            uint prColor = m.Draft ? th.Muted : Colors.Mix(th.PanelFg, th.Accent, 0.35f);
            x += Piece(dl, PullRequestIcon, m.PrText, prColor, x, baseline, small, a, draw, th.Accent, th.S(2));
            if (m.Checks != CheckState.None) x += th.S(4);
        }
        x += CheckGlyph(dl, th, m.Checks, x, baseline, px, a, now, animate, draw);
        return x - start;
    }

    private static float CheckGlyph(DrawList dl, Theme th, CheckState state, float x, float baseline, float px, float a, long now, bool animate, bool draw)
    {
        switch (state)
        {
            case CheckState.Pass: return draw ? dl.Glyph(PassIcon, x, baseline, px * 0.95f, th.Success, a) : dl.Atlas.Get(PassIcon, FontStyle.Regular, px * 0.95f).Advance;
            case CheckState.Fail: return draw ? dl.Glyph(FailIcon, x, baseline, px * 0.95f, th.Danger, a) : dl.Atlas.Get(FailIcon, FontStyle.Regular, px * 0.95f).Advance;
            case CheckState.Pending:
                float size = MathF.Round(px * 0.9f);
                if (draw)
                {
                    var m = dl.MetricsFor(px);
                    float cy = baseline - m.Ascender + m.Height / 2;
                    Spinner(dl, x + size / 2, cy, size / 2, th.Warning, a, animate ? now : 0);
                }
                return size + th.S(1);
            default: return 0;
        }
    }

    /// <summary>Eight dots on a circle; the bright one walks around (10 steps/s). <paramref name="now"/> = 0 draws it still.</summary>
    public static void Spinner(DrawList dl, float cx, float cy, float radius, uint rgb, float a, long now)
    {
        int head = (int)(now / 100 % 8);
        float dot = MathF.Max(2.5f, radius * 0.5f);
        float r = radius - dot / 2;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4 - MathF.PI / 2;
            int age = (head - i + 8) % 8;
            float alpha = 1f - age * 0.11f;
            if (now == 0) alpha = i % 2 == 0 ? 0.9f : 0.35f;
            dl.RoundRect(new RectF(cx + MathF.Cos(angle) * r - dot / 2, cy + MathF.Sin(angle) * r - dot / 2, dot, dot), dot / 2, rgb, a * alpha);
        }
    }

    /// <summary>An optional icon then text; returns the advance.</summary>
    private static float Piece(DrawList dl, int icon, string text, uint rgb, float x, float baseline, float px, float a, bool draw, uint iconRgb = 0, float iconGap = 0)
    {
        float start = x;
        if (icon != 0)
        {
            uint glyphRgb = iconRgb == 0 ? rgb : iconRgb;
            float adv = draw ? dl.Glyph(icon, x, baseline, px, glyphRgb, a) : dl.Atlas.Get(icon, FontStyle.Regular, px).Advance;
            x += adv + iconGap;
        }
        x += draw ? dl.Text(text, x, baseline, px, rgb, a) : dl.Measure(text, px);
        return x - start;
    }

    // ---- Pane pill ------------------------------------------------------------------------------

    /// <summary>Height of the pane pill.</summary>
    public static float PillHeight(Theme th) => MathF.Round(th.UiPx * 0.82f * 2.0f);

    /// <summary>
    /// The full badge as a floating pill whose right edge is <paramref name="right"/> and top is <paramref name="top"/>.
    /// Returns its width (0 when nothing to show or it doesn't fit in <paramref name="maxWidth"/>).
    /// </summary>
    public static float DrawPill(DrawList dl, Theme th, RepoBadgeModel m, float right, float top, float maxWidth, float a, long now, bool animate)
    {
        float px = th.UiPx * 0.82f;
        float h = PillHeight(th), padX = th.S(10);
        float content = Full(dl, th, m, 0, 0, px, a, now, animate, draw: false);
        float w = MathF.Round(content + padX * 2);
        if (w > maxWidth || a <= 0.001f) return w > maxWidth ? 0 : w;
        var r = new RectF(MathF.Round(right - w), MathF.Round(top), w, h);
        dl.Shadow(r.Offset(0, th.S(4)), h / 2, th.S(12), 0.40f * a);
        uint border = m.Checks switch
        {
            CheckState.Fail => Colors.Mix(th.PanelBg, th.Danger, 0.55f),
            CheckState.Pending => Colors.Mix(th.PanelBg, th.Warning, 0.40f),
            _ => Colors.Mix(th.PanelBg, th.PanelFg, 0.16f),
        };
        dl.Panel(r, h / 2, th.PanelBg, 0.94f * a, border, a, MathF.Max(1, th.S(1)));
        Full(dl, th, m, r.X + padX, Widgets.Baseline(dl, px, r.Y, h), px, a, now, animate, draw: true);
        return w;
    }

    /// <summary>Lays the pill's short colored texts out in groups: a wider gap before a group's first text, a tight one inside it.</summary>
    private struct PillPen
    {
        public DrawList Dl;
        public Theme Th;
        public float X, Baseline, Px, Alpha, Gap, Tight;
        public bool Draw, First;

        public void Add(string? text, uint rgb, int icon = 0)
        {
            if (text is null) return;
            X += First ? Gap : Tight;
            X += Piece(Dl, icon, text, rgb, X, Baseline, Px, Alpha, Draw, iconGap: icon != 0 ? Th.S(3) : 0);
            First = false;
        }
    }

    private static float Full(DrawList dl, Theme th, RepoBadgeModel m, float x, float baseline, float px, float a, long now, bool animate, bool draw)
    {
        float start = x, gap = th.S(8);
        x += Piece(dl, m.Detached ? CommitIcon : BranchIcon, m.Branch.Length > 40 ? m.BranchShort : m.Branch, th.PanelFg, x, baseline, px, a, draw, th.Accent, th.S(4));
        var pen = new PillPen { Dl = dl, Th = th, X = x, Baseline = baseline, Px = px, Alpha = a, Gap = gap, Tight = th.S(4), Draw = draw, First = true };
        pen.Add(m.AheadText, th.Accent);
        pen.Add(m.BehindText, th.Accent);
        pen.First = true;
        pen.Add(m.StagedText, th.Success);
        pen.Add(m.ModifiedText, th.Warning, ChangesDot);
        pen.Add(m.UntrackedText, th.Muted);
        pen.Add(m.ConflictText, th.Danger);
        x = pen.X;
        if (m.PrText is not null || m.Checks != CheckState.None)
        {
            x += gap;
            if (draw) dl.Rect(new RectF(MathF.Round(x - gap / 2), baseline - px * 0.75f, MathF.Max(1, th.S(1)), px * 0.85f), th.Border, a);
            x += th.S(2);
            x += Checks(dl, th, m, x, baseline, px, a, now, animate, draw);
            if (m.Draft) x += gap + Piece(dl, 0, "draft", th.Muted, x + gap, baseline, px * 0.92f, a, draw);
        }
        return x - start;
    }
}
