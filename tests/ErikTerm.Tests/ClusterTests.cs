using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

// Every non-ASCII literal below is spelled with \u / \U escapes so no editor or tool can normalize it: "e\u0301"
// (e + combining acute) is a cluster, while its NFC form U+00E9 is a single code point and would test nothing.
public class ClusterTests
{
    private static Terminal Feed(string s, int cols = 10, int rows = 3)
    {
        var t = new Terminal(cols, rows);
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    /// <summary>The grapheme cluster stored at a cell, if any (the span can't live in an async test).</summary>
    private static string? Cluster(Terminal t, int x, int y) =>
        Terminal.TryGetCluster(t.GetScreenRow(y), x, out var text) ? text.ToString() : null;

    private static bool IsWide(Terminal t, int x, int y) =>
        (t.CellAt(x, y).Flags & CellAttributes.WideLead) != 0 && (t.CellAt(x + 1, y).Flags & CellAttributes.WideTrail) != 0;

    [Test]
    [Arguments((int)'a', 1)]
    [Arguments(0x00E9, 1)]   // é precomposed
    [Arguments(0x0301, 0)]   // combining acute
    [Arguments(0x20AC, 1)]   // €
    [Arguments(0x65E5, 2)]   // 日
    [Arguments(0xAC00, 2)]   // Hangul syllable
    [Arguments(0x1160, 0)]   // Hangul medial vowel (conjoining jamo)
    [Arguments(0x3099, 0)]   // combining kana voiced mark (Mn although East Asian Wide)
    [Arguments(0x200B, 0)]   // zero width space
    [Arguments(0x200D, 0)]   // ZWJ
    [Arguments(0xFE0F, 0)]   // VS16
    [Arguments(0x2764, 1)]   // ❤ text presentation by default
    [Arguments(0x231A, 2)]   // ⌚ emoji presentation
    [Arguments(0x1F600, 2)]  // 😀
    [Arguments(0x1F3FD, 2)]  // skin tone on its own
    [Arguments(0x1F1F3, 2)]  // regional indicator
    [Arguments(0x0600, 1)]   // Arabic number sign: prepended mark, keeps a cell
    [Arguments(0x2FFFD, 2)]  // unassigned in the CJK extension plane: wide by default
    [Arguments(0xE0041, 0)]  // tag character
    public async Task WidthTable(int cp, int width)
    {
        // Act
        int actual = CharWidth.Of(cp);

        // Assert
        await Assert.That(actual).IsEqualTo(width);
    }

    [Test]
    public async Task TablesComeFromAUnicodeVersion()
    {
        // Act
        string version = CharWidth.UnicodeVersion;

        // Assert
        await Assert.That(version).Matches(@"^\d+\.\d+\.\d+$");
    }

    [Test]
    public async Task CombiningMarkJoinsPreviousCell()
    {
        // Act
        var t = Feed("e\u0301x");

        // Assert
        await Assert.That(t.CellAt(0, 0).Rune).IsEqualTo('e');
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("e\u0301");
        await Assert.That(t.CellAt(1, 0).Rune).IsEqualTo('x');
        await Assert.That(t.CursorX).IsEqualTo(2);
        await Assert.That(t.GetLineText(0)).IsEqualTo("e\u0301x");
        await Assert.That(Cluster(t, 1, 0)).IsNull();
    }

    [Test]
    public async Task SeveralMarksAccumulate()
    {
        // Act
        // a + acute + dot below + circumflex, in that (non-canonical) order.
        var t = Feed("a\u0301\u0323\u0302");

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("a\u0301\u0323\u0302");
        await Assert.That(t.CursorX).IsEqualTo(1);
    }

    [Test]
    public async Task CombiningMarkAfterWideCharJoinsTheLead()
    {
        // Act
        var t = Feed("\u65E5\u0301b"); // 日 + combining acute

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("\u65E5\u0301");
        await Assert.That(t.CellAt(2, 0).Rune).IsEqualTo('b');
    }

    [Test]
    public async Task CombiningMarkInLastColumnJoinsBeforeWrapping()
    {
        // Act
        var t = Feed("abcde\u0301f", cols: 5);

        // Assert
        await Assert.That(Cluster(t, 4, 0)).IsEqualTo("e\u0301");
        await Assert.That(t.GetLineText(1)).IsEqualTo("f");
    }

    [Test]
    public async Task MarkWithNothingBeforeItIsDropped()
    {
        // Act
        var t = Feed("\u0301a");

        // Assert
        await Assert.That(t.CellAt(0, 0).Rune).IsEqualTo('a');
        await Assert.That(Cluster(t, 0, 0)).IsNull();
        await Assert.That(t.CursorX).IsEqualTo(1);
    }

    [Test]
    public async Task Vs16MakesTextEmojiWide()
    {
        // Act
        var plain = Feed("\u2764x");            // ❤ alone: text presentation
        var withVs16 = Feed("\u2764\uFE0Fx");   // ❤ + VS16

        // Assert
        await Assert.That(IsWide(plain, 0, 0)).IsFalse();
        await Assert.That(plain.CursorX).IsEqualTo(2);
        await Assert.That(IsWide(withVs16, 0, 0)).IsTrue();
        await Assert.That(Cluster(withVs16, 0, 0)).IsEqualTo("\u2764\uFE0F");
        await Assert.That(withVs16.CellAt(2, 0).Rune).IsEqualTo('x');
        await Assert.That(withVs16.GetLineText(0)).IsEqualTo("\u2764\uFE0Fx");
    }

    [Test]
    [Arguments("1\uFE0F\u20E3")]
    [Arguments("#\uFE0F\u20E3")]
    [Arguments("*\uFE0F\u20E3")]
    [Arguments("0\uFE0F\u20E3")]
    public async Task KeycapSequencesAreOneWideCluster(string keycap)
    {
        // Act
        var t = Feed(keycap + keycap + "x");

        // Assert
        for (int x = 0; x < 4; x += 2)
        {
            await Assert.That(IsWide(t, x, 0)).IsTrue();
            await Assert.That(Cluster(t, x, 0)).IsEqualTo(keycap);
        }
        await Assert.That(t.CellAt(4, 0).Rune).IsEqualTo('x');
        await Assert.That(t.CursorX).IsEqualTo(5);
        await Assert.That(t.GetLineText(0)).IsEqualTo(keycap + keycap + "x");
    }

    [Test]
    public async Task KeycapInTheLastColumnMovesToTheNextLine()
    {
        // Act
        var t = Feed("abcd#\uFE0F\u20E3z", cols: 5);

        // Assert
        await Assert.That(t.GetLineText(0).TrimEnd()).IsEqualTo("abcd");
        await Assert.That(IsWide(t, 0, 1)).IsTrue();
        await Assert.That(Cluster(t, 0, 1)).IsEqualTo("#\uFE0F\u20E3");
        await Assert.That(t.CellAt(2, 1).Rune).IsEqualTo('z');
    }

    [Test]
    public async Task KeycapWithoutVs16StaysNarrow()
    {
        // Act
        // Unqualified (emoji-test.txt): one cell, as wcwidth and most terminals have it; the mark still joins.
        var t = Feed("7\u20E3x");

        // Assert
        await Assert.That(IsWide(t, 0, 0)).IsFalse();
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("7\u20E3");
        await Assert.That(t.CellAt(1, 0).Rune).IsEqualTo('x');
    }

    [Test]
    public async Task Vs16KeycapIsWide()
    {
        // Act
        var t = Feed("1\uFE0F\u20E3");

        // Assert
        await Assert.That(IsWide(t, 0, 0)).IsTrue();
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("1\uFE0F\u20E3");
        await Assert.That(t.CursorX).IsEqualTo(2);
    }

    [Test]
    public async Task Vs16OnNonEmojiDoesNotWiden()
    {
        // Act
        var t = Feed("a\uFE0F");

        // Assert
        await Assert.That(IsWide(t, 0, 0)).IsFalse();
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("a\uFE0F");
    }

    [Test]
    public async Task Vs16InLastColumnMovesEmojiToNextLine()
    {
        // Act
        var t = Feed("abcd\u2764\uFE0F", cols: 5);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("abcd");
        await Assert.That(t.GetScreenRow(0).Wrapped).IsTrue();
        await Assert.That(IsWide(t, 0, 1)).IsTrue();
        await Assert.That(Cluster(t, 0, 1)).IsEqualTo("\u2764\uFE0F");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((2, 1));
    }

    [Test]
    public async Task Vs16FillingTheLastTwoColumnsSetsPendingWrap()
    {
        // Act
        var t = Feed("abc\u2764\uFE0Fz", cols: 5);

        // Assert
        await Assert.That(IsWide(t, 3, 0)).IsTrue();
        await Assert.That(t.GetLineText(1)).IsEqualTo("z");
    }

    [Test]
    public async Task SkinToneModifierJoinsEmojiBase()
    {
        // Act
        var t = Feed("\U0001F44D\U0001F3FDx"); // 👍 + medium skin tone

        // Assert
        await Assert.That(IsWide(t, 0, 0)).IsTrue();
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("\U0001F44D\U0001F3FD");
        await Assert.That(t.CellAt(2, 0).Rune).IsEqualTo('x');
        await Assert.That(t.CursorX).IsEqualTo(3);
    }

    [Test]
    public async Task SkinToneAfterNonBaseIsItsOwnCell()
    {
        // Act
        var t = Feed("a\U0001F3FD");

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsNull();
        await Assert.That(t.CellAt(1, 0).Rune).IsEqualTo(0x1F3FD);
        await Assert.That(t.CursorX).IsEqualTo(3);
    }

    [Test]
    public async Task ZwjSequenceIsOneCluster()
    {
        // Arrange
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467"; // man ZWJ woman ZWJ girl

        // Act
        var t = Feed(family + "x");

        // Assert
        await Assert.That(IsWide(t, 0, 0)).IsTrue();
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo(family);
        await Assert.That(t.CellAt(2, 0).Rune).IsEqualTo('x');
        await Assert.That(t.GetLineText(0)).IsEqualTo(family + "x");
    }

    [Test]
    public async Task ZwjWithTextPresentationPartsAndVs16()
    {
        // Arrange
        const string runner = "\U0001F3C3\u200D\u2640\uFE0F"; // woman running
        const string heartOnFire = "\u2764\uFE0F\u200D\U0001F525";

        // Act
        var t = Feed(runner + heartOnFire);

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo(runner);
        await Assert.That(Cluster(t, 2, 0)).IsEqualTo(heartOnFire);
        await Assert.That(t.CursorX).IsEqualTo(4);
    }

    [Test]
    public async Task ZwjAfterLetterDoesNotSwallowFollowingEmoji()
    {
        // Act
        var t = Feed("a\u200D\U0001F600");

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("a\u200D");
        await Assert.That(t.CellAt(1, 0).Rune).IsEqualTo(0x1F600);
    }

    [Test]
    public async Task FlagsPairRegionalIndicators()
    {
        // Act
        var t = Feed("\U0001F1F3\U0001F1F1\U0001F1E9\U0001F1EA"); // NL, DE

        // Assert
        await Assert.That(IsWide(t, 0, 0)).IsTrue();
        await Assert.That(IsWide(t, 2, 0)).IsTrue();
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("\U0001F1F3\U0001F1F1");
        await Assert.That(Cluster(t, 2, 0)).IsEqualTo("\U0001F1E9\U0001F1EA");
        await Assert.That(t.CursorX).IsEqualTo(4);
        await Assert.That(t.GetLineText(0)).IsEqualTo("\U0001F1F3\U0001F1F1\U0001F1E9\U0001F1EA");
    }

    [Test]
    public async Task TagSequenceFlagIsOneCluster()
    {
        // Arrange
        const string scotland = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";

        // Act
        var t = Feed(scotland + "x");

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo(scotland);
        await Assert.That(t.CellAt(2, 0).Rune).IsEqualTo('x');
    }

    [Test]
    public async Task OverwritingACellDropsItsCluster()
    {
        // Act
        var t = Feed("e\u0301\rx");

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsNull();
        await Assert.That(t.GetLineText(0)).IsEqualTo("x");
    }

    [Test]
    public async Task ErasingACellDropsItsCluster()
    {
        // Act
        var t = Feed("e\u0301\e[1;1H\e[K");

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsNull();
    }

    [Test]
    public async Task OverwritingTheWideTrailDropsTheLeadsCluster()
    {
        // Act
        var t = Feed("\U0001F44D\U0001F3FD\e[1;2Hx"); // overwriting the trail blanks the lead

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsNull();
        await Assert.That(t.GetLineText(0)).IsEqualTo(" x");
    }

    [Test]
    public async Task InsertCharsMoveClusters()
    {
        // Act
        var t = Feed("ae\u0301b\e[1;1H\e[2@");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("  ae\u0301b");
        await Assert.That(Cluster(t, 3, 0)).IsEqualTo("e\u0301");
        await Assert.That(Cluster(t, 1, 0)).IsNull();
    }

    [Test]
    public async Task DeleteCharsMoveClusters()
    {
        // Arrange
        var t = Feed("ae\u0301b\e[1;1H\e[2@");

        // Act
        t.Feed("\e[3P"u8);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("e\u0301b");
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("e\u0301");
    }

    [Test]
    public async Task ClustersScrollIntoScrollbackAndRowsAreRecycledClean()
    {
        // Arrange
        var t = new Terminal(10, 2, scrollbackLines: 1);

        // Act
        t.Feed(Encoding.UTF8.GetBytes("e\u0301\r\n1\r\n2\r\n3\r\n"));

        // Assert
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 10; x++) await Assert.That(Cluster(t, x, y)).IsNull();
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine, 9)).IsEqualTo("2");
    }

    [Test]
    public async Task GetTextRoundTripsMixedClusters()
    {
        // Arrange
        // e + acute, thumbs up + skin tone, the ZWJ family, the NL flag, heart + VS16, then two CJK ideographs.
        const string text = "e\u0301 \U0001F44D\U0001F3FD \U0001F468\u200D\U0001F469\u200D\U0001F467 \U0001F1F3\U0001F1F1 \u2764\uFE0F \u65E5\u672C";

        // Act
        var t = Feed(text, cols: 40);

        // Assert
        await Assert.That(t.GetText(t.ScreenTopLine, 0, t.ScreenTopLine, 39)).IsEqualTo(text);
        await Assert.That(t.GetLineText(0)).IsEqualTo(text);
        await Assert.That(t.GetCellText(5, 0)).IsEqualTo("\U0001F468\u200D\U0001F469\u200D\U0001F467");
        await Assert.That(t.GetCellText(6, 0)).IsEqualTo(""); // the wide trail
    }

    [Test]
    public async Task GetTextAcrossWrapSkipsWidePadding()
    {
        // Act
        var t = Feed("abcd\u65E5", cols: 5); // 日 doesn't fit in the last column

        // Assert
        await Assert.That(t.GetText(t.ScreenTopLine, 0, t.ScreenTopLine + 1, 4)).IsEqualTo("abcd\u65E5");
    }
    [Test]
    public async Task ClustersWrittenOutOfColumnOrderAreAllFound()
    {
        // Act: right to left by moving the cursor back
        var t = Feed("\e[1;5Hé\e[1;3Há\e[1;1Hó", cols: 10);

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("ó");
        await Assert.That(Cluster(t, 2, 0)).IsEqualTo("á");
        await Assert.That(Cluster(t, 4, 0)).IsEqualTo("é");
    }

    [Test]
    public async Task InsertCharsMovesClustersAndDropsThoseOffTheRow()
    {
        // Arrange
        var t = Feed("áb́ćd́", cols: 6);

        // Act: three blanks at column 1 push b, c, d right; d's cluster falls off the row
        t.Feed(Encoding.UTF8.GetBytes("\e[1;2H\e[3@"));

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("á");
        await Assert.That(Cluster(t, 1, 0)).IsNull();
        await Assert.That(Cluster(t, 4, 0)).IsEqualTo("b́");
        await Assert.That(Cluster(t, 5, 0)).IsEqualTo("ć");
    }

    [Test]
    public async Task DeleteCharsMovesClustersOverStaleEntries()
    {
        // Arrange: x at column 1 replaced the cluster there, leaving a stale entry that the moved one must replace
        var t = Feed("áb́ć", cols: 6);
        t.Feed(Encoding.UTF8.GetBytes("\e[1;2Hx"));

        // Act: delete columns 1 and 2 of "a x c"
        t.Feed(Encoding.UTF8.GetBytes("\e[1;2H\e[1P"));

        // Assert
        await Assert.That(Cluster(t, 0, 0)).IsEqualTo("á");
        await Assert.That(Cluster(t, 1, 0)).IsEqualTo("ć");
        await Assert.That(Cluster(t, 2, 0)).IsNull();
    }

    [Test]
    public async Task IdenticalClustersOnDifferentRowsShareTheirString()
    {
        // Act
        var t = Feed("é\r\né", cols: 6);
        string? first = t.GetScreenRow(0).GetClusterString(0), second = t.GetScreenRow(1).GetClusterString(0);

        // Assert
        await Assert.That(first).IsEqualTo("é");
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task ClearedRowForgetsItsClusters()
    {
        // Arrange
        var t = Feed("é", cols: 6);

        // Act
        t.Feed(Encoding.UTF8.GetBytes("\e[2K"));

        // Assert
        await Assert.That(t.GetScreenRow(0).HasClusters).IsFalse();
        await Assert.That(Cluster(t, 0, 0)).IsNull();
    }
}
