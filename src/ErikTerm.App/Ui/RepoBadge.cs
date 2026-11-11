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
    /// How a badge's texts draw: on <see cref="Baseline"/> at <see cref="Px"/> and opacity <see cref="Alpha"/>.
    /// <see cref="Spin"/> is the clock of the pending-checks spinner; 0 draws it still.
    /// </summary>
    public readonly record struct BadgeInk(float Baseline, float Px, float Alpha, long Spin);

    /// <summary>Where a pane pill goes: its right edge and top, and the most width it may take.</summary>
    public readonly record struct PillAnchor(float Right, float Top, float MaxWidth);

    /// <summary>One drawing (or, with <see cref="Draw"/> false, measuring) pass over a badge's pieces.</summary>
    private readonly record struct Pass(DrawList Dl, Theme Th, float Baseline, float Px, float Alpha, long Spin, bool Draw);

    /// <summary>
    /// Draws the compact badge right-aligned at <paramref name="right"/>, as much of it as fits in
    /// <paramref name="maxWidth"/> (dropping the branch first, then the counts). Returns the width drawn (0 = none).
    /// </summary>
    public static float DrawCompact(DrawList dl, Theme th, RepoBadgeModel m, float right, float maxWidth, in BadgeInk ink)
    {
        if (maxWidth <= 0) return 0;
        var measure = new Pass(dl, th, ink.Baseline, ink.Px, ink.Alpha, ink.Spin, Draw: false);
        for (int level = 0; level < 3; level++)
        {
            float w = Compact(in measure, m, 0, level);
            if (w <= 0) return 0;
            if (w <= maxWidth)
            {
                Compact(measure with { Draw = true }, m, MathF.Round(right - w), level);
                return w;
            }
        }
        return 0;
    }

    /// <summary>The width of the whole compact badge (level 0), for the tab switcher's rows.</summary>
    public static float CompactWidth(DrawList dl, Theme th, RepoBadgeModel m, float px) =>
        Compact(new Pass(dl, th, 0, px, 1, 0, Draw: false), m, 0, 0);

    /// <summary>level 0: everything; 1: no branch; 2: the single most important glyph.</summary>
    private static float Compact(in Pass p, RepoBadgeModel m, float x, int level)
    {
        var th = p.Th;
        float start = x, gap = th.S(6);
        var small = p with { Px = p.Px * 0.92f };
        if (m.WouldConflict)
        {
            x += Piece(in small, Icons.Warning, "", th.Danger, x);
            if (level == 2) return x - start;
            x += gap;
        }
        if (level == 2) return CompactSingleGlyph(in p, m, x);
        x = CompactCounts(in small, m, x, gap, branch: level == 0, out bool any);
        if (m.PrText is not null || m.Checks != CheckState.None)
        {
            if (any) x += gap + th.S(2);
            x += Checks(in p, m, x);
        }
        return x - start;
    }

    /// <summary>Level 2's one glyph: the checks if there are any, else the changes, else the branch icon.</summary>
    private static float CompactSingleGlyph(in Pass p, RepoBadgeModel m, float x)
    {
        if (m.PrText is not null || m.Checks != CheckState.None) return Checks(in p, m, x);
        if (m.DirtyText is not null) return Dirty(p with { Px = p.Px * 0.92f }, m, x);
        return p.Draw ? p.Dl.Glyph(BranchIcon, x, p.Baseline, p.Px, p.Th.Muted, p.Alpha) : p.Dl.Measure("", p.Px);
    }

    /// <summary>The branch (when asked for), ahead/behind and the changes, at <paramref name="p"/>'s (small) size. Returns the pen; <paramref name="any"/> says whether something was added.</summary>
    private static float CompactCounts(in Pass p, RepoBadgeModel m, float x, float gap, bool branch, out bool any)
    {
        var th = p.Th;
        any = false;
        if (branch)
        {
            x += Piece(in p, BranchIcon, m.BranchShort, Colors.Mix(th.PanelFg, th.Muted, 0.35f), x, th.Muted, th.S(3));
            any = true;
        }
        if (m.AheadText is not null || m.BehindText is not null)
        {
            if (any) x += gap;
            x = AheadBehind(in p, m, x);
            any = true;
        }
        if (m.DirtyText is not null)
        {
            if (any) x += gap;
            x += Dirty(in p, m, x);
            any = true;
        }
        return x;
    }

    /// <summary>"↑1 ↓2"; returns the pen after it.</summary>
    private static float AheadBehind(in Pass p, RepoBadgeModel m, float x)
    {
        var th = p.Th;
        if (m.AheadText is not null) x += Piece(in p, 0, m.AheadText, th.Accent, x);
        if (m.BehindText is not null) x += (m.AheadText is not null ? th.S(2) : 0) + Piece(in p, 0, m.BehindText, th.Accent, x + (m.AheadText is not null ? th.S(2) : 0));
        return x;
    }

    /// <summary>The dot and the count of changed files.</summary>
    private static float Dirty(in Pass p, RepoBadgeModel m, float x) =>
        Piece(in p, ChangesDot, m.DirtyText!, m.Conflicted ? p.Th.Danger : p.Th.Warning, x, iconGap: p.Th.S(3));

    /// <summary>"#42 ✓" (or just the checks state when there's no PR).</summary>
    private static float Checks(in Pass p, RepoBadgeModel m, float x)
    {
        var th = p.Th;
        float start = x;
        if (m.PrText is not null)
        {
            uint prColor = m.Draft ? th.Muted : Colors.Mix(th.PanelFg, th.Accent, 0.35f);
            x += Piece(p with { Px = p.Px * 0.92f }, PullRequestIcon, m.PrText, prColor, x, th.Accent, th.S(2));
            if (m.Checks != CheckState.None) x += th.S(4);
        }
        x += CheckGlyph(in p, m.Checks, x);
        return x - start;
    }

    private static float CheckGlyph(in Pass p, CheckState state, float x)
    {
        DrawList dl = p.Dl;
        Theme th = p.Th;
        float px = p.Px;
        switch (state)
        {
            case CheckState.Pass: return p.Draw ? dl.Glyph(PassIcon, x, p.Baseline, px * 0.95f, th.Success, p.Alpha) : dl.Atlas.Get(PassIcon, FontStyle.Regular, px * 0.95f).Advance;
            case CheckState.Fail: return p.Draw ? dl.Glyph(FailIcon, x, p.Baseline, px * 0.95f, th.Danger, p.Alpha) : dl.Atlas.Get(FailIcon, FontStyle.Regular, px * 0.95f).Advance;
            case CheckState.Pending:
                float size = MathF.Round(px * 0.9f);
                if (p.Draw)
                {
                    var m = dl.MetricsFor(px);
                    float cy = p.Baseline - m.Ascender + m.Height / 2;
                    Spinner(dl, x + size / 2, cy, size / 2, th.Warning, p.Alpha, p.Spin);
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
    private static float Piece(in Pass p, int icon, string text, uint rgb, float x, uint iconRgb = 0, float iconGap = 0)
    {
        DrawList dl = p.Dl;
        float start = x;
        if (icon != 0)
        {
            uint glyphRgb = iconRgb == 0 ? rgb : iconRgb;
            float adv = p.Draw ? dl.Glyph(icon, x, p.Baseline, p.Px, glyphRgb, p.Alpha) : dl.Atlas.Get(icon, FontStyle.Regular, p.Px).Advance;
            x += adv + iconGap;
        }
        x += p.Draw ? dl.Text(text, x, p.Baseline, p.Px, rgb, p.Alpha) : dl.Measure(text, p.Px);
        return x - start;
    }

    // ---- Pane pill ------------------------------------------------------------------------------

    /// <summary>Height of the pane pill.</summary>
    public static float PillHeight(Theme th) => MathF.Round(th.UiPx * 0.82f * 2.0f);

    /// <summary>
    /// The full badge as a floating pill at <paramref name="at"/> (its right edge and top); <paramref name="spin"/> is the
    /// spinner's clock (0 = still). Returns its width (0 when nothing to show or it doesn't fit in the anchor's MaxWidth).
    /// </summary>
    public static float DrawPill(DrawList dl, Theme th, RepoBadgeModel m, in PillAnchor at, float a, long spin)
    {
        float px = th.UiPx * 0.82f;
        float h = PillHeight(th), padX = th.S(10);
        float content = Full(new Pass(dl, th, 0, px, a, spin, Draw: false), m, 0);
        float w = MathF.Round(content + padX * 2);
        if (w > at.MaxWidth || a <= 0.001f) return w > at.MaxWidth ? 0 : w;
        var r = new RectF(MathF.Round(at.Right - w), MathF.Round(at.Top), w, h);
        dl.Shadow(r.Offset(0, th.S(4)), h / 2, th.S(12), 0.40f * a);
        uint border = m.Checks switch
        {
            CheckState.Fail => Colors.Mix(th.PanelBg, th.Danger, 0.55f),
            CheckState.Pending => Colors.Mix(th.PanelBg, th.Warning, 0.40f),
            _ => Colors.Mix(th.PanelBg, th.PanelFg, 0.16f),
        };
        dl.Panel(r, h / 2, th.PanelBg, 0.94f * a, border, a, MathF.Max(1, th.S(1)));
        Full(new Pass(dl, th, Widgets.Baseline(dl, px, r.Y, h), px, a, spin, Draw: true), m, r.X + padX);
        return w;
    }

    /// <summary>Lays the pill's short colored texts out in groups: a wider gap before a group's first text, a tight one inside it.</summary>
    private struct PillPen
    {
        public Pass Pass;
        public float X, Gap, Tight;
        public bool First;

        public void Add(string? text, uint rgb, int icon = 0)
        {
            if (text is null) return;
            X += First ? Gap : Tight;
            X += Piece(in Pass, icon, text, rgb, X, iconGap: icon != 0 ? Pass.Th.S(3) : 0);
            First = false;
        }
    }

    private static float Full(in Pass p, RepoBadgeModel m, float x)
    {
        var th = p.Th;
        float start = x, gap = th.S(8);
        x += Piece(in p, m.Detached ? CommitIcon : BranchIcon, m.Branch.Length > 40 ? m.BranchShort : m.Branch, th.PanelFg, x, th.Accent, th.S(4));
        var pen = new PillPen { Pass = p, X = x, Gap = gap, Tight = th.S(4), First = true };
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
            if (p.Draw) p.Dl.Rect(new RectF(MathF.Round(x - gap / 2), p.Baseline - p.Px * 0.75f, MathF.Max(1, th.S(1)), p.Px * 0.85f), th.Border, p.Alpha);
            x += th.S(2);
            x += Checks(in p, m, x);
            if (m.Draft) x += gap + Piece(p with { Px = p.Px * 0.92f }, 0, "draft", th.Muted, x + gap);
        }
        return x - start;
    }
}
