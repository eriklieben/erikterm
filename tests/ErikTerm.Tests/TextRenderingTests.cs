using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Config;

namespace ErikTerm.Tests;

public class BoxDrawingTests
{
    // Cell sizes from tiny to zoomed, with line_height 1.0 and 1.25 shapes.
    public static IEnumerable<(int W, int H, int T)> Cells() =>
    [
        (5, 10, 1), (9, 19, 1), (11, 25, 1), (11, 31, 1), (17, 48, 2), (24, 60, 2), (40, 120, 4),
    ];

    private static byte[] Render(int cp, int w, int h, int t)
    {
        var a = new byte[w * h];
        if (!BoxDrawing.Render(cp, w, h, t, a)) Assert.Fail($"U+{cp:X4} not rendered");
        return a;
    }

    private static int Rows(byte[] a, int w, int h, int x) => Enumerable.Range(0, h).Count(y => a[y * w + x] == 255);
    private static int Cols(byte[] a, int w, int y) => Enumerable.Range(0, w).Count(x => a[y * w + x] == 255);

    /// <summary>The pixels of column <paramref name="x"/>, top to bottom.</summary>
    private static byte[] Column(byte[] a, int w, int h, int x) => Enumerable.Range(0, h).Select(y => a[y * w + x]).ToArray();

    /// <summary>The pixels of row <paramref name="y"/>, left to right.</summary>
    private static byte[] Row(byte[] a, int w, int y) => a.AsSpan(y * w, w).ToArray();

    [Test]
    [Arguments(0x2500, true)]
    [Arguments(0x259F, true)]
    [Arguments(0x25E2, true)]
    [Arguments(0x28FF, true)]
    [Arguments(0xE0B0, true)]
    [Arguments((int)'a', false)]
    [Arguments(0x25A0, false)] // ■ comes from the font
    [Arguments(0xE0A0, false)] // powerline branch icon is a font glyph
    public async Task HandlesTheProceduralRanges(int cp, bool expected)
    {
        // Act
        bool handled = BoxDrawing.Handles(cp);

        // Assert
        await Assert.That(handled).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0x2500, 1, 1, 0, 0)] // ─
    [Arguments(0x2503, 0, 0, 2, 2)] // ┃
    [Arguments(0x250C, 0, 1, 0, 1)] // ┌
    [Arguments(0x2543, 2, 1, 2, 1)] // ╃
    [Arguments(0x254B, 2, 2, 2, 2)] // ╋
    [Arguments(0x256C, 3, 3, 3, 3)] // ╬
    [Arguments(0x2552, 0, 3, 0, 1)] // ╒
    [Arguments(0x257C, 1, 2, 0, 0)] // ╼
    public async Task ArmTable(int cp, int left, int right, int up, int down)
    {
        // Act
        var arms = BoxDrawing.Arms(cp);

        // Assert
        await Assert.That(arms).IsEqualTo((left, right, up, down));
    }

    [Test]
    [Arguments(0x2504)] // ┄ dashed
    [Arguments(0x256D)] // ╭ arc
    [Arguments(0x2580)] // ▀ block
    public async Task ArmTableLeavesOutDashesArcsAndBlocks(int cp)
    {
        // Act
        var arms = BoxDrawing.Arms(cp);

        // Assert
        await Assert.That(arms).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(Cells))]
    public async Task EveryCodepointRendersSomething(int w, int h, int t)
    {
        // Arrange
        static IEnumerable<int> All()
        {
            for (int cp = 0x2500; cp <= 0x259F; cp++) yield return cp;
            for (int cp = 0x25E2; cp <= 0x25E5; cp++) yield return cp;
            for (int cp = 0x2801; cp <= 0x28FF; cp++) yield return cp;
            for (int cp = 0xE0B0; cp <= 0xE0BF; cp++) yield return cp;
        }

        // Act
        var blank = All().Where(cp => !Render(cp, w, h, t).Any(b => b > 0)).Select(cp => $"U+{cp:X4}").ToList();

        // Assert
        await Assert.That(blank).IsEmpty().Because($"these are blank at {w}x{h}");
    }

    [Test]
    [MethodDataSource(nameof(Cells))]
    public async Task BlankBrailleRendersNothing(int w, int h, int t)
    {
        // Act
        var blankBraille = Render(0x2800, w, h, t);

        // Assert
        await Assert.That(blankBraille).All(b => b == 0);
    }

    [Test]
    [MethodDataSource(nameof(Cells))]
    public async Task LinesReachTheCellEdgesSoTheyJoinAcrossCells(int w, int h, int t)
    {
        // Act
        var horiz = Render(0x2500, w, h, t);
        var vert = Render(0x2502, w, h, t);

        // Assert
        await Assert.That(Rows(horiz, w, h, 0)).IsEqualTo(t);
        await Assert.That(Rows(horiz, w, h, w - 1)).IsEqualTo(t);
        await Assert.That(Cols(horiz, w, h / 2)).IsEqualTo(w);
        await Assert.That(Cols(vert, w, 0)).IsEqualTo(t);      // top row: seamless with the row above, whatever line_height
        await Assert.That(Cols(vert, w, h - 1)).IsEqualTo(t);  // bottom row
        await Assert.That(Rows(vert, w, h, w / 2)).IsEqualTo(h);
    }

    [Test]
    [MethodDataSource(nameof(Cells))]
    public async Task JunctionsUseTheSameStrokePositions(int w, int h, int t)
    {
        // Act
        var horiz = Render(0x2500, w, h, t);
        var vert = Render(0x2502, w, h, t);
        var cross = Render(0x253C, w, h, t);   // ┼
        var tee = Render(0x251C, w, h, t);     // ├
        var corner = Render(0x256D, w, h, t);  // ╭

        // Assert
        // Right edge:
        await Assert.That(Column(cross, w, h, w - 1)).IsEquivalentTo(Column(horiz, w, h, w - 1), CollectionOrdering.Matching);
        await Assert.That(Column(tee, w, h, w - 1)).IsEquivalentTo(Column(horiz, w, h, w - 1), CollectionOrdering.Matching);
        await Assert.That(Column(corner, w, h, w - 1)).IsEquivalentTo(Column(horiz, w, h, w - 1), CollectionOrdering.Matching); // the arc runs into the same line
        // Top edge, bottom edge:
        await Assert.That(Row(cross, w, 0)).IsEquivalentTo(Row(vert, w, 0), CollectionOrdering.Matching);
        await Assert.That(Row(corner, w, h - 1)).IsEquivalentTo(Row(vert, w, h - 1), CollectionOrdering.Matching);
        await Assert.That(corner[0]).IsEqualTo((byte)0); // the rounded corner leaves the outer corner empty
    }

    [Test]
    [MethodDataSource(nameof(Cells))]
    public async Task HeavyIsTwiceLightAndDoubleIsTwoStrokes(int w, int h, int t)
    {
        // Act
        var heavy = Render(0x2501, w, h, t); // ━
        var dbl = Render(0x2550, w, h, t);   // ═
        int first = Enumerable.Range(0, h).First(y => dbl[y * w] == 255);

        // Assert
        await Assert.That(Rows(heavy, w, h, 0)).IsEqualTo(2 * t);
        await Assert.That(Rows(dbl, w, h, 0)).IsEqualTo(2 * t);
        await Assert.That(dbl[(first + t) * w]).IsEqualTo((byte)0); // a gap between the strokes
    }

    [Test]
    [MethodDataSource(nameof(Cells))]
    public async Task BlocksTileTheCell(int w, int h, int t)
    {
        // Act
        var full = Render(0x2588, w, h, t);  // █
        var upper = Render(0x2580, w, h, t);
        var lower = Render(0x2584, w, h, t);
        var left = Render(0x258C, w, h, t);
        var right = Render(0x2590, w, h, t);
        var light = Render(0x2591, w, h, t);  // ░
        var medium = Render(0x2592, w, h, t); // ▒
        var dark = Render(0x2593, w, h, t);   // ▓

        // Assert
        await Assert.That(full).All(b => b == 255);
        await Assert.That(upper.Zip(lower, (u, l) => u + l)).All(sum => sum == 255); // ▀ + ▄ = █, no overlap and no gap
        await Assert.That(left.Zip(right, (l, r) => l + r)).All(sum => sum == 255);  // ▌ + ▐
        await Assert.That(light).All(b => b == 64);
        await Assert.That(medium).All(b => b == 128);
        await Assert.That(dark).All(b => b == 191);
    }

    [Test]
    [MethodDataSource(nameof(Cells))]
    public async Task PowerlineTrianglesSpanTheFullCellHeight(int w, int h, int t)
    {
        // Arrange
        int slopeRows = (int)Math.Ceiling(h / (2.0 * w)); // rows the diagonal crosses within the first column

        // Act
        var right = Render(0xE0B0, w, h, t);
        var left = Render(0xE0B2, w, h, t);

        // Assert
        // Flush with the cell on the flat side, top to bottom (the corner pixels are anti-aliased).
        await Assert.That(Column(right, w, h, 0)).All(b => b > 0);
        await Assert.That(Rows(right, w, h, 0)).IsGreaterThanOrEqualTo(h - 2 * slopeRows);
        await Assert.That(right[w - 1]).IsEqualTo((byte)0);           // tip in the middle: top-right corner empty
        await Assert.That(right[(h / 2) * w + w - 1]).IsGreaterThan((byte)0);
        await Assert.That(Column(left, w, h, w - 1)).All(b => b > 0);
        await Assert.That(left[0]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task BrailleDotsFollowTheUnicodeBitOrder()
    {
        // Arrange
        const int w = 16, h = 32;

        // Act
        // Dot 7 (bit 6) is the bottom-left dot; dot 4 (bit 3) the top-right.
        var dot7 = Render(0x2800 + 0x40, w, h, 1);
        var dot4 = Render(0x2800 + 0x08, w, h, 1);
        var all = Render(0x28FF, w, h, 1);

        // Assert
        await Assert.That(dot7[(h * 7 / 8) * w + w / 4]).IsGreaterThan((byte)0);
        await Assert.That(dot7[(h / 8) * w + w / 4]).IsEqualTo((byte)0);
        await Assert.That(dot4[(h / 8) * w + w * 3 / 4]).IsGreaterThan((byte)0);
        foreach (int col in new[] { w / 4, w * 3 / 4 })
            for (int row = 1; row < 8; row += 2)
                await Assert.That(all[(h * row / 8) * w + col]).IsGreaterThan((byte)0).Because($"missing dot at column {col}, row {row / 2}");
    }

    [Test]
    public async Task UnhandledCodepointsAreRejected()
    {
        // Act
        bool rendered = BoxDrawing.Render('a', 4, 4, 1, new byte[16]);

        // Assert
        await Assert.That(rendered).IsFalse();
    }
}

public class FontFeatureTests
{
    private static FontFeature F(string tag, uint value) => new(FontFeature.MakeTag(tag), value);

    [Test]
    [Arguments("calt=0", "calt", 0u)]
    [Arguments("calt=1", "calt", 1u)]
    [Arguments("liga", "liga", 1u)]
    [Arguments("+ss01", "ss01", 1u)]
    [Arguments("-dlig", "dlig", 0u)]
    [Arguments(" zero = on ", "zero", 1u)]
    [Arguments("kern=off", "kern", 0u)]
    [Arguments("cv05=3", "cv05", 3u)]
    [Arguments("ss", "ss  ", 1u)] // short tags are space-padded, as in OpenType
    public async Task ParsesHarfBuzzSyntax(string text, string tag, uint value)
    {
        // Act
        bool parsed = FontFeatures.TryParse(text, out var f);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(f).IsEqualTo(F(tag, value));
    }

    [Test]
    [Arguments("")]
    [Arguments("toolong=1")]
    [Arguments("calt=maybe")]
    [Arguments("calt=-1")]
    [Arguments("+calt=0")]
    [Arguments("kern[3:5]")]
    public async Task RejectsInvalidSettings(string text)
    {
        // Act
        bool parsed = FontFeatures.TryParse(text, out _);

        // Assert
        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TagsArePackedBigEndian()
    {
        // Act
        uint tag = FontFeature.MakeTag("calt");

        // Assert
        await Assert.That(tag).IsEqualTo(0x63616C74u);
    }

    [Test]
    public async Task ParseListSkipsAndReportsBadEntries()
    {
        // Arrange
        var errors = new List<string>();

        // Act
        var parsed = FontFeatures.Parse(["calt=0", "nope=x", "liga=0"], errors);

        // Assert
        await Assert.That(parsed).IsEquivalentTo(new[] { F("calt", 0), F("liga", 0) }, CollectionOrdering.Matching);
        var error = await Assert.That(errors).HasSingleItem();
        await Assert.That(error).Contains("nope=x");
    }

    [Test]
    public async Task DefaultsEnableLigatures()
    {
        // Act
        var parsed = FontFeatures.Parse(FontFeatures.Default);

        // Assert
        await Assert.That(parsed).IsEquivalentTo(new[] { F("calt", 1), F("liga", 1), F("clig", 1) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FormatsForDiagnostics()
    {
        // Arrange
        var feature = F("calt", 0);

        // Act
        string text = feature.ToString();

        // Assert
        await Assert.That(text).IsEqualTo("calt=0");
    }

    [Test]
    public async Task SettingDefaultsMatch()
    {
        // Act
        var features = new Settings().HarfbuzzFeatures;

        // Assert
        await Assert.That(features).IsEquivalentTo(FontFeatures.Default, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SettingAcceptsEmptyList()
    {
        // Act
        var r = SettingsLoader.Parse("harfbuzz_features = []", "cfg.toml");

        // Assert
        await Assert.That(r.Settings.HarfbuzzFeatures).IsEmpty();
        await Assert.That(r.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SettingTakesAList()
    {
        // Act
        var r = SettingsLoader.Parse("""harfbuzz_features = ["calt=0", "liga=0"]""", "cfg.toml");

        // Assert
        await Assert.That(r.Settings.HarfbuzzFeatures).IsEquivalentTo(new[] { "calt=0", "liga=0" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, "libharfbuzz.so.0")]
    [Arguments(true, "harfbuzz.dll")]
    public async Task HarfBuzzLibraryNames(bool windows, string expected)
    {
        // Act
        // A pure name table: nothing is loaded or registered.
        string first = NativeLibraries.Candidates(NativeLibraries.HarfBuzz, windows: windows, macOS: false)[0];

        // Assert
        await Assert.That(first).IsEqualTo(expected);
    }
}
