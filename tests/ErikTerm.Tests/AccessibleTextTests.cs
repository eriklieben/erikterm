using System.Text;
using ErikTerm.Core;
using ErikTerm.Core.Accessibility;

namespace ErikTerm.Tests;

/// <summary>The text model screen readers read through (Windows UI Automation's text pattern sits on top of it).</summary>
public class AccessibleTextTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static TextPoint P(long line, int col) => new(line, col);

    // ---- Rows ---------------------------------------------------------------------------------

    [Test]
    public async Task A_wide_character_is_one_character_over_two_columns()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "a界b");

        // Act
        var row = AccessibleRow.From(t.GetScreenRow(0), t.Columns);

        // Assert
        await Assert.That(row.CharText(1).ToString()).IsEqualTo("界");
        await Assert.That(row.Column(2)).IsEqualTo(3);
        await Assert.That(row.CharAt(2)).IsEqualTo(1);
        await Assert.That(row.Count).IsEqualTo(9);
    }

    [Test]
    public async Task A_combining_sequence_is_one_character()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "éx");

        // Act
        var row = AccessibleRow.From(t.GetScreenRow(0), t.Columns);

        // Assert
        await Assert.That(row.CharText(0).ToString()).IsEqualTo("é");
        await Assert.That(row.CharText(1).ToString()).IsEqualTo("x");
        await Assert.That(row.TextEnd).IsEqualTo(2);
    }

    [Test]
    public async Task Hidden_text_reads_as_blanks()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "a\e[8mpw\e[0mb");

        // Act
        var row = AccessibleRow.From(t.GetScreenRow(0), t.Columns);

        // Assert
        await Assert.That(row.Text(0, row.TextEnd).ToString()).IsEqualTo("a  b");
    }

    [Test]
    public async Task Terminal_rows_end_after_the_last_line_with_content()
    {
        // Arrange
        var t = Feed(new Terminal(10, 5), "one\r\ntwo");
        var rows = new TerminalRows(t, new Lock());

        // Act
        long end = rows.EndLine;

        // Assert
        await Assert.That(end).IsEqualTo(t.ScreenTopLine + 2);
    }

    [Test]
    public async Task Terminal_rows_cover_the_scrollback()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2, scrollbackLines: 50), "l1\r\nl2\r\nl3\r\nl4");
        var rows = new TerminalRows(t, new Lock());

        // Act
        string text = TextNavigator.GetText(rows, TextNavigator.Document(rows));

        // Assert
        await Assert.That(text).IsEqualTo("l1\r\nl2\r\nl3\r\nl4\r\n");
    }

    [Test]
    public async Task A_screen_row_that_changes_is_decoded_again()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "ab");
        var rows = new TerminalRows(t, new Lock());
        _ = rows.Row(t.ScreenTopLine);

        // Act
        Feed(t, "c");
        var row = rows.Row(t.ScreenTopLine);

        // Assert
        await Assert.That(row.Text(0, row.TextEnd).ToString()).IsEqualTo("abc");
    }

    // ---- Characters ---------------------------------------------------------------------------

    [Test]
    public async Task Moving_by_character_steps_over_a_wide_character_in_one_move()
    {
        // Arrange
        var rows = FixedRows.Of(6, "a界b");

        // Act
        var p = TextNavigator.MoveEndpoint(rows, P(0, 1), TextUnit.Character, 1, out int moved);

        // Assert
        await Assert.That(p).IsEqualTo(P(0, 3));
        await Assert.That(moved).IsEqualTo(1);
    }

    [Test]
    public async Task The_second_column_of_a_wide_character_normalizes_to_its_first()
    {
        // Arrange
        var rows = FixedRows.Of(6, "a界b");

        // Act
        var p = TextNavigator.Normalize(rows, P(0, 2));

        // Assert
        await Assert.That(p).IsEqualTo(P(0, 1));
    }

    [Test]
    public async Task Moving_back_from_a_line_start_lands_on_the_last_character_of_the_line_above()
    {
        // Arrange
        var rows = FixedRows.Of(4, "ab", "cd");

        // Act
        var p = TextNavigator.MoveEndpoint(rows, P(1, 0), TextUnit.Character, -1, out int moved);

        // Assert
        await Assert.That(p).IsEqualTo(P(0, 3));
        await Assert.That(moved).IsEqualTo(-1);
    }

    [Test]
    public async Task Moving_stops_at_the_document_end_and_says_how_far_it_got()
    {
        // Arrange
        var rows = FixedRows.Of(2, "ab");

        // Act
        var p = TextNavigator.MoveEndpoint(rows, P(0, 0), TextUnit.Character, 10, out int moved);

        // Assert
        await Assert.That(p).IsEqualTo(P(1, 0));
        await Assert.That(moved).IsEqualTo(2);
    }

    [Test]
    public async Task Expanding_a_caret_to_a_character_covers_the_cell_under_it()
    {
        // Arrange
        var rows = FixedRows.Of(6, "a界b");
        var caret = new TextSpan(P(0, 2), P(0, 2));

        // Act
        var span = TextNavigator.Expand(rows, caret, TextUnit.Character);

        // Assert
        await Assert.That(TextNavigator.GetText(rows, span)).IsEqualTo("界");
    }

    // ---- Words --------------------------------------------------------------------------------

    [Test]
    public async Task Words_are_runs_of_letters_or_of_punctuation_with_the_blanks_after_them()
    {
        // Arrange
        var rows = FixedRows.Of(20, "git push --force");
        var words = new List<string>();
        var p = P(0, 0);

        // Act
        while (p < TextNavigator.DocumentEnd(rows))
        {
            var next = TextNavigator.NextBoundary(rows, p, TextUnit.Word);
            words.Add(TextNavigator.GetText(rows, new TextSpan(p, next)));
            p = next;
        }

        // Assert
        await Assert.That(words).IsEquivalentTo(["git ", "push ", "--", "force\r\n"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Every_line_starts_a_word_even_a_blank_one()
    {
        // Arrange
        var rows = FixedRows.Of(5, "ab", "", "cd");

        // Act
        var p = TextNavigator.MoveEndpoint(rows, P(0, 0), TextUnit.Word, 2, out int moved);

        // Assert
        await Assert.That(p).IsEqualTo(P(2, 0));
        await Assert.That(moved).IsEqualTo(2);
    }

    [Test]
    public async Task A_word_runs_on_across_a_soft_wrap()
    {
        // Arrange
        var rows = FixedRows.Of(4, "abcd\\", "ef g");

        // Act
        var span = TextNavigator.Expand(rows, new TextSpan(P(1, 1), P(1, 1)), TextUnit.Word);

        // Assert
        await Assert.That(TextNavigator.GetText(rows, span)).IsEqualTo("abcdef ");
    }

    [Test]
    public async Task Expanding_inside_a_word_covers_the_whole_word()
    {
        // Arrange
        var rows = FixedRows.Of(20, "echo hello world");

        // Act
        var span = TextNavigator.Expand(rows, new TextSpan(P(0, 7), P(0, 8)), TextUnit.Word);

        // Assert
        await Assert.That(TextNavigator.GetText(rows, span)).IsEqualTo("hello ");
    }

    // ---- Lines and paragraphs -----------------------------------------------------------------

    [Test]
    public async Task A_line_is_one_row_and_its_text_ends_with_a_line_break()
    {
        // Arrange
        var rows = FixedRows.Of(10, "first", "second");

        // Act
        var span = TextNavigator.Expand(rows, new TextSpan(P(1, 3), P(1, 3)), TextUnit.Line);

        // Assert
        await Assert.That(span).IsEqualTo(new TextSpan(P(1, 0), P(2, 0)));
        await Assert.That(TextNavigator.GetText(rows, span)).IsEqualTo("second\r\n");
    }

    [Test]
    public async Task A_soft_wrapped_line_is_two_lines_but_one_paragraph()
    {
        // Arrange
        var rows = FixedRows.Of(4, "abcd\\", "ef", "gh");
        var caret = new TextSpan(P(1, 1), P(1, 1));

        // Act
        var line = TextNavigator.Expand(rows, caret, TextUnit.Line);
        var paragraph = TextNavigator.Expand(rows, caret, TextUnit.Paragraph);

        // Assert
        await Assert.That(TextNavigator.GetText(rows, line)).IsEqualTo("ef\r\n");
        await Assert.That(TextNavigator.GetText(rows, paragraph)).IsEqualTo("abcdef\r\n");
    }

    [Test]
    public async Task Moving_a_line_range_keeps_it_one_line_long()
    {
        // Arrange
        var rows = FixedRows.Of(10, "a", "b", "c");
        var line = new TextSpan(P(0, 0), P(1, 0));

        // Act
        var moved = TextNavigator.Move(rows, line, TextUnit.Line, 1, out int count);

        // Assert
        await Assert.That(moved).IsEqualTo(new TextSpan(P(1, 0), P(2, 0)));
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task A_range_cannot_be_moved_to_start_at_the_document_end()
    {
        // Arrange
        var rows = FixedRows.Of(10, "a", "b");
        var last = new TextSpan(P(1, 0), P(2, 0));

        // Act
        var moved = TextNavigator.Move(rows, last, TextUnit.Line, 1, out int count);

        // Assert
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(moved).IsEqualTo(last);
    }

    [Test]
    public async Task Expanding_at_the_document_end_reads_the_last_line()
    {
        // Arrange
        var rows = FixedRows.Of(10, "a", "prompt $");
        var end = TextNavigator.DocumentEnd(rows);

        // Act
        var span = TextNavigator.Expand(rows, new TextSpan(end, end), TextUnit.Line);

        // Assert
        await Assert.That(TextNavigator.GetText(rows, span)).IsEqualTo("prompt $\r\n");
    }

    [Test]
    public async Task Moving_back_by_lines_counts_negative()
    {
        // Arrange
        var rows = FixedRows.Of(10, "a", "b", "c");

        // Act
        var moved = TextNavigator.Move(rows, new TextSpan(P(2, 0), P(3, 0)), TextUnit.Line, -5, out int count);

        // Assert
        await Assert.That(count).IsEqualTo(-2);
        await Assert.That(moved.Start).IsEqualTo(P(0, 0));
    }

    // ---- Text and rectangles ------------------------------------------------------------------

    [Test]
    public async Task Text_stops_at_max_length()
    {
        // Arrange
        var rows = FixedRows.Of(10, "abcdef");

        // Act
        string text = TextNavigator.GetText(rows, TextNavigator.Document(rows), maxLength: 3);

        // Assert
        await Assert.That(text).IsEqualTo("abc");
    }

    [Test]
    public async Task A_soft_wrap_joins_rows_without_a_line_break()
    {
        // Arrange
        var rows = FixedRows.Of(3, "abc\\", "de");

        // Act
        string text = TextNavigator.GetText(rows, TextNavigator.Document(rows));

        // Assert
        await Assert.That(text).IsEqualTo("abcde\r\n");
    }

    [Test]
    public async Task Row_spans_cover_text_not_trailing_blanks()
    {
        // Arrange
        var rows = FixedRows.Of(10, "ab", "cdef");

        // Act
        var spans = TextNavigator.RowSpans(rows, new TextSpan(P(0, 1), P(1, 2)));

        // Assert
        await Assert.That(spans).IsEquivalentTo([new RowSpan(0, 1, 2), new RowSpan(1, 0, 2)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_caret_has_a_zero_width_span()
    {
        // Arrange
        var rows = FixedRows.Of(10, "ab");

        // Act
        var spans = TextNavigator.RowSpans(rows, new TextSpan(P(0, 1), P(0, 1)));

        // Assert
        await Assert.That(spans).IsEquivalentTo([new RowSpan(0, 1, 1)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Find_returns_the_range_of_a_match()
    {
        // Arrange
        var rows = FixedRows.Of(10, "one", "Two two");

        // Act
        var found = TextNavigator.Find(rows, TextNavigator.Document(rows), "two", backward: false, ignoreCase: true);
        var last = TextNavigator.Find(rows, TextNavigator.Document(rows), "two", backward: true, ignoreCase: false);

        // Assert
        await Assert.That(found).IsEqualTo(new TextSpan(P(1, 0), P(1, 3)));
        await Assert.That(last).IsEqualTo(new TextSpan(P(1, 4), P(1, 7)));
    }

    [Test]
    public async Task Points_outside_the_document_are_clamped()
    {
        // Arrange
        var rows = new FixedRows([AccessibleRow.FromText("ab", 4)], firstLine: 10);

        // Act
        var before = TextNavigator.Normalize(rows, P(3, 2));
        var after = TextNavigator.Normalize(rows, P(99, 0));
        var pastRowEnd = TextNavigator.Normalize(rows, P(10, 4));

        // Assert
        await Assert.That(before).IsEqualTo(P(10, 0));
        await Assert.That(after).IsEqualTo(P(11, 0));
        await Assert.That(pastRowEnd).IsEqualTo(P(11, 0));
    }

    // ---- What's new ---------------------------------------------------------------------------

    [Test]
    public async Task The_first_update_only_takes_a_baseline()
    {
        // Arrange
        var t = Feed(new Terminal(20, 4), "$ ");
        var tracker = new OutputTracker();

        // Act
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change).IsNull();
    }

    [Test]
    public async Task New_lines_and_the_new_prompt_are_reported()
    {
        // Arrange
        var t = Feed(new Terminal(20, 4), "$ ls");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "\r\nfile1 file2\r\n$ ");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change?.Text).IsEqualTo("file1 file2\n$");
        await Assert.That(change?.RewriteOnly).IsFalse();
    }

    [Test]
    public async Task Text_added_to_a_line_is_reported_alone()
    {
        // Arrange
        var t = Feed(new Terminal(20, 4), "$ l");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "s");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change?.Text).IsEqualTo("s");
    }

    [Test]
    public async Task Deleting_text_reports_nothing()
    {
        // Arrange
        var t = Feed(new Terminal(20, 4), "$ ls");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "\b\e[K");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change).IsNull();
    }

    [Test]
    public async Task A_line_rewritten_in_place_is_reported_whole_as_a_rewrite()
    {
        // Arrange
        var t = Feed(new Terminal(30, 4), "Downloading 45%");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "\rDownloading 46%");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change?.Text).IsEqualTo("Downloading 46%");
        await Assert.That(change?.RewriteOnly).IsTrue();
    }

    [Test]
    public async Task Output_that_scrolled_off_the_screen_is_still_found()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2, scrollbackLines: 100), "$ ");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "\r\na\r\nb\r\nc\r\n$ ");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change?.Text).IsEqualTo("a\nb\nc\n$");
    }

    [Test]
    public async Task A_flood_is_read_back_only_from_its_end()
    {
        // Arrange
        var t = new Terminal(10, 4, scrollbackLines: 1000);
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, string.Concat(Enumerable.Range(0, 500).Select(i => $"{i}\r\n")));
        var change = tracker.Update(t);

        // Assert
        var lines = change!.Value.Text.Split('\n');
        await Assert.That(lines.Length).IsLessThanOrEqualTo(OutputTracker.MaxLines);
        await Assert.That(lines[^1]).IsEqualTo("499");
    }

    [Test]
    public async Task Switching_to_the_alternate_screen_takes_a_new_baseline()
    {
        // Arrange
        var t = Feed(new Terminal(20, 4), "$ vim");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "\e[?1049h~\r\n~");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change).IsNull();
    }

    [Test]
    public async Task Changes_on_the_alternate_screen_say_so()
    {
        // Arrange
        var t = Feed(new Terminal(20, 4), "\e[?1049h");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "menu");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change?.AlternateScreen).IsTrue();
    }

    [Test]
    public async Task A_soft_wrapped_line_is_reported_as_one_line()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "$ ");
        var tracker = new OutputTracker();
        tracker.Update(t);

        // Act
        Feed(t, "\r\nabcdefgh\r\n");
        var change = tracker.Update(t);

        // Assert
        await Assert.That(change?.Text).IsEqualTo("abcdefgh");
    }

    [Test]
    public async Task Typing_in_the_middle_of_a_line_reports_only_the_inserted_text()
    {
        // Arrange & Act
        var (text, rewrite) = OutputTracker.Changed("$ ls -l", "$ lsa -l");

        // Assert
        await Assert.That(text).IsEqualTo("a");
        await Assert.That(rewrite).IsFalse();
    }
}
