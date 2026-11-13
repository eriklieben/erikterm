using ErikTerm.App.Rendering;
using ErikTerm.Config;

namespace ErikTerm.App.Ui;

/// <summary>Every color and size the UI chrome uses, derived from settings + the active color scheme.</summary>
internal sealed class Theme
{
    public float Scale = 1;              // device pixels per point
    public uint Background, Foreground, Accent, Split, Selection;
    /// <summary>Selected text color (selection_fg; null keeps the text's own colors) and the unfocused cursor outline (cursor_border).</summary>
    public uint? SelectionFg, CursorBorder;
    public uint PanelBg, PanelFg, Muted, Border, Danger, Success, Warning, Info;
    public uint PaneSelectBg, PaneSelectFg;
    public (float H, float S, float B) InactiveHsb = (1, 0.9f, 0.8f);
    public float TextBackgroundOpacity = 1, WindowOpacity = 1;
    public float UiPx = 16, PaneSelectPx = 48;
    public int PaletteRows = 12;
    public float AnimationMs = 160;
    public bool VisualBell = true;
    /// <summary>minimum_contrast: text against its cell background (1 = off; PaneRenderer, Rendering/Contrast.cs) — the config's, else the scheme's default.</summary>
    public float MinimumContrast = 1;
    /// <summary>
    /// The scheme's floor for text in an inactive pane, applied after the dimming (1 = none): only when minimum_contrast
    /// comes from the scheme, not from the config.
    /// </summary>
    public float InactiveMinimumContrast = 1;
    public IReadOnlyList<string> HarfbuzzFeatures = FontFeatures.Default;

    /// <summary>high_contrast is on (AccessibilityPolicy): the chrome's colours were strengthened.</summary>
    public bool HighContrast;

    // The tab bar (Ui/TabBar.cs): its pills are the foreground (inactive, hovered, the + button) or the active fill drawn
    // see-through over the background; the active tab's outline is drawn at OutlineAlpha.
    public const float TabTint = 0.045f, TabHoverTint = 0.105f, ButtonTint = 0.03f, ButtonHoverTint = 0.11f;
    public const float ActiveFillAlpha = 0.92f, OutlineAlpha = 0.55f, FadedBadgeAlpha = 0.75f;

    /// <summary>
    /// How see-through repository badges are on an inactive tab: <see cref="FadedBadgeAlpha"/>, or more opaque where
    /// their text couldn't reach 4.5:1 otherwise.
    /// </summary>
    public float BadgeAlpha = FadedBadgeAlpha;

    /// <summary>The active tab's fill: the accent over the panel on a dark scheme, over the background on a light one.</summary>
    public uint TabActiveFill;

    /// <summary>The active tab's outline: the accent, at 3:1 against the bar once drawn at <see cref="OutlineAlpha"/>.</summary>
    public uint TabOutline;

    /// <summary>
    /// What the tab bar draws with: this theme with its text colours (PanelFg, Muted, Accent) at 4.5:1 and its status
    /// colours at 3:1 against every tab pill, moved toward white or black only as far as needed (Contrast.AdjustOver).
    /// </summary>
    public Theme Tabs => _tabs ?? this;
    private Theme? _tabs;

    public static Theme From(Settings s, ColorScheme colors, float scale, bool highContrast = false)
    {
        var t = new Theme { Scale = scale };
        t.Background = colors.Background;
        t.Foreground = colors.Foreground;
        t.Accent = colors.Accent;
        t.Split = colors.Split;
        t.Selection = colors.SelectionBg;
        t.SelectionFg = colors.SelectionFg;
        t.CursorBorder = colors.CursorBorder;
        t.Danger = colors.Ansi[1];
        t.Success = colors.Ansi[2];
        t.Warning = colors.Ansi[3];
        t.Info = colors.Ansi[4]; // blue: an agent finished, not yet seen

        // A panel a shade darker than the terminal (Catppuccin's "mantle" relationship).
        t.PanelBg = s.CommandPaletteBgColor ?? Colors.Mix(colors.Background, 0x000000, 0.28f);
        t.PanelFg = s.CommandPaletteFgColor ?? colors.Foreground;
        t.Muted = Colors.Mix(t.PanelFg, t.PanelBg, 0.42f);
        t.Border = Colors.Mix(t.PanelFg, t.PanelBg, 0.86f);

        t.PaneSelectBg = s.PaneSelectBgColor ?? colors.Accent;
        t.PaneSelectFg = s.PaneSelectFgColor ?? (Colors.Luminance(t.PaneSelectBg) > 0.5f ? Colors.Mix(colors.Background, 0, 0.3f) : 0xFFFFFF);

        t.InactiveHsb = ((float)s.InactivePaneHsb.Hue, (float)s.InactivePaneHsb.Saturation, (float)s.InactivePaneHsb.Brightness);
        t.TextBackgroundOpacity = (float)s.TextBackgroundOpacity;
        t.WindowOpacity = (float)s.WindowBackgroundOpacity;
        t.UiPx = (float)s.CommandPaletteFontSize * 96f / 72f * scale;
        t.PaneSelectPx = (float)s.PaneSelectFontSize * 96f / 72f * scale;
        t.PaletteRows = s.CommandPaletteRows;
        t.AnimationMs = s.AnimationDurationMs;
        t.VisualBell = s.VisualBell;
        t.MinimumContrast = (float)s.MinimumContrastFor(colors);
        t.InactiveMinimumContrast = s.MinimumContrast is null ? (float)colors.InactiveMinimumContrast : 1;
        t.HarfbuzzFeatures = s.HarfbuzzFeatures;
        if (highContrast) t.StrengthenContrast();
        t.DeriveTabs();
        return t;
    }

    /// <summary>The tab pills as drawn over the background: inactive, hovered, active, and the + / "more" buttons.</summary>
    public (uint Tab, uint Hover, uint Active, uint Button) TabBackgrounds() =>
        (Colors.Mix(Background, Foreground, TabTint), Colors.Mix(Background, Foreground, TabHoverTint),
         Colors.Mix(Background, TabActiveFill, ActiveFillAlpha), Colors.Mix(Background, Foreground, ButtonTint));

    private void DeriveTabs()
    {
        bool light = Contrast.Luminance(Background) > Contrast.Luminance(Foreground);
        TabActiveFill = Colors.Mix(light ? Background : PanelBg, Accent, 0.20f);
        uint tab = TabBackgrounds().Tab;
        float alpha = FadedBadgeAlpha;
        var k = TabColors(alpha);
        while (alpha < 1 && !BadgesReadable(k, tab, alpha))
        {
            alpha = MathF.Min(1, alpha + 0.05f);
            k = TabColors(alpha);
        }
        BadgeAlpha = alpha;
        TabOutline = k.TabOutline;
        k.BadgeAlpha = alpha;
        k._tabs = null; // its own tab colours
        _tabs = k;
    }

    /// <summary>Whether <paramref name="k"/>'s badges, drawn at <paramref name="alpha"/> on a tab pill, read at 4.5:1.</summary>
    private static bool BadgesReadable(Theme k, uint tab, float alpha) =>
        Contrast.Ratio(Colors.Mix(tab, k.Accent, alpha), tab) >= 4.5f &&
        Contrast.Ratio(Colors.Mix(tab, Colors.Mix(k.PanelFg, k.Muted, 0.35f), alpha), tab) >= 4.5f &&
        Contrast.Ratio(Colors.Mix(tab, Colors.Mix(k.PanelFg, k.Accent, 0.35f), alpha), tab) >= 4.5f;

    /// <summary>This theme's colours moved just far enough to read on every tab pill, with badges at <paramref name="badgeAlpha"/>.</summary>
    private Theme TabColors(float badgeAlpha)
    {
        var (tab, hover, active, button) = TabBackgrounds();
        uint[] pills = [tab, hover, active, button];
        var k = (Theme)MemberwiseClone();
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (uint pill in pills)
            {
                k.PanelFg = Contrast.Adjust(k.PanelFg, pill, 4.5f);
                k.Muted = Contrast.Adjust(k.Muted, pill, 4.5f);
                k.Accent = Contrast.Adjust(k.Accent, pill, 4.5f);
                k.Danger = Contrast.AdjustOver(k.Danger, pill, badgeAlpha, 3);
                k.Warning = Contrast.AdjustOver(k.Warning, pill, badgeAlpha, 3);
                k.Success = Contrast.AdjustOver(k.Success, pill, badgeAlpha, 3);
                k.Info = Contrast.AdjustOver(k.Info, pill, badgeAlpha, 3);
            }
            // Repository badges are see-through on an inactive tab; the active tab's outline on the bar's background.
            k.PanelFg = Contrast.AdjustOver(k.PanelFg, tab, badgeAlpha, 4.5f);
            k.Muted = Contrast.AdjustOver(k.Muted, tab, badgeAlpha, 4.5f);
            k.Accent = Contrast.AdjustOver(k.Accent, tab, badgeAlpha, 4.5f);
        }
        k.TabOutline = Contrast.AdjustOver(Accent, Background, OutlineAlpha, 3);
        return k;
    }

    /// <summary>
    /// high_contrast: the chrome's colours pushed (toward white or black, keeping their hue) to WCAG levels — text 7:1
    /// against its panel, secondary text 4.5:1, and outlines, dividers, the accent of focus marks and status colours
    /// 3:1 (non-text contrast) against what they're drawn on.
    /// </summary>
    private void StrengthenContrast()
    {
        HighContrast = true;
        PanelFg = Contrast.Adjust(PanelFg, PanelBg, 7);
        Muted = Contrast.Adjust(Muted, PanelBg, 4.5f);
        Border = Contrast.Adjust(Border, PanelBg, 3);
        Split = Contrast.Adjust(Split, Background, 3);
        Accent = Contrast.Adjust(Accent, Background, 3);
        Danger = Contrast.Adjust(Danger, PanelBg, 4.5f);
        Success = Contrast.Adjust(Success, PanelBg, 4.5f);
        Warning = Contrast.Adjust(Warning, PanelBg, 4.5f);
        Info = Contrast.Adjust(Info, PanelBg, 4.5f);
        PaneSelectFg = Contrast.Adjust(PaneSelectFg, PaneSelectBg, 7);
    }

    public float S(float points) => points * Scale;

    /// <summary>A copy for a window's overlays at its font zoom (OverlayZoom): text and spacing grow together.</summary>
    public Theme WithOverlayZoom(float zoom)
    {
        var t = (Theme)MemberwiseClone();
        t.Scale = Scale * zoom;
        t.UiPx = UiPx * zoom;
        t.PaneSelectPx = PaneSelectPx * zoom;
        t._tabs = _tabs?.WithOverlayZoom(zoom);
        return t;
    }
}

/// <summary>Nerd Font Codicons used by the UI (verified against JetBrainsMono Nerd Font).</summary>
internal static class Icons
{
    public const int Search = 0xEA6D;
    public const int SplitRight = 0xEB56;
    public const int SplitDown = 0xEB57;
    public const int Close = 0xEA76;
    public const int Copy = 0xEBCC;
    public const int Paste = 0xEAC0;
    public const int ZoomIn = 0xEB4C;      // screen-full
    public const int ZoomOut = 0xEB4D;     // screen-normal
    public const int Terminal = 0xEA85;
    public const int Color = 0xEB5C;       // palette
    public const int Window = 0xEB7F;
    public const int Gear = 0xEB51;
    public const int Refresh = 0xEB37;
    public const int FontBigger = 0xEB81;  // zoom-in magnifier
    public const int FontSmaller = 0xEB82;
    public const int Clear = 0xEABF;       // clear-all
    public const int Layout = 0xEBEB;
    public const int Eye = 0xEA70;
    public const int Top = 0xEAF4;         // double chevron up
    public const int Bottom = 0xEAF3;
    public const int Add = 0xEA60;
    public const int ArrowLeft = 0xEA9B, ArrowRight = 0xEA9C, ArrowUp = 0xEAA1, ArrowDown = 0xEA9A;
    public const int ResizeH = 0xEA99, ResizeV = 0xEB73;
    public const int Info = 0xEA74;
    public const int Check = 0xEBB3;       // filled check circle
    public const int Warning = 0xEA6C;
    public const int Error = 0xEA87;
    public const int Keyboard = 0xEA65;
    public const int SelectAll = 0xEB67;   // checklist
    public const int Edit = 0xEA73;        // pencil
    public const int Sparkle = 0xEC10;     // coding agents
    public const int ChevronDown = 0xEAB4; // the tab bar's "more" button
    public const int Bell = 0xEAA2;        // notifications
    public const int BellDot = 0xEB9A;     // unread notifications
    public const int BellOff = 0xEC08;     // bell-slash: desktop notifications off

    // Caption buttons for a borderless window (Ui/WindowControls.cs). Close reuses the chrome-close cross above.
    public const int ChromeMinimize = 0xEABA;
    public const int ChromeMaximize = 0xEAB9;
    public const int ChromeRestore = 0xEABB;
    public const int ChromeClose = Close;
    public const int Swatch = 0xEBB4;      // circle-large-filled: a color swatch (Tab Color entries)
    public const int History = 0xEA82;     // recent commands
    public const int Folder = 0xEA83;      // recent directories
    public const int Rerun = 0xEBC0;       // run a command again
    public const int Snippet = 0xEB66;
    public const int Send = 0xEC0F;        // send to agent
    public const int Broadcast = 0xEAAD;
    public const int CloudDownload = 0xEAC2;
    public const int Container = 0xF01A7;  // md cube-outline: a tab whose shell runs in a (dev) container
}
