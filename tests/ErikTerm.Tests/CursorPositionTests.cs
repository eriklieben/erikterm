using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class CursorPositionTests
{
    private static Terminal Fed(string text, int chunkBytes = int.MaxValue)
    {
        var term = new Terminal(20, 10);
        var bytes = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i < bytes.Length; i += chunkBytes)
            term.Feed(bytes.AsSpan(i, Math.Min(chunkBytes, bytes.Length - i)));
        return term;
    }

    private static string Screen(Terminal term)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < term.Rows; y++) sb.Append(term.GetLineText(y)).Append('\n');
        return sb.Append(term.CursorX).Append(',').Append(term.CursorY).ToString();
    }

    [Test]
    [Arguments("\u001b[5;7H", 6, 4)]
    [Arguments("\u001b[999;999H", 19, 9)]
    [Arguments("\u001b[0;0H", 0, 0)]
    [Arguments("\u001b[;7H", 6, 0)]
    [Arguments("\u001b[5;H", 0, 4)]
    [Arguments("\u001b[5H", 0, 4)]
    [Arguments("\u001b[5;7;9H", 6, 4)]
    [Arguments("\u001b[5:7H", 6, 4)]
    public async Task Every_form_lands_on_the_same_cell_whole_or_split(string sequence, int x, int y)
    {
        // Arrange: the prefix moves the cursor away from the home position
        string text = "\u001b[3;3H" + sequence;

        // Act
        var whole = Fed(text);
        var split = Fed(text, chunkBytes: 1);

        // Assert
        await Assert.That((whole.CursorX, whole.CursorY)).IsEqualTo((x, y));
        await Assert.That((split.CursorX, split.CursorY)).IsEqualTo((x, y));
    }

    [Test]
    public async Task Origin_mode_counts_from_the_top_margin()
    {
        // Act
        var term = Fed("\u001b[3;6r\u001b[?6h\u001b[2;4H");

        // Assert
        await Assert.That((term.CursorX, term.CursorY)).IsEqualTo((3, 3));
    }

    [Test]
    public async Task Moving_cancels_a_pending_wrap()
    {
        // Arrange: exactly a full row leaves a wrap pending
        string fill = new('a', 20);

        // Act
        var term = Fed(fill + "\u001b[4;2Hb");

        // Assert
        await Assert.That((term.CursorX, term.CursorY)).IsEqualTo((2, 3));
    }

    [Test]
    public async Task Random_moves_and_words_match_the_byte_by_byte_path()
    {
        // Arrange
        var rng = new Random(20261109);
        string[] forms = ["\u001b[{0};{1}H", "\u001b[{0};{1}f", "\u001b[;{1}H", "\u001b[{0};H", "\u001b[{0}H", "\u001b[{0}:{1}H"];
        var sb = new StringBuilder("\u001b[2;9r");
        for (int i = 0; i < 2000; i++)
        {
            sb.AppendFormat(forms[rng.Next(forms.Length)], rng.Next(0, 30), rng.Next(0, 40));
            sb.Append((char)('a' + rng.Next(26)), rng.Next(1, 8));
        }
        string text = sb.ToString();

        // Act
        string whole = Screen(Fed(text));
        string split = Screen(Fed(text, chunkBytes: 1));

        // Assert
        await Assert.That(whole).IsEqualTo(split);
    }
}
