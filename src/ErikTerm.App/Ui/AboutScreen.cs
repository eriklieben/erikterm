using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// "About erikterm": a centred card with a procedural logo, version pills, an Overview tab (build,
/// display, text and session facts in a two-column grid) and a scrollable Licenses tab. Facts are
/// captured once on open; the layout is computed when the size changes and cached as draw ops, so
/// drawing a frame allocates nothing.
/// </summary>
internal sealed class AboutScreen
{
    private enum Ink : byte { Fg, Muted, Faint, Accent, Warning, Border }
    private enum OpKind : byte { Text, Glyph, Rect, RoundRect }

    /// <summary>A cached draw command; coordinates are relative to the body's content origin.</summary>
    private struct Op
    {
        public OpKind Kind;
        public Ink Ink;
        public FontStyle Style;
        public int Group;
        public int Code;
        public float X, Y, W, H, Px, Radius, Alpha;
        public string? Text;
    }

    private enum Target { None, TabOverview, TabLicenses, Copy, OpenConfig, Close, CloseX, ConfigLink, Count }

    private static readonly Target[] FocusOrder = [Target.TabOverview, Target.TabLicenses, Target.Copy, Target.OpenConfig, Target.Close];

    private const int IconBuild = 0xEB29;     // package
    private const int IconSystem = 0xEC19;    // chip
    private const int IconDisplay = 0xEA7A;   // device-desktop
    private const int IconText = 0xEAB1;      // case (Aa)
    private const int IconSession = 0xEA85;   // terminal
    private const int IconLicenses = 0xEB12;  // law
    private const int IconTag = 0xEA66;
    private const int IconCommit = 0xEAFC;
    private const int IconCalendar = 0xEAB0;
    private const int IconLinkExternal = 0xEB14;
    private const int Dot = 0xEA71;           // circle-filled

    public Action? CopyDiagnostics;
    public Action? OpenConfig;

    // Content, captured on open.
    private List<AboutSection> _sections = [];
    private List<LicenseEntry> _licenses = [];
    private string _versionPill = "", _commitPill = "", _datePill = "", _licenseIntro = "";
    private bool _dirty;

    // State.
    private bool _open;
    private long _openedAt;
    private float _animMs = 160;
    private Tween _visible = new(0);
    private int _tab;
    private int _tabDir = 1;
    private Tween _tabFade = new(1);
    private Tween _indicatorX = new(0), _indicatorW = new(0);
    private bool _indicatorPlaced;
    private readonly Tween[] _scroll = [new(0), new(0)];
    private readonly Tween[] _glow = new Tween[(int)Target.Count];
    private Target _hover, _pressed, _focus = Target.Close;
    private bool _focusVisible;

    // Layout cache (rebuilt when the width, text size, font or compactness changes).
    private float _layoutW = -1, _layoutPx = -1;
    private bool _layoutCompact;
    private GlyphAtlas? _layoutAtlas;
    private readonly List<Op> _hero = [];
    private readonly List<Op>[] _body = [[], []];
    private readonly float[] _contentH = new float[2];
    private float _heroH, _tabsH, _footerH, _bodyPadTop, _bodyPadBottom;
    private RectF _logoRel, _linkRel;
    private readonly RectF[] _tabRel = new RectF[2];
    private readonly string[] _tabLabels = ["Overview", "Licenses"];
    private readonly int[] _tabIcons = [0xEA74, IconLicenses];
    private readonly RectF[] _buttonRel = new RectF[3]; // Copy, Open config, Close; Y relative to the footer top
    private readonly string[] _buttonLabels = ["Copy diagnostics", "Open config", "Close"];
    private readonly int[] _buttonIcons = [Icons.Copy, Icons.Gear, Icons.Close];
    private bool _buttonsIconOnly, _showCopyright;

    // Last frame, for hit-testing.
    private RectF _panel, _bodyView;
    private readonly RectF[] _targets = new RectF[(int)Target.Count];
    private float _maxScroll, _rowStep = 24;

    public bool IsOpen => _open;
    public bool IsVisible(long now) => _open || _visible.Value(now) > 0.001f;

    // While open the logo's cursor breathes, like a caret; otherwise only tweens animate.
    public bool IsAnimating(long now)
    {
        if (_open || _visible.IsAnimating(now) || _tabFade.IsAnimating(now)) return true;
        if (_scroll[0].IsAnimating(now) || _scroll[1].IsAnimating(now)) return true;
#pragma warning disable S3267 // IsAnimating runs every frame: no LINQ iterator or closure here.
        foreach (var g in _glow) if (g.IsAnimating(now)) return true;
#pragma warning restore S3267
        return false;
    }

    public void Open(AboutInfo info, Theme th, long now)
    {
        _sections = AboutFormat.Sections(info);
        _licenses = AboutLicenses.Build(info);
        _versionPill = "v" + info.Version;
        _commitPill = info.Commit ?? "";
        _dirty = info.Dirty;
        _datePill = info.BuildDate ?? "";
        _licenseIntro = $"erikterm {info.VersionLine} · {AboutInfo.Copyright}. Built on the work of these projects — thank you.";

        _open = true;
        _openedAt = now;
        _animMs = th.AnimationMs;
        _visible.Snap(0);
        _visible.Set(1, now, _animMs * 1.6f);
        _tab = 0;
        _tabFade.Snap(1);
        _scroll[0].Snap(0);
        _scroll[1].Snap(0);
        _hover = _pressed = Target.None;
        _focus = Target.Close;
        _focusVisible = false;
        _indicatorPlaced = false;
        for (int i = 0; i < _glow.Length; i++) _glow[i].Snap(0);
        _layoutW = -1; // content changed
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.9f);
    }

    // ---- Input ------------------------------------------------------------------------------

    public bool HandleKey(uint key, ushort mod, long now)
    {
        if (!_open) return false;
        bool ctrl = (mod & Sdl.KMOD_CTRL) != 0, shift = (mod & Sdl.KMOD_SHIFT) != 0;
        switch (key)
        {
            case Sdl.K_ESCAPE: Close(now); break;
            case 'c' when ctrl: CopyDiagnostics?.Invoke(); break;
            case Sdl.K_TAB when ctrl: SelectTab(1 - _tab, now); break;
            case Sdl.K_TAB: MoveFocus(shift ? -1 : 1, now); break;
            case Sdl.K_LEFT or Sdl.K_RIGHT: MoveSideways(key == Sdl.K_LEFT ? -1 : 1, now); break;
            case Sdl.K_UP: ScrollBy(-_rowStep * 2, now); break;
            case Sdl.K_DOWN: ScrollBy(_rowStep * 2, now); break;
            case Sdl.K_PAGEUP: ScrollBy(-MathF.Max(_rowStep, _bodyView.H - _rowStep * 2), now); break;
            case Sdl.K_PAGEDOWN or Sdl.K_SPACE when key == Sdl.K_PAGEDOWN || !_focusVisible:
                ScrollBy(MathF.Max(_rowStep, _bodyView.H - _rowStep * 2), now);
                break;
            case Sdl.K_HOME: ScrollTo(0, now); break;
            case Sdl.K_END: ScrollTo(_maxScroll, now); break;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER or Sdl.K_SPACE: Activate(_focus, now); break;
        }
        return true;
    }

    /// <summary>Left / Right: switches tabs, or moves the focus along the footer's buttons when one of those has it.</summary>
    private void MoveSideways(int dir, long now)
    {
        if (!_focusVisible || _focus is Target.TabOverview or Target.TabLicenses)
        {
            SelectTab(dir < 0 ? 0 : 1, now);
            if (_focusVisible) _focus = _tab == 0 ? Target.TabOverview : Target.TabLicenses;
        }
        else _focus = (Target)Math.Clamp((int)_focus + dir, (int)Target.Copy, (int)Target.Close);
    }

    public bool HandleMouseMove(float x, float y, long now)
    {
        if (!_open) return false;
        SetHover(HitTest(x, y), now);
        return _hover != Target.None;
    }

    public bool HandleMouseDown(float x, float y, long now)
    {
        if (!_open) return false;
        if (!_panel.Contains(x, y)) { Close(now); return true; }
        _pressed = HitTest(x, y);
        return true;
    }

    public bool HandleMouseUp(float x, float y, long now)
    {
        if (!_open) return false;
        var hit = HitTest(x, y);
        if (hit != Target.None && hit == _pressed) Activate(hit, now);
        _pressed = Target.None;
        return true;
    }

    public bool HandleWheel(int lines, long now)
    {
        if (!_open) return false;
        ScrollBy(-lines * _rowStep * 3, now);
        return true;
    }

    private void Activate(Target t, long now)
    {
        switch (t)
        {
            case Target.TabOverview: SelectTab(0, now); break;
            case Target.TabLicenses: SelectTab(1, now); break;
            case Target.Copy: CopyDiagnostics?.Invoke(); break;
            case Target.OpenConfig or Target.ConfigLink: Close(now); OpenConfig?.Invoke(); break;
            case Target.Close or Target.CloseX: Close(now); break;
        }
    }

    private void SelectTab(int tab, long now)
    {
        if (tab == _tab) return;
        _tabDir = tab > _tab ? 1 : -1;
        _tab = tab;
        _tabFade.Snap(0);
        _tabFade.Set(1, now, _animMs * 1.5f);
    }

    private void MoveFocus(int dir, long now)
    {
        int i = Array.IndexOf(FocusOrder, _focus);
        if (!_focusVisible) i = dir > 0 ? -1 : 0; // first Tab lands on the first item
        _focusVisible = true;
        i = ((i + dir) % FocusOrder.Length + FocusOrder.Length) % FocusOrder.Length;
        _focus = FocusOrder[i];
        if (_focus == Target.TabOverview) SelectTab(0, now);
        else if (_focus == Target.TabLicenses) SelectTab(1, now);
    }

    private void SetHover(Target t, long now)
    {
        if (t == _hover) return;
        if (_hover != Target.None) _glow[(int)_hover].Set(0, now, _animMs * 1.2f);
        _hover = t;
        if (t != Target.None) _glow[(int)t].Set(1, now, _animMs * 0.6f);
    }

    private void ScrollBy(float delta, long now) => ScrollTo(_scroll[_tab].Target + delta, now);

    private void ScrollTo(float y, long now) =>
        _scroll[_tab].Set(Math.Clamp(MathF.Round(y), 0, _maxScroll), now, _animMs * 0.9f);

    private Target HitTest(float x, float y)
    {
        if (!_panel.Contains(x, y)) return Target.None;
        for (int i = 1; i < _targets.Length; i++)
        {
            if ((Target)i == Target.ConfigLink && (_tab != 0 || !_bodyView.Contains(x, y))) continue;
            if (_targets[i].W > 0 && _targets[i].Contains(x, y)) return (Target)i;
        }
        return Target.None;
    }

    // ---- Layout -----------------------------------------------------------------------------

    private void Layout(DrawList dl, Theme th, float panelW, bool compact)
    {
        _layoutW = panelW;
        _layoutPx = th.UiPx;
        _layoutAtlas = dl.Atlas;
        _layoutCompact = compact;

        float px = th.UiPx;
        float padX = th.S(28);
        float innerW = panelW - 2 * padX;
        _rowStep = MathF.Round(px * 1.75f);

        LayoutHero(dl, th, panelW, compact);

        // Tabs.
        _tabsH = MathF.Round(px * 2.5f);
        float tx = padX - th.S(10);
        for (int i = 0; i < 2; i++)
        {
            float w = px * 1.55f + dl.Measure(_tabLabels[i], px * 0.92f) + th.S(20);
            _tabRel[i] = new RectF(MathF.Round(tx), _heroH, MathF.Round(w), _tabsH);
            tx += w + th.S(4);
        }

        _bodyPadTop = th.S(20);
        _bodyPadBottom = th.S(20);
        LayoutOverview(dl, th, innerW);
        LayoutLicenses(dl, th, innerW);
        LayoutFooter(dl, th, panelW);
    }

    /// <summary>The row of pills under the wordmark, filled left to right.</summary>
    private struct HeroPills
    {
        public FontSet.Metrics Metrics;
        public float Px, Height, Right, Y, X;
    }

    private void LayoutHero(DrawList dl, Theme th, float panelW, bool compact)
    {
        _hero.Clear();
        float px = th.UiPx;
        float padX = th.S(28), top = compact ? th.S(18) : th.S(26);
        float logo = MathF.Round(px * (compact ? 2.9f : 4.4f));
        _logoRel = new RectF(padX, top, logo, logo);

        float wmPx = MathF.Round(px * (compact ? 1.6f : 2.1f));
        float tagPx = px * 0.92f, pillPx = px * 0.78f;
        var wm = dl.MetricsFor(wmPx);
        var tag = dl.MetricsFor(tagPx);
        var pm = dl.MetricsFor(pillPx);
        float pillH = MathF.Round(pm.Height + th.S(9));
        bool showTagline = !compact;

        float blockH = wm.Height + (showTagline ? th.S(4) + tag.Height : 0) + th.S(12) + pillH;
        float blockTop = MathF.Round(top + (logo - blockH) / 2);
        float x = padX + logo + th.S(20);

        float wmBase = MathF.Round(blockTop + wm.Ascender);
        float erikW = dl.Measure("erik", wmPx, FontStyle.Bold);
        _hero.Add(new Op { Kind = OpKind.Text, Text = "erik", X = x, Y = wmBase, Px = wmPx, Ink = Ink.Fg, Style = FontStyle.Bold, Group = 1, Alpha = 1 });
        _hero.Add(new Op { Kind = OpKind.Text, Text = "term", X = x + erikW, Y = wmBase, Px = wmPx, Ink = Ink.Accent, Style = FontStyle.Bold, Group = 1, Alpha = 1 });
        float y = blockTop + wm.Height;
        if (showTagline)
        {
            y += th.S(4);
            string tagline = dl.Ellipsize(AboutInfo.Tagline, tagPx, panelW - x - padX);
            _hero.Add(new Op { Kind = OpKind.Text, Text = tagline, X = x, Y = MathF.Round(y + tag.Ascender), Px = tagPx, Ink = Ink.Muted, Group = 1, Alpha = 1 });
            y += tag.Height;
        }
        y = MathF.Round(y + th.S(12));

        // Pills: version (accent), commit, "modified", build date — as many as fit.
        var pills = new HeroPills { Metrics = pm, Px = pillPx, Height = pillH, Right = panelW - padX, Y = y, X = x };
        AddHeroPill(dl, th, ref pills, _versionPill, IconTag, Ink.Accent, tinted: true);
        AddHeroPill(dl, th, ref pills, _commitPill, IconCommit, Ink.Fg, tinted: false);
        if (_dirty) AddHeroPill(dl, th, ref pills, "modified", Dot, Ink.Warning, tinted: true);
        AddHeroPill(dl, th, ref pills, _datePill, IconCalendar, Ink.Fg, tinted: false);

        _heroH = MathF.Round(MathF.Max(top + logo, y + pillH) + (compact ? th.S(14) : th.S(22)));
    }

    private void AddHeroPill(DrawList dl, Theme th, ref HeroPills row, string text, int icon, Ink ink, bool tinted)
    {
        if (text.Length == 0) return;
        float pillX = row.X, y = row.Y, pillH = row.Height;
        float iconW = icon != 0 ? row.Px * 1.45f : 0;
        float w = MathF.Round(dl.Measure(text, row.Px) + iconW + th.S(20));
        if (pillX + w > row.Right) { row.X = row.Right + 1; return; } // stop at the first that doesn't fit
        float baseline = MathF.Round(y + (pillH - row.Metrics.Height) / 2 + row.Metrics.Ascender);
        if (tinted)
            _hero.Add(new Op { Kind = OpKind.RoundRect, X = pillX, Y = y, W = w, H = pillH, Radius = pillH / 2, Ink = ink, Alpha = 0.16f, Group = 2 });
        else
        {
            _hero.Add(new Op { Kind = OpKind.RoundRect, X = pillX, Y = y, W = w, H = pillH, Radius = pillH / 2, Ink = Ink.Border, Alpha = 1, Group = 2 });
            _hero.Add(new Op { Kind = OpKind.RoundRect, X = pillX + 1, Y = y + 1, W = w - 2, H = pillH - 2, Radius = pillH / 2 - 1, Ink = Ink.Faint, Alpha = 0.10f, Group = 2 });
        }
        float tx = pillX + th.S(10);
        if (icon != 0)
        {
            _hero.Add(new Op { Kind = OpKind.Glyph, Code = icon, X = tx, Y = baseline, Px = row.Px, Ink = tinted ? ink : Ink.Muted, Alpha = 1, Group = 2 });
            tx += iconW;
        }
        _hero.Add(new Op { Kind = OpKind.Text, Text = text, X = tx, Y = baseline, Px = row.Px, Ink = tinted ? ink : Ink.Fg, Alpha = tinted ? 1 : 0.9f, Group = 2 });
        row.X += w + th.S(8);
    }

    /// <summary>Where a tab's layout goes: the ops it adds to <see cref="Ops"/>, measured with <see cref="Dl"/> at <see cref="Th"/>'s sizes.</summary>
    private readonly record struct OpLayout(DrawList Dl, Theme Th, List<Op> Ops);

    /// <summary>The Overview tab's columns and text sizes.</summary>
    private readonly record struct OverviewGrid(float RowPx, float RowH, float HeadH, float LabelW, float ColW, float ColGap, bool TwoColumns, FontSet.Metrics Metrics);

    private void LayoutOverview(DrawList dl, Theme th, float w)
    {
        var ops = _body[0];
        ops.Clear();
        _linkRel = default;
        float px = th.UiPx;
        float rowPx = px * 0.9f, colGap = th.S(40), sectionGap = th.S(22);
        bool two = w >= px * 34;
        var grid = new OverviewGrid(rowPx, _rowStep, MathF.Round(px * 2.1f), OverviewLabelWidth(dl, th, rowPx), two ? MathF.Floor((w - colGap) / 2) : w, colGap, two, dl.MetricsFor(rowPx));
        Span<float> heights = [0, 0];

        for (int si = 0; si < _sections.Count; si++)
        {
            int col = two && heights[1] < heights[0] ? 1 : 0;
            heights[col] = AddOverviewSection(new OpLayout(dl, th, ops), _sections[si], si, col, heights[col], grid) + sectionGap;
        }
        _contentH[0] = MathF.Max(heights[0], heights[1]) - sectionGap;
    }

    private float OverviewLabelWidth(DrawList dl, Theme th, float rowPx)
    {
        float labelW = 0;
        foreach (var s in _sections) foreach (var r in s.Rows) labelW = MathF.Max(labelW, dl.Measure(r.Label, rowPx));
        return MathF.Round(labelW + th.S(14));
    }

    /// <summary>A section's heading and rows from <paramref name="y"/> down; returns the y below them.</summary>
    private float AddOverviewSection(in OpLayout l, AboutSection s, int si, int col, float y, in OverviewGrid g)
    {
        var (dl, th, ops) = l;
        float px = th.UiPx, rowPx = g.RowPx;
        var rm = g.Metrics;
        float x = col * (g.ColW + g.ColGap);

        float hb = MathF.Round(y + (g.HeadH - rm.Height) / 2 + rm.Ascender);
        ops.Add(new Op { Kind = OpKind.Glyph, Code = SectionIcon(s.Kind), X = x, Y = hb, Px = px * 0.95f, Ink = Ink.Accent, Alpha = 1, Group = si });
        float titleX = x + px * 1.7f;
        float tw = dl.Measure(s.Title, rowPx, FontStyle.Bold);
        ops.Add(new Op { Kind = OpKind.Text, Text = s.Title, X = titleX, Y = hb, Px = rowPx, Ink = Ink.Fg, Style = FontStyle.Bold, Alpha = 1, Group = si });
        float lineX = titleX + tw + th.S(12);
        if (x + g.ColW > lineX)
            ops.Add(new Op { Kind = OpKind.Rect, X = lineX, Y = MathF.Round(y + g.HeadH / 2), W = x + g.ColW - lineX, H = MathF.Max(1, MathF.Round(th.S(1))), Ink = Ink.Border, Alpha = 1, Group = si });
        y += g.HeadH;

        foreach (var r in s.Rows)
        {
            AddOverviewRow(in l, r, si, col, x, y, g);
            y += g.RowH;
        }
        return y;
    }

    private void AddOverviewRow(in OpLayout l, AboutRow r, int si, int col, float x, float y, in OverviewGrid g)
    {
        var (dl, th, ops) = l;
        float rowPx = g.RowPx;
        float b = MathF.Round(y + (g.RowH - g.Metrics.Height) / 2 + g.Metrics.Ascender);
        ops.Add(new Op { Kind = OpKind.Text, Text = r.Label, X = x, Y = b, Px = rowPx, Ink = Ink.Muted, Alpha = 1, Group = si });
        // Left-column values may run into the column gap (the config path is the longest).
        float vx = x + g.LabelW, vw = g.ColW - g.LabelW + (g.TwoColumns && col == 0 ? g.ColGap * 0.6f : 0);
        bool link = r.Kind == AboutValueKind.Link;
        float linkIconW = link ? rowPx * 1.5f : 0;
        string value = link ? EllipsizePath(dl, r.Value, rowPx, vw - linkIconW) : dl.Ellipsize(r.Value, rowPx, vw - linkIconW);
        var ink = r.Kind switch { AboutValueKind.Link => Ink.Accent, AboutValueKind.Warning => Ink.Warning, AboutValueKind.Dim => Ink.Muted, _ => Ink.Fg };
        ops.Add(new Op { Kind = OpKind.Text, Text = value, X = vx, Y = b, Px = rowPx, Ink = ink, Alpha = 1, Group = si });
        if (link)
        {
            float valueW = dl.Measure(value, rowPx);
            ops.Add(new Op { Kind = OpKind.Glyph, Code = IconLinkExternal, X = vx + valueW + rowPx * 0.45f, Y = b, Px = rowPx * 0.9f, Ink = Ink.Accent, Alpha = 0.8f, Group = si });
            _linkRel = new RectF(vx - th.S(6), y + th.S(2), valueW + linkIconW + th.S(10), g.RowH - th.S(4));
        }
    }

    /// <summary>The Licenses tab's text sizes and line heights.</summary>
    private readonly record struct LicenseType(float NamePx, float MetaPx, float BodyPx, float PillPx,
        FontSet.Metrics Name, FontSet.Metrics Meta, FontSet.Metrics Body, FontSet.Metrics Pill, float MetaLine, float BodyLine);

    private void LayoutLicenses(DrawList dl, Theme th, float w)
    {
        var ops = _body[1];
        ops.Clear();
        float px = th.UiPx;
        float namePx = px * 0.98f, metaPx = px * 0.84f, bodyPx = px * 0.8f, pillPx = px * 0.74f;
        var mm = dl.MetricsFor(metaPx);
        var bm = dl.MetricsFor(bodyPx);
        float metaLine = MathF.Round(mm.Height * 1.45f), bodyLine = MathF.Round(bm.Height * 1.45f);
        var t = new LicenseType(namePx, metaPx, bodyPx, pillPx, dl.MetricsFor(namePx), mm, bm, dl.MetricsFor(pillPx), metaLine, bodyLine);
        float y = 0;

        foreach (var line in Wrap(dl, _licenseIntro, metaPx, w))
        {
            ops.Add(new Op { Kind = OpKind.Text, Text = line, X = 0, Y = MathF.Round(y + mm.Ascender), Px = metaPx, Ink = Ink.Muted, Alpha = 1, Group = 0 });
            y += metaLine;
        }
        y += th.S(16);

        for (int i = 0; i < _licenses.Count; i++)
            y = AddLicense(new OpLayout(dl, th, ops), _licenses[i], i + 1, w, y, t);
        _contentH[1] = y;
    }

    /// <summary>One component's entry from <paramref name="y"/> down; returns the y below it.</summary>
    private static float AddLicense(in OpLayout l, LicenseEntry e, int g, float w, float y, in LicenseType t)
    {
        var (dl, th, ops) = l;
        ops.Add(new Op { Kind = OpKind.Rect, X = 0, Y = MathF.Round(y), W = w, H = MathF.Max(1, MathF.Round(th.S(1))), Ink = Ink.Border, Alpha = 1, Group = g });
        y += th.S(16);
        y = AddLicenseTitle(in l, e, g, w, y, t);

        foreach (var line in Wrap(dl, e.Copyright, t.MetaPx, w))
        {
            ops.Add(new Op { Kind = OpKind.Text, Text = line, X = 0, Y = MathF.Round(y + t.Meta.Ascender), Px = t.MetaPx, Ink = Ink.Fg, Alpha = 0.85f, Group = g });
            y += t.MetaLine;
        }

        if (e.Credit is { } credit) y = AddLicenseCredit(in l, credit, g, w, y, t);

        y += th.S(8);
        foreach (var p in e.Paragraphs)
        {
            foreach (var line in Wrap(dl, p, t.BodyPx, w))
            {
                ops.Add(new Op { Kind = OpKind.Text, Text = line, X = 0, Y = MathF.Round(y + t.Body.Ascender), Px = t.BodyPx, Ink = Ink.Muted, Alpha = 1, Group = g });
                y += t.BodyLine;
            }
            y += th.S(6);
        }
        if (e.Url is { } url)
        {
            float b = MathF.Round(y + t.Body.Ascender);
            ops.Add(new Op { Kind = OpKind.Glyph, Code = IconLinkExternal, X = 0, Y = b, Px = t.BodyPx, Ink = Ink.Accent, Alpha = 0.8f, Group = g });
            ops.Add(new Op { Kind = OpKind.Text, Text = dl.Ellipsize(url, t.BodyPx, w - t.BodyPx * 1.6f), X = t.BodyPx * 1.6f, Y = b, Px = t.BodyPx, Ink = Ink.Accent, Alpha = 0.9f, Group = g });
            y += t.BodyLine;
        }
        return y + th.S(10);
    }

    /// <summary>Name, version, license pill on the right; returns the y below them.</summary>
    private static float AddLicenseTitle(in OpLayout l, LicenseEntry e, int g, float w, float y, in LicenseType t)
    {
        var (dl, th, ops) = l;
        float pillH = MathF.Round(t.Pill.Height + th.S(8));
        float pillW = MathF.Round(dl.Measure(e.License, t.PillPx) + th.S(18));
        float rowH = MathF.Max(t.Name.Height, pillH);
        float nb = MathF.Round(y + (rowH - t.Name.Height) / 2 + t.Name.Ascender);
        float nameMax = w - pillW - th.S(16);
        string name = dl.Ellipsize(e.Name, t.NamePx, nameMax, FontStyle.Bold);
        float nw = dl.Measure(name, t.NamePx, FontStyle.Bold);
        ops.Add(new Op { Kind = OpKind.Text, Text = name, X = 0, Y = nb, Px = t.NamePx, Ink = Ink.Fg, Style = FontStyle.Bold, Alpha = 1, Group = g });
        if (e.Version is { Length: > 0 } ver && nw + th.S(10) < nameMax)
        {
            string v = dl.Ellipsize(ver, t.MetaPx, nameMax - nw - th.S(10));
            ops.Add(new Op { Kind = OpKind.Text, Text = v, X = nw + th.S(10), Y = nb, Px = t.MetaPx, Ink = Ink.Muted, Alpha = 1, Group = g });
        }
        float py = MathF.Round(y + (rowH - pillH) / 2);
        ops.Add(new Op { Kind = OpKind.RoundRect, X = w - pillW, Y = py, W = pillW, H = pillH, Radius = pillH / 2, Ink = Ink.Accent, Alpha = 0.15f, Group = g });
        ops.Add(new Op { Kind = OpKind.Text, Text = e.License, X = w - pillW + th.S(9), Y = MathF.Round(py + (pillH - t.Pill.Height) / 2 + t.Pill.Ascender), Px = t.PillPx, Ink = Ink.Accent, Alpha = 1, Group = g });
        return y + (rowH + th.S(6));
    }

    /// <summary>The FreeType credit line, set off in an accent callout; returns the y below it.</summary>
    private static float AddLicenseCredit(in OpLayout l, string credit, int g, float w, float y, in LicenseType t)
    {
        var (dl, th, ops) = l;
        y += th.S(8);
        float pad = th.S(12);
        var lines = Wrap(dl, credit, t.MetaPx, w - 2 * pad - th.S(4));
        float boxH = MathF.Round(lines.Count * t.MetaLine + 2 * pad - (t.MetaLine - t.Meta.Height));
        ops.Add(new Op { Kind = OpKind.RoundRect, X = 0, Y = y, W = w, H = boxH, Radius = th.S(8), Ink = Ink.Accent, Alpha = 0.09f, Group = g });
        ops.Add(new Op { Kind = OpKind.RoundRect, X = 0, Y = y + th.S(8), W = th.S(3), H = boxH - th.S(16), Radius = th.S(1.5f), Ink = Ink.Accent, Alpha = 1, Group = g });
        float ly = y + pad;
        foreach (var line in lines)
        {
            ops.Add(new Op { Kind = OpKind.Text, Text = line, X = pad + th.S(4), Y = MathF.Round(ly + t.Meta.Ascender), Px = t.MetaPx, Ink = Ink.Fg, Alpha = 1, Group = g });
            ly += t.MetaLine;
        }
        return y + boxH;
    }

    private void LayoutFooter(DrawList dl, Theme th, float panelW)
    {
        float px = th.UiPx, bpx = px * 0.9f;
        float padX = th.S(28), gap = th.S(8);
        _footerH = MathF.Round(px * 3.4f);
        float h = MathF.Round(px * 2.2f);
        float copyW = dl.Measure(AboutInfo.Copyright, px * 0.85f) + th.S(24);

        Span<float> widths = stackalloc float[3];
        float total = 0;
        for (int i = 0; i < 3; i++)
        {
            widths[i] = MathF.Round(bpx * 1.4f + dl.Measure(_buttonLabels[i], bpx) + th.S(30));
            total += widths[i] + (i > 0 ? gap : 0);
        }
        float avail = panelW - 2 * padX;
        _showCopyright = total + copyW <= avail;
        _buttonsIconOnly = total > avail;
        if (_buttonsIconOnly)
        {
            total = 0;
            for (int i = 0; i < 3; i++) { widths[i] = h + th.S(6); total += widths[i] + (i > 0 ? gap : 0); }
        }
        float x = panelW - padX - total;
        float y = MathF.Round((_footerH - h) / 2);
        for (int i = 0; i < 3; i++)
        {
            _buttonRel[i] = new RectF(MathF.Round(x), y, widths[i], h);
            x += widths[i] + gap;
        }
    }

    private static int SectionIcon(AboutSectionKind kind) => kind switch
    {
        AboutSectionKind.Build => IconBuild,
        AboutSectionKind.System => IconSystem,
        AboutSectionKind.Display => IconDisplay,
        AboutSectionKind.Text => IconText,
        _ => IconSession,
    };

    /// <summary>Shortens a path in the middle so the file name stays visible: "~/.cache/…/erikterm.toml".</summary>
    private static string EllipsizePath(DrawList dl, string path, float px, float maxW)
    {
        if (dl.Measure(path, px) <= maxW) return path;
        int slash = path.LastIndexOfAny(['/', '\\']);
        if (slash <= 0) return dl.Ellipsize(path, px, maxW);
        string tail = path[slash..];
        float room = maxW - dl.Measure(tail, px) - dl.Measure("…", px);
        if (room <= 0) return dl.Ellipsize(path, px, maxW);
        var head = new System.Text.StringBuilder();
        float w = 0;
        foreach (var rune in path[..slash].EnumerateRunes())
        {
            float adv = dl.Measure(rune.ToString(), px);
            if (w + adv > room) break;
            head.Append(rune.ToString());
            w += adv;
        }
        return head.Append('…').Append(tail).ToString();
    }

    /// <summary>Greedy word wrap by measured width; over-long words (paths, URLs) break anywhere.</summary>
    private static List<string> Wrap(DrawList dl, string text, float px, float maxW)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        float lineW = 0, space = dl.Measure(" ", px);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            float ww = dl.Measure(word, px);
            if (line.Length > 0 && lineW + space + ww > maxW)
            {
                lines.Add(line.ToString());
                line.Clear();
                lineW = 0;
            }
            if (ww > maxW)
            {
                lineW = AppendBrokenWord(dl, word, px, maxW, lines, line, lineW);
                continue;
            }
            if (line.Length > 0) { line.Append(' '); lineW += space; }
            line.Append(word);
            lineW += ww;
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    /// <summary>A word too long for any line, rune by rune; returns the width of the line it ends on.</summary>
    private static float AppendBrokenWord(DrawList dl, string word, float px, float maxW, List<string> lines, System.Text.StringBuilder line, float lineW)
    {
        foreach (var rune in word.EnumerateRunes())
        {
            float rw = dl.Measure(rune.ToString(), px);
            if (lineW + rw > maxW && line.Length > 0) { lines.Add(line.ToString()); line.Clear(); lineW = 0; }
            line.Append(rune.ToString());
            lineW += rw;
        }
        return lineW;
    }

    // ---- Drawing ----------------------------------------------------------------------------

    private static uint Color(Ink ink, Theme th) => ink switch
    {
        Ink.Fg => th.PanelFg,
        Ink.Muted => th.Muted,
        Ink.Faint => th.PanelFg,
        Ink.Accent => th.Accent,
        Ink.Warning => th.Warning,
        _ => Colors.Mix(th.PanelFg, th.PanelBg, 0.80f),
    };

    /// <summary>Staggered entrance: each group fades and rises in a beat after the previous one.</summary>
    private float Stagger(int group, long now, out float rise)
    {
        if (_animMs <= 0 || !_open) { rise = 0; return 1; }
        float t = (now - _openedAt - group * 24f) / (_animMs * 2.2f);
        float e = Ease.OutCubic(t);
        rise = 1 - e;
        return e;
    }

    /// <summary>The card's geometry for this frame.</summary>
    private readonly record struct CardFrame(RectF Panel, RectF Shown, float Radius, float Ox, float Oy, float ContentAlpha);

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        float px = th.UiPx;
        float e = Ease.OutCubic(v);

        // Backdrop.
        dl.Rect(window, 0x000000, 0.36f * v);

        // Size: wide, capped by the window; compact header when the window is short.
        float margin = th.S(window.W < th.S(560) ? 10 : 24);
        float panelW = MathF.Round(MathF.Min(px * 56, window.W - 2 * margin));
        bool compact = window.H < px * 30;
#pragma warning disable S1244 // Cache keys: the stored values are exactly the last ones, and any change means lay out again.
        if (panelW != _layoutW || px != _layoutPx || compact != _layoutCompact || !ReferenceEquals(dl.Atlas, _layoutAtlas))
#pragma warning restore S1244
            Layout(dl, th, panelW, compact);

        float chrome = _heroH + _tabsH + _footerH + _bodyPadTop + _bodyPadBottom;
        float natural = chrome + _contentH[0];
        float panelH = MathF.Round(MathF.Min(natural, window.H - 2 * margin));
        var panel = new RectF(MathF.Round(window.X + (window.W - panelW) / 2), MathF.Round(window.Y + (window.H - panelH) * 0.46f), panelW, panelH);
        _panel = panel;

        // Open: scale up from 96% and rise a little; content follows slightly later.
        float scale = 0.96f + 0.04f * e;
        float rise = (1 - e) * th.S(18);
        var shown = new RectF(
            MathF.Round(panel.X + panel.W * (1 - scale) / 2), MathF.Round(panel.Y + panel.H * (1 - scale) / 2 + rise),
            MathF.Round(panel.W * scale), MathF.Round(panel.H * scale));
        float radius = th.S(16);
        Widgets.FloatingPanel(dl, th, shown, radius, v);

        float ca = Ease.OutCubic(Math.Clamp((v - 0.15f) / 0.85f, 0, 1)); // content alpha
        float ox = panel.X, oy = panel.Y + MathF.Round(rise);
        var card = new CardFrame(panel, shown, radius, ox, oy, ca);
        dl.PushClip(shown);

        DrawHero(dl, th, card, now);
        DrawTabs(dl, th, ox, oy, now, ca);
        DrawBody(dl, th, card, chrome, now);
        DrawFooter(dl, th, card, now);

        dl.PopClip();
    }

    /// <summary>The header wash, logo, wordmark and pills, and the close button in the corner.</summary>
    private void DrawHero(DrawList dl, Theme th, in CardFrame card, long now)
    {
        var shown = card.Shown;
        float ox = card.Ox, oy = card.Oy, ca = card.ContentAlpha;

        // Header wash: a faint accent tint over the hero + tabs.
        dl.PushClip(new RectF(shown.X, shown.Y, shown.W, _heroH + _tabsH + (oy - card.Panel.Y)));
        dl.RoundRect(shown.Inset(1), card.Radius - 1, th.Accent, 0.045f * ca);
        dl.PopClip();

        var logo = new RectF(ox + _logoRel.X, oy + _logoRel.Y, _logoRel.W, _logoRel.H);
        float la = Stagger(0, now, out float lr) * ca;
        logo = logo.Offset(0, MathF.Round(lr * th.S(10)));
        DrawLogo(dl, th, logo, la, now);

        foreach (ref readonly var op in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hero))
        {
            float a = Stagger(op.Group, now, out float r) * ca;
            DrawOp(dl, th, op, ox, oy + MathF.Round(r * th.S(10)), a);
        }

        DrawCloseX(dl, th, card, now);
    }

    private void DrawCloseX(DrawList dl, Theme th, in CardFrame card, long now)
    {
        float px = th.UiPx, ca = card.ContentAlpha;
        float cs = MathF.Round(px * 1.9f);
        var closeX = new RectF(card.Panel.Right - cs - th.S(12), card.Oy + th.S(12), cs, cs);
        _targets[(int)Target.CloseX] = closeX;
        float cg = _glow[(int)Target.CloseX].Value(now);
        if (cg > 0.001f) dl.RoundRect(closeX, cs / 2, Colors.Mix(th.PanelBg, th.PanelFg, 0.14f), cg * ca);
        float cxb = Widgets.Baseline(dl, px, closeX.Y, closeX.H);
        float cgw = dl.Atlas.Get(Icons.Close, FontStyle.Regular, px).Advance;
        dl.Glyph(Icons.Close, MathF.Round(closeX.X + (cs - cgw) / 2), cxb, px, Colors.Mix(th.Muted, th.PanelFg, cg), ca);
    }

    /// <summary>The scrolling content of the current tab, its soft edges and the scrollbar.</summary>
    private void DrawBody(DrawList dl, Theme th, in CardFrame card, float chrome, long now)
    {
        var panel = card.Panel;
        float ca = card.ContentAlpha;
        float bodyTop = card.Oy + _heroH + _tabsH;
        float bodyH = MathF.Max(0, panel.H - chrome + _bodyPadTop + _bodyPadBottom);
        var view = new RectF(panel.X + th.S(1), bodyTop, panel.W - th.S(2), bodyH);
        _bodyView = view;
        float contentH = _contentH[_tab];
        float viewInner = bodyH - _bodyPadTop - _bodyPadBottom;
        _maxScroll = MathF.Max(0, MathF.Round(contentH - viewInner));
        if (_scroll[_tab].Target > _maxScroll) _scroll[_tab].Snap(_maxScroll);
        float scroll = _scroll[_tab].Value(now);

        float fade = _tabFade.Value(now);
        float slide = (1 - Ease.OutCubic(fade)) * th.S(18) * _tabDir;
        float bx = panel.X + th.S(28) + MathF.Round(slide);
        float by = bodyTop + _bodyPadTop - MathF.Round(scroll);
        dl.PushClip(view);
        var body = new BodyOrigin(bx, by, ca, fade);
        DrawBodyOps(dl, th, view, in body, now);
        UpdateConfigLink(dl, th, in body, now);

        // Soft edges where content scrolls out of view.
        float edge = th.S(22);
        if (scroll > 0.5f) EdgeFade(dl, th, new RectF(view.X, view.Y, view.W, edge), down: true, MathF.Min(1, scroll / edge) * ca);
        if (scroll < _maxScroll - 0.5f) EdgeFade(dl, th, new RectF(view.X, view.Bottom - edge, view.W, edge), down: false, MathF.Min(1, (_maxScroll - scroll) / edge) * ca);
        dl.PopClip();

        if (_maxScroll > 0)
        {
            float trackH = viewInner + _bodyPadTop + _bodyPadBottom - th.S(16);
            float thumbH = MathF.Max(th.S(28), trackH * viewInner / contentH);
            float thumbY = view.Y + th.S(8) + (trackH - thumbH) * (scroll / _maxScroll);
            dl.RoundRect(new RectF(view.Right - th.S(8), thumbY, th.S(4), thumbH), th.S(2), th.Muted, 0.45f * ca);
        }
    }

    /// <summary>Where the scrolled body content starts (<see cref="X"/>, <see cref="Y"/>), the card's content alpha and the tab-switch fade.</summary>
    private readonly record struct BodyOrigin(float X, float Y, float ContentAlpha, float Fade);

    private void DrawBodyOps(DrawList dl, Theme th, RectF view, in BodyOrigin body, long now)
    {
        float bx = body.X, by = body.Y, ca = body.ContentAlpha, fade = body.Fade;
        foreach (ref readonly var op in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_body[_tab]))
        {
            float top = by + op.Y - (op.Kind is OpKind.Text or OpKind.Glyph ? op.Px * 1.2f : 0);
            float bottom = by + op.Y + (op.Kind is OpKind.Text or OpKind.Glyph ? op.Px * 0.5f : op.H);
            if (bottom < view.Y || top > view.Bottom) continue;
            float a = Stagger(op.Group + 4, now, out float r) * ca * fade;
            DrawOp(dl, th, op, bx, by + MathF.Round(r * th.S(10)), a);
        }
    }

    /// <summary>The config link's click target, and its hover underline / focus.</summary>
    private void UpdateConfigLink(DrawList dl, Theme th, in BodyOrigin body, long now)
    {
        float bx = body.X, by = body.Y, ca = body.ContentAlpha, fade = body.Fade;
        if (_tab != 0 || _linkRel.W <= 0)
        {
            _targets[(int)Target.ConfigLink] = default;
            return;
        }
        var link = new RectF(bx + _linkRel.X, by + _linkRel.Y, _linkRel.W, _linkRel.H);
        _targets[(int)Target.ConfigLink] = link;
        float lg = _glow[(int)Target.ConfigLink].Value(now);
        if (lg > 0.001f) dl.RoundRect(link, th.S(6), th.Accent, 0.12f * lg * ca * fade);
    }

    private void DrawFooter(DrawList dl, Theme th, in CardFrame card, long now)
    {
        var panel = card.Panel;
        var shown = card.Shown;
        float px = th.UiPx, ca = card.ContentAlpha;
        float footTop = MathF.Round(card.Oy + panel.H - _footerH);
        float fa = Stagger(6, now, out float fr) * ca;
        footTop += MathF.Round(fr * th.S(6));
        dl.PushClip(new RectF(shown.X, footTop, shown.W, shown.Bottom - footTop));
        dl.RoundRect(shown.Inset(1), card.Radius - 1, 0x000000, 0.12f * fa);
        dl.PopClip();
        dl.Rect(new RectF(panel.X + th.S(1), footTop, panel.W - th.S(2), MathF.Max(1, MathF.Round(th.S(1)))), th.Border, fa);
        if (_showCopyright)
        {
            float b = Widgets.Baseline(dl, px * 0.85f, footTop, _footerH);
            dl.Text(AboutInfo.Copyright, panel.X + th.S(28), b, px * 0.85f, th.Muted, fa);
        }
        for (int i = 0; i < 3; i++)
        {
            var target = Target.Copy + i;
            var r = _buttonRel[i];
            var br = new RectF(panel.X + r.X, footTop + r.Y, r.W, r.H);
            _targets[(int)target] = br;
            DrawButton(dl, th, br, i, _glow[(int)target].Value(now), _focusVisible && _focus == target, fa);
        }
    }

    private static void DrawOp(DrawList dl, Theme th, in Op op, float ox, float oy, float a)
    {
        if (a <= 0.002f) return;
        uint c = Color(op.Ink, th);
        switch (op.Kind)
        {
            case OpKind.Text: dl.Text(op.Text!, ox + op.X, oy + op.Y, op.Px, c, a * op.Alpha, op.Style); break;
            case OpKind.Glyph: dl.Glyph(op.Code, ox + op.X, oy + op.Y, op.Px, c, a * op.Alpha, op.Style); break;
            case OpKind.Rect: dl.Rect(new RectF(ox + op.X, oy + op.Y, op.W, op.H), c, a * op.Alpha); break;
            case OpKind.RoundRect: dl.RoundRect(new RectF(ox + op.X, oy + op.Y, op.W, op.H), op.Radius, c, a * op.Alpha); break;
        }
    }

    private static void EdgeFade(DrawList dl, Theme th, RectF band, bool down, float a)
    {
        float x = band.X, y = band.Y, h = band.H;
        const int Bands = 8;
        float bh = h / Bands;
        for (int i = 0; i < Bands; i++)
        {
            float t = (i + 0.5f) / Bands;               // 0 at the top band
            float alpha = down ? 1 - t : t;             // opaque at the view's edge
            float y0 = MathF.Round(y + i * bh), y1 = MathF.Round(y + (i + 1) * bh);
            dl.Rect(x, y0, band.Right, y1, th.PanelBg, alpha * alpha * 0.92f * a);
        }
    }

    private void DrawTabs(DrawList dl, Theme th, float ox, float oy, long now, float ca)
    {
        float a = Stagger(3, now, out float r) * ca;
        float dy = MathF.Round(r * th.S(8));
        for (int i = 0; i < 2; i++)
        {
            var rel = _tabRel[i];
            DrawTab(dl, th, i, new RectF(ox + rel.X, oy + rel.Y + dy, rel.W, rel.H), a, now);
        }
        DrawTabUnderline(dl, th, ox, oy, a, now, ca);
    }

    /// <summary>Tab <paramref name="i"/>'s label and icon in <paramref name="rect"/>, with its hover and focus backgrounds.</summary>
    private void DrawTab(DrawList dl, Theme th, int i, RectF rect, float a, long now)
    {
        float px = th.UiPx, tpx = px * 0.92f;
        var target = i == 0 ? Target.TabOverview : Target.TabLicenses;
        _targets[(int)target] = rect;
        bool active = i == _tab;
        float g = _glow[(int)target].Value(now);
        float b = Widgets.Baseline(dl, tpx, rect.Y, rect.H - th.S(2));
        bool focused = _focusVisible && _focus == target;
        float bg = MathF.Max(g * 0.6f, focused ? 1 : 0);
        if (bg > 0.001f) dl.RoundRect(new RectF(rect.X + th.S(2), rect.Y + th.S(7), rect.W - th.S(4), rect.H - th.S(14)), th.S(7), focused ? th.Accent : th.PanelFg, (focused ? 0.16f : 0.07f) * bg * a);
        float x = rect.X + th.S(10);
        uint fg = active ? th.PanelFg : Colors.Mix(th.Muted, th.PanelFg, g * 0.7f);
        dl.Glyph(_tabIcons[i], x, b, tpx, active ? th.Accent : fg, a);
        dl.Text(_tabLabels[i], x + px * 1.55f, b, tpx, fg, a, active ? FontStyle.Bold : FontStyle.Regular);
    }

    /// <summary>The line under the tabs, and the accent bar sliding along it under the active tab.</summary>
    private void DrawTabUnderline(DrawList dl, Theme th, float ox, float oy, float a, long now, float ca)
    {
        var at = _tabRel[_tab];
        float ix = at.X + th.S(8), iw = at.W - th.S(16);
        if (!_indicatorPlaced) { _indicatorX.Snap(ix); _indicatorW.Snap(iw); _indicatorPlaced = true; }
        else { _indicatorX.Set(ix, now, _animMs * 1.2f); _indicatorW.Set(iw, now, _animMs * 1.2f); }
        float lineY = oy + _heroH + _tabsH;
        float h = MathF.Max(2, MathF.Round(th.S(2)));
        dl.Rect(new RectF(_panel.X + th.S(1), lineY - MathF.Max(1, MathF.Round(th.S(1))), _panel.W - th.S(2), MathF.Max(1, MathF.Round(th.S(1)))), th.Border, ca);
        dl.RoundRect(new RectF(ox + _indicatorX.Value(now), lineY - h, _indicatorW.Value(now), h), h / 2, th.Accent, a);
    }

    /// <summary>Footer button <paramref name="index"/> (Copy, Open config, Close: the last is the primary one).</summary>
    private void DrawButton(DrawList dl, Theme th, RectF r, int index, float hover, bool focused, float a)
    {
        bool primary = Target.Copy + index == Target.Close;

        float px = th.UiPx * 0.9f;
        float radius = th.S(8);
        if (focused) dl.RoundRect(new RectF(r.X - th.S(3), r.Y - th.S(3), r.W + th.S(6), r.H + th.S(6)), radius + th.S(3), th.Accent, 0.45f * a);

        uint fg;
        if (primary)
        {
            uint fill = Colors.Mix(th.Accent, 0xFFFFFF, 0.12f * hover);
            dl.Shadow(r.Offset(0, th.S(2)), radius, th.S(8), (0.25f + 0.2f * hover) * a, th.Accent);
            dl.RoundRect(r, radius, fill, a);
            fg = Colors.Luminance(th.Accent) > 0.5f ? Colors.Mix(th.PanelBg, 0x000000, 0.35f) : 0xFFFFFF;
        }
        else
        {
            uint fill = Colors.Mix(th.PanelBg, th.PanelFg, 0.06f + 0.07f * hover);
            dl.Panel(r, radius, fill, a, Colors.Mix(Colors.Mix(th.PanelBg, th.PanelFg, 0.16f), th.Accent, 0.55f * hover), a);
            fg = Colors.Mix(Colors.Mix(th.PanelFg, th.PanelBg, 0.12f), th.PanelFg, hover);
        }

        float b = Widgets.Baseline(dl, px, r.Y, r.H);
        int icon = _buttonIcons[index];
        if (_buttonsIconOnly)
        {
            float gw = dl.Atlas.Get(icon, FontStyle.Regular, px).Advance;
            dl.Glyph(icon, MathF.Round(r.X + (r.W - gw) / 2), b, px, fg, a);
            return;
        }
        float x = r.X + th.S(14);
        dl.Glyph(icon, x, b, px, primary ? fg : Colors.Mix(th.Muted, th.Accent, hover), a);
        dl.Text(_buttonLabels[index], x + px * 1.4f, b, px, fg, a, primary ? FontStyle.Bold : FontStyle.Regular);
    }

    // The app icon's colors (assets/icon/erikterm.svg, Catppuccin Macchiato): the logo is the brand mark, not themed.
    private const uint LogoTop = 0x363A4F, LogoBottom = 0x1E2030, LogoLavender = 0xB7BDF8, LogoBlue = 0x8AADF4;

    /// <summary>
    /// The app icon (assets/icon/erikterm.svg) drawn to scale: a deep rounded tile with a vertical gradient (clip
    /// bands of the same rounded rect) and a hairline lavender rim, a round-capped "❯" chevron and a slim caret that
    /// breathes. Coordinates are the SVG's 64-unit grid, whose tile spans 4..60.
    /// </summary>
    private void DrawLogo(DrawList dl, Theme th, RectF tile, float a, long now)
    {
        if (a <= 0.002f) return;
        float u = tile.W / 56f;
        float X(float x) => tile.X + (x - 4) * u;
        float radius = MathF.Round(14 * u);

        dl.Shadow(tile.Offset(0, th.S(6)), radius, th.S(14), 0.45f * a);                     // drop shadow

        // Rim: the tile gradient under 45% lavender, then the face inset by the rim width.
        float rim = MathF.Max(1, MathF.Round(u));
        GradientRoundRect(dl, tile, radius, Colors.Mix(LogoTop, LogoLavender, 0.45f), Colors.Mix(LogoBottom, LogoLavender, 0.45f), a);
        GradientRoundRect(dl, tile.Inset(rim), radius - rim, LogoTop, LogoBottom, a);

        // Chevron M20 21 L32 32 L20 43: two capsules meeting at the apex, each clipped to its side of the apex row so
        // the joint isn't painted twice (that would show while the logo fades in).
        float ax = X(32), ay = MathF.Round(tile.Y + 28 * u), ex = X(20), reach = 11 * u, stroke = 6 * u;
        dl.PushClip(new RectF(tile.X, tile.Y, tile.W, ay - tile.Y));
        dl.Capsule(ex, ay - reach, ax, ay, stroke, LogoLavender, a);
        dl.PopClip();
        dl.PushClip(new RectF(tile.X, ay, tile.W, tile.Bottom - ay));
        dl.Capsule(ax, ay, ex, ay + reach, stroke, LogoLavender, a);
        dl.PopClip();

        // Caret: x 39..44, y 19..45; it breathes gently, staying blue enough to read as the icon's caret.
        float phase = (now - _openedAt) % 1400 / 1400f;
        float breathe = 0.7f + 0.3f * (0.5f + 0.5f * MathF.Cos(phase * MathF.Tau));
        float cw = MathF.Max(2, MathF.Round(5 * u)), ch = MathF.Round(26 * u);
        var caret = new RectF(MathF.Round(X(39)), MathF.Round(ay - ch / 2), cw, ch);
        dl.RoundRect(caret, cw / 2, LogoBlue, breathe * a);
    }

    /// <summary>A vertical two-color gradient in a rounded rectangle, drawn as horizontal clip bands.</summary>
    private static void GradientRoundRect(DrawList dl, RectF r, float radius, uint top, uint bottom, float a)
    {
        int bands = Math.Clamp((int)(r.H / 4), 4, 24);
        float y0 = MathF.Floor(r.Y) - 2; // include the anti-aliased edge
        for (int i = 0; i < bands; i++)
        {
            float y1 = i == bands - 1 ? MathF.Ceiling(r.Bottom) + 1 : MathF.Round(r.Y + r.H * (i + 1) / bands);
            dl.PushClip(new RectF(r.X - 2, y0, r.W + 4, y1 - y0));
            dl.RoundRect(r, radius, Colors.Mix(top, bottom, (i + 0.5f) / bands), a);
            dl.PopClip();
            y0 = y1;
        }
    }
}
