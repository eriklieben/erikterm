using ErikTerm.App.Ui;
using ErikTerm.Config;

namespace ErikTerm.Tests;

public class AboutTests
{
    private static Dictionary<string, string?> Meta(string? commit = null, string? dirty = null, string? date = null)
    {
        var m = new Dictionary<string, string?>();
        if (commit is not null) m[AboutFormat.MetaCommit] = commit;
        if (dirty is not null) m[AboutFormat.MetaDirty] = dirty;
        if (date is not null) m[AboutFormat.MetaBuildDate] = date;
        return m;
    }

    [Test]
    public async Task ParseBuildPrefersStampedCommitAndStripsSourceRevision()
    {
        // Arrange
        var meta = Meta("354fb54", "true", "2026-11-07 14:03 UTC");

        // Act
        var b = AboutFormat.ParseBuild("0.1.0+354fb54c66822ce198268c864b6dc0a69def102a", meta);

        // Assert
        await Assert.That(b.Version).IsEqualTo("0.1.0");
        await Assert.That(b.Commit).IsEqualTo("354fb54");
        await Assert.That(b.Dirty).IsTrue();
        await Assert.That(b.BuildDate).IsEqualTo("2026-11-07 14:03 UTC");
    }

    [Test]
    public async Task ParseBuildFallsBackToSourceRevision()
    {
        // Arrange
        var meta = Meta(commit: "", dirty: "false");

        // Act
        var b = AboutFormat.ParseBuild("0.2.0+abcdef0123456789", meta);

        // Assert
        await Assert.That(b).IsEqualTo(("0.2.0", "abcdef0", false, (string?)null));
    }

    [Test]
    public async Task ParseBuildOutsideAGitCheckoutHasNoCommitAndIsNotDirty()
    {
        // Arrange
        // Outside a git checkout: no commit, and "dirty" is meaningless without one.
        var meta = Meta(dirty: "true");

        // Act
        var none = AboutFormat.ParseBuild("0.2.0", meta);

        // Assert
        await Assert.That(none).IsEqualTo(("0.2.0", (string?)null, false, (string?)null));
    }

    [Test]
    public async Task ParseBuildWithoutAVersionIsZero()
    {
        // Act
        var b = AboutFormat.ParseBuild(null, Meta());

        // Assert
        await Assert.That(b.Version).IsEqualTo("0.0.0");
    }

    [Test]
    public async Task ParseBuildIgnoresASourceRevisionThatIsNotAHash()
    {
        // Act
        var b = AboutFormat.ParseBuild("1.0.0+not-a-hash", Meta());

        // Assert
        await Assert.That(b.Commit).IsNull();
    }

    [Test]
    [Arguments("0.1.0", "354fb54", false, "0.1.0 (354fb54)")]
    [Arguments("0.1.0", "354fb54", true, "0.1.0 (354fb54, modified)")]
    [Arguments("0.1.0", null, false, "0.1.0")]
    public async Task VersionLine(string version, string? commit, bool dirty, string expected)
    {
        // Act
        string line = AboutFormat.VersionLine(version, commit, dirty);

        // Assert
        await Assert.That(line).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, true, "unknown")]
    [Arguments("354fb54", true, "354fb54 (modified)")]
    public async Task CommitText(string? commit, bool dirty, string expected)
    {
        // Act
        string text = AboutFormat.CommitText(commit, dirty);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments(3004018, "3.4.18")]
    [Arguments(3002000, "3.2.0")]
    [Arguments(0, "unknown")]
    public async Task SdlVersion(int packed, string expected)
    {
        // Act
        string version = AboutFormat.SdlVersion(packed);

        // Assert
        await Assert.That(version).IsEqualTo(expected);
    }

    [Test]
    [Arguments(512L, "512 B")]
    [Arguments(1536L, "1.5 KB")]
    [Arguments(104_857_600L, "100 MB")]
    [Arguments(88_290_099L, "84.2 MB")]
    public async Task Bytes(long bytes, string expected)
    {
        // Act
        string text = AboutFormat.Bytes(bytes);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    public static IEnumerable<(TimeSpan Elapsed, string Expected)> Durations() =>
    [
        (TimeSpan.FromSeconds(12.7), "12s"),
        (new TimeSpan(0, 3, 5), "3m 05s"),
        (new TimeSpan(2, 7, 30), "2h 07m"),
        (new TimeSpan(1, 3, 0, 0), "1d 3h"),
        (TimeSpan.FromSeconds(-4), "0s"),
    ];

    [Test]
    [MethodDataSource(nameof(Durations))]
    public async Task Duration(TimeSpan elapsed, string expected)
    {
        // Act
        string text = AboutFormat.Duration(elapsed);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments(119.98f, "120 Hz")]
    [Arguments(59.94f, "59.94 Hz")]
    [Arguments(0f, "")]
    public async Task Refresh(float hz, string expected)
    {
        // Act
        string text = AboutFormat.Refresh(hz);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/home/erik/.config/erikterm/erikterm.toml", "/home/erik", "~/.config/erikterm/erikterm.toml")]
    [Arguments("/home/erikb/x.toml", "/home/erik", "/home/erikb/x.toml")]
    [Arguments("/etc/x.toml", null, "/etc/x.toml")]
    public async Task HomeRelative(string path, string? home, string expected)
    {
        // Act
        string text = AboutFormat.HomeRelative(path, home);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments(2, 14, 1, 2026, 2025)]
    [Arguments(2, 13, 3, 2026, 2024)]
    [Arguments(2, 13, 2, 2026, 2023)]
    [Arguments(2, 15, 0, 2026, 2026)]
    [Arguments(3, 0, 0, 2028, 2028)] // unknown future version: the build year
    public async Task FreeTypeCreditUsesTheReleaseYear(int major, int minor, int patch, int buildYear, int expected)
    {
        // Act
        int year = AboutFormat.FreeTypeYear(major, minor, patch, buildYear);

        // Assert
        await Assert.That(year).IsEqualTo(expected);
    }

    [Test]
    public async Task FreeTypeCreditNamesTheYear()
    {
        // Act
        string credit = AboutFormat.FreeTypeCredit(2025);

        // Assert
        await Assert.That(credit).IsEqualTo(
            "Portions of this software are copyright © 2025 The FreeType Project (www.freetype.org). All rights reserved.");
    }

    private static AboutInfo Fake() => new()
    {
        Version = "0.1.0",
        Commit = "354fb54",
        Dirty = true,
        BuildDate = "2026-11-07 14:03 UTC",
        NativeAot = true,
        Runtime = ".NET 10.0.0",
        Os = "Arch Linux · Linux 7.2.11",
        Arch = "x64",
        WorkingSetBytes = 88_290_099,
        Uptime = new TimeSpan(0, 3, 5),
        ProcessId = 4242,
        SdlVersion = "3.4.18",
        VideoDriver = "wayland",
        Renderer = "opengl",
        VSync = true,
        DisplayWidth = 2560,
        DisplayHeight = 1440,
        RefreshHz = 165,
        PixelDensity = 1.5f,
        DisplayScale = 1.5f,
        WindowOpacity = 0.75f,
        FreeTypeVersion = "2.15.0",
        HarfBuzzVersion = null,
        FontPath = "/usr/share/fonts/TTF/JetBrainsMonoNerdFont-Regular.ttf",
        FontPoints = 11,
        FontPixels = 22,
        CellWidth = 13,
        CellHeight = 38,
        LineHeight = 1.25,
        FallbackFonts = 14,
        AtlasEntries = 312,
        AtlasFill = 0.18f,
        UnicodeVersion = "17.0.0",
        Tabs = 2,
        Panes = 3,
        ColorScheme = "Catppuccin Macchiato",
        ConfigPath = "/home/erik/.config/erikterm/erikterm.toml",
        ConfigExists = true,
        Home = "/home/erik",
        Libraries = ["/usr/lib/libSDL3.so.0.4.18", "/usr/lib/libfreetype.so.6.20.4", "/usr/lib/libSDL3.so.0.4.18"],
    };

    [Test]
    public async Task SectionsFormatTheFacts()
    {
        // Arrange
        var info = Fake();

        // Act
        var sections = AboutFormat.Sections(info);
        AboutRow Row(string label) => sections.SelectMany(s => s.Rows).Single(r => r.Label == label);

        // Assert
        await Assert.That(sections.Select(s => s.Title)).IsEquivalentTo(["Build", "Display", "System", "Text", "Session"], CollectionOrdering.Matching);
        await Assert.That(Row("Commit")).IsEqualTo(new AboutRow("Commit", "354fb54 (modified)", AboutValueKind.Warning));
        await Assert.That(Row("Compiled").Value).IsEqualTo("Native AOT");
        await Assert.That(Row("Renderer").Value).IsEqualTo("opengl · vsync");
        await Assert.That(Row("Display").Value).IsEqualTo("2560 × 1440 @ 165 Hz");
        await Assert.That(Row("Density").Value).IsEqualTo("1.5×");
        await Assert.That(Row("Opacity").Value).IsEqualTo("75%");
        await Assert.That(Row("HarfBuzz").Kind).IsEqualTo(AboutValueKind.Warning);
        await Assert.That(Row("Font").Value).IsEqualTo("JetBrainsMonoNerdFont-Regular.ttf");
        await Assert.That(Row("Size").Value).IsEqualTo("11 pt · 22 px");
        await Assert.That(Row("Cell").Value).IsEqualTo("13 × 38 px · line height 1.25");
        await Assert.That(Row("Atlas").Value).IsEqualTo("312 entries · 18% full");
        await Assert.That(Row("Config")).IsEqualTo(new AboutRow("Config", "~/.config/erikterm/erikterm.toml", AboutValueKind.Link));
    }

    [Test]
    public async Task SessionNamesTheLastCrashLog()
    {
        // Arrange
        var info = Fake() with
        {
            LastCrashLog = "/home/erik/.local/state/erikterm/crash-20261107-101500-000.log",
            LastCrashTime = new DateTime(2026, 11, 7, 10, 15, 0),
            Now = new DateTime(2026, 11, 7, 12, 30, 0),
        };

        // Act
        var row = AboutFormat.Sections(info).SelectMany(s => s.Rows).Single(r => r.Label == "Crash log");

        // Assert
        await Assert.That(row).IsEqualTo(new AboutRow("Crash log", "~/.local/state/erikterm/crash-20261107-101500-000.log · 2h 15m ago", AboutValueKind.Warning));
    }

    [Test]
    public async Task SessionCountsConfigProblems()
    {
        // Arrange
        var info = Fake() with { ConfigProblems = 3 };

        // Act
        var row = AboutFormat.Sections(info).SelectMany(s => s.Rows).Single(r => r.Label == "Problems");

        // Assert
        await Assert.That(row.Value).IsEqualTo("3 config problems · palette: Show Config Problems");
    }

    [Test]
    public async Task NoProblemRowsWithoutProblems()
    {
        // Act
        var labels = AboutFormat.Sections(Fake()).SelectMany(s => s.Rows).Select(r => r.Label).ToList();

        // Assert
        await Assert.That(labels).DoesNotContain("Problems");
        await Assert.That(labels).DoesNotContain("Crash log");
    }

    [Test]
    public async Task DiagnosticsIsAPlainTextReport()
    {
        // Arrange
        var info = Fake();

        // Act
        string text = AboutFormat.Diagnostics(info);
        string[] lines = text.Split('\n');

        // Assert
        await Assert.That(lines[0]).IsEqualTo("erikterm 0.1.0 (354fb54, modified)");
        await Assert.That(lines).Contains("Build");
        await Assert.That(lines).Contains("  Commit     354fb54 (modified)");
        await Assert.That(lines).Contains("  HarfBuzz   not available (no ligatures)");
        await Assert.That(lines).Contains("  Memory     84.2 MB working set");
        // The full config path (not "~/…"), the font file and every loaded library.
        await Assert.That(lines).Contains("  Config     /home/erik/.config/erikterm/erikterm.toml");
        await Assert.That(lines).Contains("  /usr/share/fonts/TTF/JetBrainsMonoNerdFont-Regular.ttf");
        await Assert.That(lines).Contains("  /usr/lib/libfreetype.so.6.20.4");
        await Assert.That(lines).DoesNotContain(l => l.Contains('\r') || l.Contains('\t'));
        await Assert.That(text).EndsWith("\n");
    }

    [Test]
    public async Task LicensesCreditFreeTypeAndListLibraries()
    {
        // Arrange
        var info = Fake() with { FreeTypeYear = 2026 };
        string expectedCredit = AboutFormat.FreeTypeCredit(2026);

        // Act
        var entries = AboutLicenses.Build(info);

        // Assert
        var ft = await Assert.That(entries).HasSingleItem(e => e.Name == "FreeType");
        await Assert.That(ft.Credit).IsEqualTo(expectedCredit);
        await Assert.That(entries).Contains(e => e.Name == "SDL" && e.License == "zlib License" && e.Version == "3.4.18");
        await Assert.That(entries).Contains(e => e.Name == "HarfBuzz" && e.License == "Old MIT License");
        await Assert.That(entries).Contains(e => e.License == "Unicode License v3" && e.Version == "17.0.0");
    }

    [Test]
    public async Task LibrarySummaryNamesEachLoadedLibraryOnce()
    {
        // Arrange
        var info = Fake() with { FreeTypeYear = 2026 };

        // Act
        string summary = AboutLicenses.LibrarySummary(info.Libraries);

        // Assert
        await Assert.That(summary).IsEqualTo("Loaded now (2): libSDL3.so.0.4.18 · libfreetype.so.6.20.4");
    }

    [Test]
    public async Task ShowAboutIsAKnownAction()
    {
        // Act
        bool parsed = ActionSpec.TryParse("ShowAbout", out var a, out _);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(a.Name).IsEqualTo("ShowAbout");
    }

    [Test]
    public async Task ShowAboutTakesNoArguments()
    {
        // Act
        bool parsed = ActionSpec.TryParse("ShowAbout(x)", out _, out _);

        // Assert
        await Assert.That(parsed).IsFalse();
    }
}
