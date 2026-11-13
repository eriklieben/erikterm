using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>The list of built-in color schemes (ColorSchemes.All, Schemes/*.cs).</summary>
public class BuiltInSchemeTests
{
    [Test]
    public async Task NamesAreUniqueAndEachIsFoundByIt()
    {
        // Act
        var duplicates = ColorSchemes.All.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var notFound = ColorSchemes.All.Where(s => !ReferenceEquals(ColorSchemes.Find(s.Name), s)).Select(s => s.Name).ToList();

        // Assert
        await Assert.That(duplicates).IsEmpty();
        await Assert.That(notFound).IsEmpty();
    }

    [Test]
    public async Task ErikComesFirstAndTheRestByName()
    {
        // Act
        var rest = ColorSchemes.All.Skip(1).Select(s => s.Name).ToList();

        // Assert
        await Assert.That(ColorSchemes.All[0]).IsSameReferenceAs(ColorSchemes.Erik);
        await Assert.That(rest).IsEquivalentTo(rest.Order(StringComparer.OrdinalIgnoreCase), CollectionOrdering.Matching);
    }

    [Test]
    public async Task PalettesAreComplete()
    {
        // Act
        var incomplete = ColorSchemes.All
            .Where(s => s.Ansi.Length != 8 || s.Brights.Length != 8 || s.Name.Trim().Length == 0 ||
                        s.Ansi.Concat(s.Brights).Append(s.Foreground).Append(s.Background).Any(c => c > 0xFFFFFF))
            .Select(s => s.Name).ToList();

        // Assert
        await Assert.That(incomplete).IsEmpty();
    }

    [Test]
    public async Task AccentsStandOutFromTheBackground()
    {
        // Act: the accent colours tabs and focus marks (WCAG non-text contrast, 3:1)
        var faint = ColorSchemes.All.Where(s => Contrast.Ratio(s.Accent, s.Background) < 3f)
            .Select(s => $"{s.Name} {Contrast.Ratio(s.Accent, s.Background):0.00}").ToList();

        // Assert
        await Assert.That(faint).IsEmpty();
    }

    [Test]
    public async Task EveryBuiltInCarriesTheContrastDefault()
    {
        // Act
        var without = ColorSchemes.All.Where(s => s.MinimumContrast < ColorSchemes.BuiltInMinimumContrast).Select(s => s.Name).ToList();

        // Assert
        await Assert.That(without).IsEmpty();
    }

    [Test]
    public async Task CreditsAreComplete()
    {
        // Act
        string[] licenses = [SchemeLicenses.Mit, SchemeLicenses.Apache2, SchemeLicenses.Bsd3];
        var incomplete = ColorSchemes.All.Where(s => s.Credit is { } c && (c.Project.Length == 0 || c.Copyright.Length == 0 ||
                                                     !licenses.Contains(c.License) || !c.Url.StartsWith("https://", StringComparison.Ordinal)))
            .Select(s => s.Name).ToList();

        // Assert
        await Assert.That(incomplete).IsEmpty();
    }

    [Test]
    public async Task EveryCreditIsInTheAboutLicenses()
    {
        // Arrange
        var list = new List<LicenseEntry>();

        // Act
        AboutLicenses.AddSchemeCredits(list, ColorSchemes.All);
        var missing = ColorSchemes.All.Where(s => s.Credit is not null && !list.Any(e => e.Paragraphs[0].Contains(s.Name, StringComparison.Ordinal)))
            .Select(s => s.Name).ToList();

        // Assert
        await Assert.That(missing).IsEmpty();
        await Assert.That(list.Select(e => e.Name).Distinct().Count()).IsEqualTo(list.Count);
    }

    [Test]
    public async Task EveryCreditIsInTheThirdPartyNotices()
    {
        // Arrange
        string notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.md"));

        // Act
        var missing = ColorSchemes.All.Where(s => s.Credit is { } c && (!notices.Contains($"*{s.Name}*", StringComparison.Ordinal) ||
                                                                        !notices.Contains(c.Url, StringComparison.Ordinal)))
            .Select(s => s.Name).ToList();

        // Assert
        await Assert.That(missing).IsEmpty();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "THIRD-PARTY-NOTICES.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>
/// Readability of every built-in scheme as erikterm draws it: text is adjusted against its cell's background
/// (minimum_contrast; the scheme's default when the config sets none), then the focused pane draws it as is; an
/// inactive pane dims the text and coloured cell backgrounds (inactive_pane_hsb, default) but not the window's
/// background, and holds the scheme's inactive floor against that (PaneRenderer.ForegroundOf). Targets: 4.5:1 (WCAG AA)
/// focused, 3:1 dimmed.
/// </summary>
public class BuiltInSchemeContrastTests
{
    private const float Focused = 4.5f, Dimmed = 3f;

    public static IEnumerable<Func<string>> Schemes() => ColorSchemes.All.Select(s => (Func<string>)(() => s.Name));

    /// <summary>The (text, background) pairs that 16-colour statuslines and prompts draw.</summary>
    internal static IEnumerable<(string What, uint Fg, uint Bg)> StatuslinePairs(ColorScheme s)
    {
        uint[] p = [.. s.Ansi, .. s.Brights];
        // vim 16-colour statuslines (lightline/airline "16color") and powerline-style prompts: black on a colour block
        for (int i = 1; i < 16; i++)
            if (i != 8) yield return ($"black on colour {i}", p[0], p[i]);
        // the file name segment: white and bright white on bright black
        yield return ("white on bright black", p[7], p[8]);
        yield return ("bright white on bright black", p[15], p[8]);
        yield return ("bright white on black", p[15], p[0]);
        // the file type / encoding segment: bright black text on the background
        yield return ("bright black on the background", p[8], s.Background);
        yield return ("body text", s.Foreground, s.Background);
        yield return ("reverse video", s.Background, s.Foreground);
    }

    /// <summary>The contrast of <paramref name="fg"/> on <paramref name="bg"/> as a focused and as an inactive pane draws it.</summary>
    internal static (float Focused, float Dimmed) AsDrawn(uint fg, uint bg, Theme theme)
    {
        uint text = theme.MinimumContrast > 1 ? Contrast.Adjust(fg, bg, theme.MinimumContrast) : fg;
        var (h, sat, b) = theme.InactiveHsb;
        uint dimmedText = Colors.AdjustHsb(text, h, sat, b);
        uint dimmedBg = bg == theme.Background ? bg : Colors.AdjustHsb(bg, h, sat, b); // the window background isn't dimmed
        if (theme.InactiveMinimumContrast > 1) dimmedText = Contrast.Adjust(dimmedText, dimmedBg, theme.InactiveMinimumContrast);
        return (Contrast.Ratio(text, bg), Contrast.Ratio(dimmedText, dimmedBg));
    }

    private static Theme ThemeOf(string name, Settings? settings = null)
    {
        var scheme = ColorSchemes.Find(name)!;
        return Theme.From((settings ?? new Settings()) with { Colors = scheme }, scheme, 1);
    }

    private static List<string> Failures(Theme theme, IEnumerable<(string What, uint Fg, uint Bg)> pairs) =>
        [.. pairs.Select(p => (p.What, Ratio: AsDrawn(p.Fg, p.Bg, theme)))
            .Where(p => p.Ratio.Focused < Focused || p.Ratio.Dimmed < Dimmed)
            .Select(p => $"{p.What}: {p.Ratio.Focused:0.00} / {p.Ratio.Dimmed:0.00}")];

    [Test]
    [MethodDataSource(nameof(Schemes))]
    public async Task StatuslineBlocksAreReadable(string name)
    {
        // Arrange
        var theme = ThemeOf(name);

        // Act
        var failures = Failures(theme, StatuslinePairs(ColorSchemes.Find(name)!));

        // Assert
        await Assert.That(failures).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Schemes))]
    public async Task AnyAnsiColourIsReadableOnAnyOther(string name)
    {
        // Arrange
        var s = ColorSchemes.Find(name)!;
        var theme = ThemeOf(name);
        uint[] p = [.. s.Ansi, .. s.Brights, s.Foreground, s.Background];

        // Act: every pair of the 16 colours plus the default text and background, either way round (bold is a bright pair)
        var pairs = from i in Enumerable.Range(0, p.Length)
                    from j in Enumerable.Range(0, p.Length)
                    where i != j && p[i] != p[j]
                    select ($"{i} on {j}", p[i], p[j]);
        var failures = Failures(theme, pairs);

        // Assert
        await Assert.That(failures).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Schemes))]
    public async Task ErikTermOriginalsReadWithoutTheContrastDefault(string name)
    {
        // Arrange: erikterm's own schemes (no credit) are designed to the targets in their palette itself
        var s = ColorSchemes.Find(name)!;
        if (s.Credit is not null) return;
        var theme = ThemeOf(name, new Settings { MinimumContrast = 1 });
        uint[] p = [.. s.Ansi, .. s.Brights];
        bool dark = Contrast.Luminance(s.Background) < Contrast.Luminance(s.Foreground);

        // Act: the colour blocks, body text, and each ANSI colour as text at 3:1 (dark: black, light: white and
        // bright white are background tones and exempt, as is conventional). On a dark background bright black can't
        // be 4.5:1 as text and still carry white text at 4.5:1 as a block; the dark originals keep the block, and the
        // contrast default lifts bright black text (StatuslineBlocksAreReadable).
        var blocks = Failures(theme, StatuslinePairs(s).Where(x => x.What != "bright black on the background" || !dark));
        var weakText = Enumerable.Range(0, 16)
            .Where(i => !(dark ? i is 0 or 8 : i is 7 or 15) && Contrast.Ratio(p[i], s.Background) < 3f)
            .Select(i => $"colour {i}: {Contrast.Ratio(p[i], s.Background):0.00}").ToList();

        // Assert
        await Assert.That(blocks).IsEmpty();
        await Assert.That(weakText).IsEmpty();
    }

    [Test]
    public async Task TheSchemesDefaultAppliesWhenTheConfigSetsNone()
    {
        // Act
        var builtIn = Theme.From(new Settings(), ColorSchemes.Erik, 1);
        var file = Theme.From(new Settings(), ColorSchemes.Erik with { MinimumContrast = 1, InactiveMinimumContrast = 1, SourcePath = "/x/colors/mine.toml" }, 1);

        // Assert
        await Assert.That(builtIn.MinimumContrast).IsEqualTo((float)ColorSchemes.BuiltInMinimumContrast);
        await Assert.That(builtIn.InactiveMinimumContrast).IsEqualTo((float)ColorSchemes.BuiltInInactiveMinimumContrast);
        await Assert.That(file.MinimumContrast).IsEqualTo(1f);
        await Assert.That(file.InactiveMinimumContrast).IsEqualTo(1f);
    }

    [Test]
    [Arguments(1.0)]
    [Arguments(3.0)]
    [Arguments(7.0)]
    public async Task TheConfigsMinimumContrastWins(double configured)
    {
        // Act
        var t = Theme.From(new Settings { MinimumContrast = configured }, ColorSchemes.Erik, 1);

        // Assert
        await Assert.That(t.MinimumContrast).IsEqualTo((float)configured);
        await Assert.That(t.InactiveMinimumContrast).IsEqualTo(1f); // the user's own setting isn't changed in inactive panes
    }

    /// <summary>
    /// Everything the tab bar draws on a tab, with the colours and see-through blends of Ui/TabBar.cs (and the badges it
    /// draws: RepoBadge, AgentBadge, ContainerIcon, ProgressBar): text at 4.5:1, marks and icons at 3:1. The tab bar
    /// looks the same in a focused and an unfocused window.
    /// </summary>
    internal static IEnumerable<(string What, uint Fg, uint Bg, float Minimum)> TabBarPairs(Theme theme)
    {
        var k = theme.Tabs;
        var (tab, hover, active, button) = theme.TabBackgrounds();
        uint title = Colors.Mix(k.PanelFg, k.Muted, 0.45f);
        uint Faded(uint rgb, uint under) => Colors.Mix(under, rgb, k.BadgeAlpha);
        const float Text = 4.5f, Mark = 3f;
        yield return ("active tab number", k.Accent, active, Text);
        yield return ("active tab title", k.PanelFg, active, Text);
        yield return ("inactive tab number", k.Muted, tab, Text);
        yield return ("inactive tab title", title, tab, Text);
        yield return ("hovered tab number", k.Muted, hover, Text);
        yield return ("hovered tab title", Colors.Mix(title, k.PanelFg, 0.6f), hover, Text);
        yield return ("\"N more\" label", k.Muted, button, Text);
        yield return ("branch name on an inactive tab", Faded(Colors.Mix(k.PanelFg, k.Muted, 0.35f), tab), tab, Text);
        yield return ("ahead/behind on an inactive tab", Faded(k.Accent, tab), tab, Text);
        yield return ("pull request on an inactive tab", Faded(Colors.Mix(k.PanelFg, k.Accent, 0.35f), tab), tab, Text);
        yield return ("ahead/behind on the active tab", k.Accent, active, Text);
        yield return ("+ button", k.Muted, button, Mark);
        yield return ("close ×", k.Muted, hover, Mark);
        yield return ("activity dot", k.Accent, tab, Mark);
        yield return ("progress on the active tab", k.Accent, active, Mark);
        yield return ("active tab outline", Colors.Mix(theme.Background, k.TabOutline, Theme.OutlineAlpha), theme.Background, Mark);
        yield return ("branch icon", Faded(k.Muted, tab), tab, Mark);
        yield return ("container icon", Colors.Mix(tab, k.Info, 0.8f), tab, Mark);
        foreach (var (name, rgb) in new[] { ("waiting", k.Danger), ("working", k.Warning), ("done", k.Info), ("idle", k.Muted) })
        {
            yield return ($"agent {name} dot", rgb, tab, Mark);
            yield return ($"agent {name} dot on the active tab", rgb, active, Mark);
        }
        yield return ("checks passed", Faded(k.Success, tab), tab, Mark);
        yield return ("checks failed", Faded(k.Danger, tab), tab, Mark);
        yield return ("checks pending", Faded(k.Warning, tab), tab, Mark);
    }

    [Test]
    [MethodDataSource(nameof(Schemes))]
    public async Task TabBarIsReadable(string name)
    {
        // Arrange
        var theme = ThemeOf(name);

        // Act
        var failures = TabBarPairs(theme).Where(p => Contrast.Ratio(p.Fg, p.Bg) < p.Minimum)
            .Select(p => $"{p.What}: {Contrast.Ratio(p.Fg, p.Bg):0.00}").ToList();

        // Assert
        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task HighContrastStillRaisesTheDefaultToSeven()
    {
        // Act
        var (effective, _) = AccessibilityPolicy.Apply(new Settings { HighContrast = AccessibilityMode.On }, SystemAccessibility.Unknown);

        // Assert
        await Assert.That(effective.MinimumContrastFor(ColorSchemes.Erik)).IsEqualTo(AccessibilityPolicy.HighContrastMinimum);
    }
}
