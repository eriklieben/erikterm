using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Making the screen shorter: blank rows go first, and text below the cursor isn't thrown away if it can stay.</summary>
public class ResizeShrinkTests
{
    private static Terminal Screen(string text)
    {
        var t = new Terminal(20, 5, 100);
        t.Feed(Encoding.UTF8.GetBytes(text));
        return t;
    }

    private const string FiveLines = "one\r\ntwo\r\nthree\r\nfour\r\nfive";

    [Test]
    public async Task TextBelowTheCursorStaysAndTheTopScrollsOff()
    {
        // Arrange: the cursor on the third line, text on the two below it.
        var t = Screen(FiveLines + "\u001b[3;1H");

        // Act
        t.Resize(20, 3);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("three");
        await Assert.That(t.GetLineText(2)).IsEqualTo("five");
        await Assert.That(t.ScrollbackCount).IsEqualTo(2);
    }

    [Test]
    public async Task BlankRowsBelowTheCursorGoFirst()
    {
        // Arrange
        var t = Screen("a\r\nb");

        // Act
        t.Resize(20, 3);

        // Assert: nothing had to scroll off.
        await Assert.That(t.GetLineText(0)).IsEqualTo("a");
        await Assert.That(t.GetLineText(1)).IsEqualTo("b");
        await Assert.That(t.ScrollbackCount).IsEqualTo(0);
    }

    [Test]
    public async Task TheCursorsRowStaysOnTheScreen()
    {
        // Arrange: the cursor on the top line, text filling the rest.
        var t = Screen(FiveLines + "\u001b[1;1H");

        // Act
        t.Resize(20, 3);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("one");
        await Assert.That(t.CursorY).IsEqualTo(0);
        await Assert.That(t.ScrollbackCount).IsEqualTo(0);
    }

    [Test]
    public async Task ACursorOnTheLastLineScrollsAsBefore()
    {
        // Arrange
        var t = Screen(FiveLines);

        // Act
        t.Resize(20, 3);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("three");
        await Assert.That(t.GetLineText(2)).IsEqualTo("five");
        await Assert.That(t.CursorY).IsEqualTo(2);
    }
}
