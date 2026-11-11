using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Replies to queries: XTGETTCAP, DECRQSS, OSC 4/10/11/12, DECRQM, DECXCPR, DA3, 2031/996, 2048, title stack.</summary>
public class QueryReplyTests
{
    private static (Terminal Term, List<string> Replies) Setup(int cols = 20, int rows = 5)
    {
        var t = new Terminal(cols, rows);
        var replies = new List<string>();
        t.Response = data => replies.Add(Encoding.ASCII.GetString(data));
        return (t, replies);
    }

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    private static string Hex(string s) => Convert.ToHexString(Encoding.ASCII.GetBytes(s));

    // ---- XTGETTCAP ----------------------------------------------------------------------------

    [Test]
    public async Task XtGetTcapAnswersAStringCapability()
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, $"\eP+q{Hex("Smulx")}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo([$"\eP1+r{Hex("Smulx")}={Hex("\e[4:%p1%dm")}\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task XtGetTcapAnswersABooleanWithoutAValue()
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, $"\eP+q{Hex("Tc")}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo([$"\eP1+r{Hex("Tc")}\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("colors", "256")]
    [Arguments("Co", "256")]
    [Arguments("RGB", "8/8/8")]
    [Arguments("Ms", "\e]52;%p1%s;%p2%s\a")]
    [Arguments("kbs", "\x7f")]
    public async Task XtGetTcapAnswersValues(string name, string value)
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, $"\eP+q{Hex(name)}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo([$"\eP1+r{Hex(name)}={Hex(value)}\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task XtGetTcapNameIsTheTermTheProgramGot()
    {
        // Arrange
        var (t, replies) = Setup();
        t.TerminalName = "xterm-erikterm";

        // Act
        Feed(t, $"\eP+q{Hex("TN")}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo([$"\eP1+r{Hex("TN")}={Hex("xterm-erikterm")}\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task XtGetTcapAnswersEachNameOfAList()
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, $"\eP+q{Hex("Tc")};{Hex("nope")};{Hex("Su")}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo(
            [$"\eP1+r{Hex("Tc")}\e\\", $"\eP0+r{Hex("nope")}\e\\", $"\eP1+r{Hex("Su")}\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("zz")]   // not hex
    [Arguments("616")]  // odd length
    public async Task XtGetTcapRejectsMalformedNames(string hex)
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, $"\eP+q{hex}\e\\");

        // Assert
        await Assert.That(replies.Count).IsEqualTo(1);
        await Assert.That(replies[0]).StartsWith("\eP0+r");
    }

    [Test]
    public async Task XtGetTcapDoesNotAnswerCancelledCapabilities()
    {
        // Arrange: smm is cancelled in the entry (erikterm has no meta mode)
        var (t, replies) = Setup();

        // Act
        Feed(t, $"\eP+q{Hex("smm")}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo([$"\eP0+r{Hex("smm")}\e\\"], CollectionOrdering.Matching);
    }

    // ---- DECRQSS ------------------------------------------------------------------------------

    [Test]
    public async Task DecrqssReportsTheSgrInTheFormNeovimChecks()
    {
        // Arrange
        var (t, replies) = Setup();

        // Act: Neovim's truecolor probe
        Feed(t, "\e[0m\e[48;2;1;2;3m\eP$qm\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\eP1$r0;48:2::1:2:3m\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecrqssSgrIncludesStylesAndUnderlineColor()
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, "\e[1;4:3;31;58;5;9;53m\eP$qm\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\eP1$r0;1;4:3;53;31;58:5:9m\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("\e[2;4r", "r", "2;4r")]
    [Arguments("\e[5 q", " q", "5 q")]
    [Arguments("\e[4 q", " q", "4 q")]
    [Arguments("", "\"p", "62;1\"p")]
    [Arguments("", "t", "5t")]
    public async Task DecrqssReportsSettings(string setup, string setting, string expected)
    {
        // Arrange
        var (t, replies) = Setup();
        Feed(t, setup);

        // Act
        Feed(t, $"\eP$q{setting}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo([$"\eP1$r{expected}\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("s")]   // DECSLRM: no left/right margins
    [Arguments("x")]
    public async Task DecrqssRejectsUnknownSettings(string setting)
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, $"\eP$q{setting}\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\eP0$r\e\\"], CollectionOrdering.Matching);
    }

    // ---- OSC colors -------------------------------------------------------------------------------

    [Test]
    public async Task Osc4QueryAnswersThePaletteEntry()
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette[1] = 0xAB1234;

        // Act
        Feed(t, "\e]4;1;?\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e]4;1;rgb:abab/1212/3434\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OscRepliesEndWithBelWhenTheQueryDid()
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette.Background = 0x102030;

        // Act
        Feed(t, "\e]11;?\a\e]11;?\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo(
            ["\e]11;rgb:1010/2020/3030\a", "\e]11;rgb:1010/2020/3030\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("rgb:ff/80/00", 0xFF8000u)]
    [Arguments("rgb:f/8/0", 0xFF8800u)]
    [Arguments("rgb:ffff/8080/0000", 0xFF8000u)]
    [Arguments("#ff8000", 0xFF8000u)]
    [Arguments("#f80", 0xF08000u)]
    [Arguments("#fff888000", 0xFF8800u)]
    public async Task Osc4SetsAPaletteEntry(string spec, uint expected)
    {
        // Arrange
        var (t, _) = Setup();

        // Act
        Feed(t, $"\e]4;5;{spec}\e\\");

        // Assert
        await Assert.That(t.Palette[5]).IsEqualTo(expected);
    }

    [Test]
    public async Task Osc4SetsSeveralEntries()
    {
        // Arrange
        var (t, _) = Setup();

        // Act
        Feed(t, "\e]4;1;#010101;2;#020202\a");

        // Assert
        await Assert.That((t.Palette[1], t.Palette[2])).IsEqualTo((0x010101u, 0x020202u));
    }

    [Test]
    public async Task Osc4IgnoresAnInvalidColor()
    {
        // Arrange
        var (t, _) = Setup();
        uint before = t.Palette[3];

        // Act
        Feed(t, "\e]4;3;notacolor\a");

        // Assert
        await Assert.That(t.Palette[3]).IsEqualTo(before);
    }

    [Test]
    public async Task Osc104ResetsOneEntryToTheScheme()
    {
        // Arrange
        var (t, _) = Setup();
        t.Palette[1] = 0x111111;
        t.Palette[2] = 0x222222;
        Feed(t, "\e]4;1;#ff0000;2;#00ff00\a");

        // Act
        Feed(t, "\e]104;1\a");

        // Assert
        await Assert.That((t.Palette[1], t.Palette[2])).IsEqualTo((0x111111u, 0x00FF00u));
    }

    [Test]
    public async Task Osc104WithoutIndexesResetsTheWholePalette()
    {
        // Arrange
        var (t, _) = Setup();
        t.Palette[7] = 0x777777;
        Feed(t, "\e]4;7;#ff0000;200;#00ff00\a");

        // Act
        Feed(t, "\e]104\a");

        // Assert
        await Assert.That(t.Palette[7]).IsEqualTo(0x777777u);
        await Assert.That(t.Palette.HasOverrides).IsFalse();
    }

    [Test]
    public async Task ASchemeChangeKeepsAProgramsPaletteColor()
    {
        // Arrange
        var (t, _) = Setup();
        Feed(t, "\e]4;1;#abcdef\a");

        // Act: the user switches color scheme
        t.Palette[1] = 0x123456;

        // Assert
        await Assert.That(t.Palette[1]).IsEqualTo(0xABCDEFu);
    }

    [Test]
    public async Task ResettingAfterASchemeChangeShowsTheNewSchemesColor()
    {
        // Arrange
        var (t, _) = Setup();
        Feed(t, "\e]4;1;#abcdef\a");
        t.Palette[1] = 0x123456;

        // Act
        Feed(t, "\e]104;1\a");

        // Assert
        await Assert.That(t.Palette[1]).IsEqualTo(0x123456u);
    }

    [Test]
    [Arguments(10, DynamicColor.Foreground)]
    [Arguments(11, DynamicColor.Background)]
    [Arguments(12, DynamicColor.Cursor)]
    public async Task OscSetsDynamicColors(int code, DynamicColor which)
    {
        // Arrange
        var (t, _) = Setup();

        // Act
        Feed(t, $"\e]{code};#102030\a");

        // Assert
        await Assert.That(t.Palette.GetDynamic(which)).IsEqualTo(0x102030u);
    }

    [Test]
    [Arguments(110, 10, DynamicColor.Foreground)]
    [Arguments(111, 11, DynamicColor.Background)]
    [Arguments(112, 12, DynamicColor.Cursor)]
    public async Task OscResetsDynamicColors(int reset, int code, DynamicColor which)
    {
        // Arrange
        var (t, _) = Setup();
        t.Palette.Foreground = 0xAAAAAA;
        t.Palette.Background = 0xBBBBBB;
        t.Palette.Cursor = 0xCCCCCC;
        uint scheme = t.Palette.GetDynamic(which);
        Feed(t, $"\e]{code};#102030\a");

        // Act
        Feed(t, $"\e]{reset}\a");

        // Assert
        await Assert.That(t.Palette.GetDynamic(which)).IsEqualTo(scheme);
    }

    [Test]
    public async Task Osc10WithSeveralColorsSetsTheFollowingCodes()
    {
        // Arrange
        var (t, _) = Setup();

        // Act: xterm's chaining: OSC 10 ; fg ; bg
        Feed(t, "\e]10;#010203;#040506\a");

        // Assert
        await Assert.That((t.Palette.Foreground, t.Palette.Background)).IsEqualTo((0x010203u, 0x040506u));
    }

    [Test]
    public async Task Osc12QueryAnswersTheCursorColor()
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette.Cursor = 0xF5C26B;

        // Act
        Feed(t, "\e]12;?\e\\");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e]12;rgb:f5f5/c2c2/6b6b\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ASchemeChangeKeepsAProgramsBackground()
    {
        // Arrange
        var (t, _) = Setup();
        Feed(t, "\e]11;#202020\a");

        // Act
        t.Palette.Background = 0xFFFFFF;

        // Assert
        await Assert.That(t.Palette.Background).IsEqualTo(0x202020u);
        await Assert.That(t.Palette.HasBackgroundOverride).IsTrue();
    }

    [Test]
    public async Task FullResetDropsProgramColors()
    {
        // Arrange
        var (t, _) = Setup();
        t.Palette.Background = 0x111111;
        Feed(t, "\e]11;#202020\a\e]4;1;#ff0000\a");

        // Act
        Feed(t, "\ec");

        // Assert
        await Assert.That(t.Palette.HasOverrides).IsFalse();
        await Assert.That(t.Palette.Background).IsEqualTo(0x111111u);
    }

    // ---- Light / dark (2031, 996) -------------------------------------------------------------------

    [Test]
    [Arguments(0x101010u, "\e[?997;1n")]
    [Arguments(0xF0F0F0u, "\e[?997;2n")]
    public async Task ColorSchemeQueryReportsDarkOrLight(uint background, string expected)
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette.Background = background;

        // Act
        Feed(t, "\e[?996n");

        // Assert
        await Assert.That(replies).IsEquivalentTo([expected], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Mode2031ReportsWhenTheSchemeFlips()
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette.Background = 0x101010;
        t.ColorsChanged();
        Feed(t, "\e[?2031h");

        // Act: the user picks a light scheme
        t.Palette.Background = 0xFAFAFA;
        t.ColorsChanged();

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[?997;2n"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Mode2031StaysQuietWhenTheBackgroundStaysDark()
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette.Background = 0x101010;
        Feed(t, "\e[?2031h");

        // Act
        t.Palette.Background = 0x202020;
        t.ColorsChanged();

        // Assert
        await Assert.That(replies).IsEmpty();
    }

    [Test]
    public async Task Mode2031ReportsAnOsc11Flip()
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette.Background = 0x101010;
        Feed(t, "\e[?2031h");

        // Act
        Feed(t, "\e]11;#ffffff\a");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[?997;2n"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithoutMode2031AFlipIsNotReported()
    {
        // Arrange
        var (t, replies) = Setup();
        t.Palette.Background = 0x101010;
        t.ColorsChanged();

        // Act
        t.Palette.Background = 0xFAFAFA;
        t.ColorsChanged();

        // Assert
        await Assert.That(replies).IsEmpty();
    }

    // ---- Modes and small replies ----------------------------------------------------------------------

    [Test]
    [Arguments("\e[?2027$p", "\e[?2027;3$y")]
    [Arguments("\e[?2031$p", "\e[?2031;2$y")]
    [Arguments("\e[?2031h\e[?2031$p", "\e[?2031;1$y")]
    [Arguments("\e[?2048$p", "\e[?2048;2$y")]
    [Arguments("\e[4$p", "\e[4;2$y")]
    [Arguments("\e[4h\e[4$p", "\e[4;1$y")]
    [Arguments("\e[20$p", "\e[20;2$y")]
    [Arguments("\e[99$p", "\e[99;0$y")]
    public async Task ModeQueriesReport(string input, string expected)
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, input);

        // Assert
        await Assert.That(replies).IsEquivalentTo([expected], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ExtendedCursorPositionIncludesThePage()
    {
        // Arrange
        var (t, replies) = Setup();
        Feed(t, "\e[3;7H");

        // Act
        Feed(t, "\e[?6n");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[?3;7;1R"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TertiaryDeviceAttributesReportAUnitId()
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        Feed(t, "\e[=c");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\eP!|00000000\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task InBandResizeReportsOnEnableAndOnResize()
    {
        // Arrange
        var (t, replies) = Setup(cols: 20, rows: 5);
        (t.CellPixelWidth, t.CellPixelHeight) = (10, 20);

        // Act
        Feed(t, "\e[?2048h");
        t.Resize(30, 6);

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[48;5;20;100;200t", "\e[48;6;30;120;300t"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResizeIsNotReportedWithoutMode2048()
    {
        // Arrange
        var (t, replies) = Setup();

        // Act
        t.Resize(30, 6);

        // Assert
        await Assert.That(replies).IsEmpty();
    }

    [Test]
    public async Task TitleStackRestoresThePushedTitle()
    {
        // Arrange
        var (t, _) = Setup();
        Feed(t, "\e]2;shell\a");

        // Act: Neovim pushes, sets its own, pops on exit
        Feed(t, "\e[22;0t\e]2;nvim\a\e[23;0t");

        // Assert
        await Assert.That(t.Title).IsEqualTo("shell");
    }

    [Test]
    public async Task TitleStackPopOnAnEmptyStackKeepsTheTitle()
    {
        // Arrange
        var (t, _) = Setup();
        Feed(t, "\e]2;mine\a");

        // Act
        Feed(t, "\e[23;0t");

        // Assert
        await Assert.That(t.Title).IsEqualTo("mine");
    }

    [Test]
    public async Task ReportTitleStaysUnanswered()
    {
        // Arrange: CSI 21 t would echo program-controlled text back as input
        var (t, replies) = Setup();
        Feed(t, "\e]2;$(rm -rf ~)\a");

        // Act
        Feed(t, "\e[21t");

        // Assert
        await Assert.That(replies).IsEmpty();
    }

    [Test]
    public async Task BlinkModeTwelveDoesNotOverrideTheConfiguredBlink()
    {
        // Arrange: xterm-256color's cnorm sends ?12l whenever a program shows the cursor
        var (t, _) = Setup();
        t.DefaultCursorBlink = true;

        // Act
        Feed(t, "\e[?12l\e[?25h");

        // Assert
        await Assert.That(t.CursorBlink).IsTrue();
    }

    // ---- The term setting ----------------------------------------------------------------------------

    [Test]
    [Arguments("", "auto")]
    [Arguments("term = \"xterm-256color\"", "xterm-256color")]
    [Arguments("term = \"xterm-erikterm\"", "xterm-erikterm")]
    public async Task TermSettingIsParsed(string toml, string expected)
    {
        // Act
        var settings = ErikTerm.Config.SettingsLoader.Parse(toml).Settings;

        // Assert
        await Assert.That(settings.Term).IsEqualTo(expected);
    }

    [Test]
    public async Task TermSettingRejectsANameWithSpaces()
    {
        // Act
        var r = ErikTerm.Config.SettingsLoader.Parse("term = \"xterm 256\"");

        // Assert
        await Assert.That(r.Settings.Term).IsEqualTo("auto");
        await Assert.That(r.Diagnostics.Count).IsEqualTo(1);
    }
}
