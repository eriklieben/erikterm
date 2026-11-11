using System.Text;

namespace ErikTerm.Core.Accessibility;

/// <summary>Text units a screen reader moves by. The values are UI Automation's TextUnit.</summary>
public enum TextUnit
{
    Character = 0,
    /// <summary>Not supported: treated as <see cref="Word"/>, the next larger unit, as UI Automation asks.</summary>
    Format = 1,
    Word = 2,
    /// <summary>One terminal row, as Windows Terminal counts lines: a soft-wrapped line is several.</summary>
    Line = 3,
    /// <summary>A logical line: a row and the rows it soft-wraps into.</summary>
    Paragraph = 4,
    /// <summary>Not supported: treated as <see cref="Document"/>.</summary>
    Page = 5,
    Document = 6,
}

/// <summary>A range of text from <see cref="Start"/> up to (not including) <see cref="End"/>.</summary>
public readonly record struct TextSpan(TextPoint Start, TextPoint End)
{
    public bool IsEmpty => Start == End;
}

/// <summary>One row's part of a range, in columns [StartCol, EndCol): what a bounding rectangle covers.</summary>
public readonly record struct RowSpan(long Line, int StartCol, int EndCol);

/// <summary>
/// Moving through a terminal's text by characters, words, lines, logical lines and the whole document, the way UI
/// Automation's text ranges do (ITextRangeProvider: Move, MoveEndpointByUnit, ExpandToEnclosingUnit, GetText). Pure
/// functions over <see cref="IAccessibleRows"/>, so the rules are tested without Windows.
/// <para>
/// Units: a character is a cell, or two for a wide character, and a grapheme cluster is one. A word starts where a
/// run of letters and digits or a run of punctuation starts, and at the start of every line (so a blank line is a
/// word), and takes the blanks after it. A line is a row, as in Windows Terminal; a paragraph joins soft wraps.
/// </para>
/// </summary>
public static class TextNavigator
{
    /// <summary>The most rows a logical line is followed through, so a pathological wrap can't stall a call.</summary>
    private const int MaxParagraphRows = 1000;

    public static TextPoint DocumentStart(IAccessibleRows rows) => new(rows.FirstLine, 0);

    public static TextPoint DocumentEnd(IAccessibleRows rows) => new(Math.Max(rows.FirstLine, rows.EndLine), 0);

    public static TextSpan Document(IAccessibleRows rows) => new(DocumentStart(rows), DocumentEnd(rows));

    /// <summary>Unsupported units become the next larger supported one.</summary>
    public static TextUnit Supported(TextUnit unit) => unit switch
    {
        TextUnit.Format => TextUnit.Word,
        TextUnit.Page => TextUnit.Document,
        < TextUnit.Character => TextUnit.Character,
        > TextUnit.Document => TextUnit.Document,
        _ => unit,
    };

    /// <summary>
    /// The point clamped into the document and onto the start of the character it falls in (the second column of a
    /// wide character belongs to the first). A column past the row's end is the start of the next line.
    /// </summary>
    public static TextPoint Normalize(IAccessibleRows rows, TextPoint p)
    {
        var start = DocumentStart(rows);
        var end = DocumentEnd(rows);
        if (p <= start) return start;
        if (p >= end) return end;
        if (p.Col <= 0) return p with { Col = 0 };
        var row = rows.Row(p.Line);
        if (p.Col >= row.Width) return TextPoint.Min(new TextPoint(p.Line + 1, 0), end);
        return p with { Col = row.Column(row.CharAt(p.Col)) };
    }

    public static TextSpan Normalize(IAccessibleRows rows, TextSpan span)
    {
        var a = Normalize(rows, span.Start);
        var b = Normalize(rows, span.End);
        return a <= b ? new TextSpan(a, b) : new TextSpan(b, a);
    }

    // ---- Boundaries ---------------------------------------------------------------------------

    /// <summary>Whether a unit starts at <paramref name="p"/> (a normalized point). The document's end counts as one.</summary>
    public static bool IsBoundary(IAccessibleRows rows, TextPoint p, TextUnit unit)
    {
        if (p == DocumentStart(rows) || p == DocumentEnd(rows)) return true;
        return Supported(unit) switch
        {
            TextUnit.Character => true,
            TextUnit.Word => IsWordStart(rows, p),
            TextUnit.Line => p.Col == 0,
            TextUnit.Paragraph => p.Col == 0 && !rows.Row(p.Line - 1).Wrapped,
            _ => false,
        };
    }

    /// <summary>The first boundary after <paramref name="p"/>; the document's end when there is none.</summary>
    public static TextPoint NextBoundary(IAccessibleRows rows, TextPoint p, TextUnit unit)
    {
        var end = DocumentEnd(rows);
        if (p >= end) return end;
        switch (Supported(unit))
        {
            case TextUnit.Character: return NextChar(rows, p);
            case TextUnit.Line: return TextPoint.Min(new TextPoint(p.Line + 1, 0), end);
            case TextUnit.Paragraph: return NextParagraph(rows, p, end);
            case TextUnit.Word:
                var q = NextChar(rows, p);
                while (q < end && !IsWordStart(rows, q)) q = NextChar(rows, q);
                return q;
            default: return end;
        }
    }

    /// <summary>The last boundary before <paramref name="p"/>; the document's start when there is none.</summary>
    public static TextPoint PreviousBoundary(IAccessibleRows rows, TextPoint p, TextUnit unit)
    {
        var start = DocumentStart(rows);
        if (p <= start) return start;
        switch (Supported(unit))
        {
            case TextUnit.Character: return PreviousChar(rows, p);
            case TextUnit.Line: return TextPoint.Max(p.Col > 0 ? p with { Col = 0 } : new TextPoint(p.Line - 1, 0), start);
            case TextUnit.Paragraph: return PreviousParagraph(rows, p, start);
            case TextUnit.Word:
                var q = PreviousChar(rows, p);
                while (q > start && !IsWordStart(rows, q)) q = PreviousChar(rows, q);
                return q;
            default: return start;
        }
    }

    /// <summary>Where the unit containing <paramref name="p"/> starts.</summary>
    public static TextPoint UnitStart(IAccessibleRows rows, TextPoint p, TextUnit unit) =>
        IsBoundary(rows, p, unit) ? p : PreviousBoundary(rows, p, unit);

    // ---- Range operations (ITextRangeProvider) -------------------------------------------------

    /// <summary>
    /// ExpandToEnclosingUnit: exactly one unit, the one the range starts in. At the document's end that is the last
    /// unit, so a caret after the last character still reads the last line.
    /// </summary>
    public static TextSpan Expand(IAccessibleRows rows, TextSpan span, TextUnit unit)
    {
        var p = Normalize(rows, span.Start);
        if (p == DocumentEnd(rows) && p > DocumentStart(rows)) p = PreviousBoundary(rows, p, unit);
        var start = UnitStart(rows, p, unit);
        return new TextSpan(start, NextBoundary(rows, start, unit));
    }

    /// <summary>
    /// Move: a degenerate range (a caret) moves as a point. Any other range is normalized to the unit it starts in,
    /// moved by whole units — never so far that it starts at the document's end — and covers one unit again.
    /// </summary>
    public static TextSpan Move(IAccessibleRows rows, TextSpan span, TextUnit unit, int count, out int moved)
    {
        span = Normalize(rows, span);
        moved = 0;
        if (count == 0) return span;
        if (span.IsEmpty)
        {
            var p = MoveEndpoint(rows, span.Start, unit, count, out moved);
            return new TextSpan(p, p);
        }
        var s = UnitStart(rows, span.Start, unit);
        var end = DocumentEnd(rows);
        var start = DocumentStart(rows);
        while (moved < count)
        {
            var n = NextBoundary(rows, s, unit);
            if (n >= end) break;
            s = n;
            moved++;
        }
        while (moved > count && s > start)
        {
            s = PreviousBoundary(rows, s, unit);
            moved--;
        }
        return new TextSpan(s, NextBoundary(rows, s, unit));
    }

    /// <summary>MoveEndpointByUnit for one endpoint: up to <paramref name="count"/> boundaries on, stopping at the document's ends.</summary>
    public static TextPoint MoveEndpoint(IAccessibleRows rows, TextPoint p, TextUnit unit, int count, out int moved)
    {
        p = Normalize(rows, p);
        moved = 0;
        var start = DocumentStart(rows);
        var end = DocumentEnd(rows);
        while (moved < count && p < end)
        {
            p = NextBoundary(rows, p, unit);
            moved++;
        }
        while (moved > count && p > start)
        {
            p = PreviousBoundary(rows, p, unit);
            moved--;
        }
        return p;
    }

    /// <summary>
    /// The text of a range. A line that ends inside the range ends with "\r\n" and loses its trailing blanks; a
    /// soft-wrapped row runs on into the next one without either. <paramref name="maxLength"/> below 0: all of it.
    /// </summary>
    public static string GetText(IAccessibleRows rows, TextSpan span, int maxLength = -1)
    {
        span = Normalize(rows, span);
        var sb = new StringBuilder();
        if (maxLength == 0) return "";
        for (long line = span.Start.Line; line <= span.End.Line && line < rows.EndLine; line++)
        {
            var row = rows.Row(line);
            var (from, to, lineBreak) = CharRange(row, line, span);
            if (to > from) sb.Append(row.Text(from, to));
            if (lineBreak) sb.Append("\r\n");
            if (maxLength > 0 && sb.Length >= maxLength) return sb.ToString(0, maxLength);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The columns a range covers on each of its rows, for bounding rectangles. A row the range runs to the end of
    /// covers its text, not the blanks after it. A degenerate range gives one zero-width span: the caret.
    /// </summary>
    public static List<RowSpan> RowSpans(IAccessibleRows rows, TextSpan span)
    {
        span = Normalize(rows, span);
        var spans = new List<RowSpan>();
        if (span.IsEmpty)
        {
            spans.Add(new RowSpan(span.Start.Line, span.Start.Col, span.Start.Col));
            return spans;
        }
        for (long line = span.Start.Line; line <= span.End.Line && line < rows.EndLine; line++)
        {
            var row = rows.Row(line);
            int from = line == span.Start.Line ? span.Start.Col : 0;
            int to;
            if (line == span.End.Line) to = span.End.Col;
            else to = row.Wrapped ? row.Width : row.Column(row.TextEnd);
            if (to > from) spans.Add(new RowSpan(line, from, to));
        }
        return spans;
    }

    /// <summary>
    /// The first (or with <paramref name="backward"/> the last) occurrence of <paramref name="text"/> inside the
    /// range, or null. Matches are found in the range's text, so a match can run across a soft wrap but not across a
    /// line end.
    /// </summary>
    public static TextSpan? Find(IAccessibleRows rows, TextSpan span, string text, bool backward, bool ignoreCase)
    {
        if (text.Length == 0) return null;
        span = Normalize(rows, span);
        var map = new List<TextPoint>();
        var sb = new StringBuilder();
        BuildMap(rows, span, sb, map);
        string haystack = sb.ToString();
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int i = backward ? haystack.LastIndexOf(text, comparison) : haystack.IndexOf(text, comparison);
        if (i < 0) return null;
        int last = i + text.Length;
        var end = last < map.Count ? map[last] : span.End;
        return new TextSpan(map[i], end);
    }

    /// <summary>The range's text with, for every UTF-16 unit, the point of the character it belongs to.</summary>
    private static void BuildMap(IAccessibleRows rows, TextSpan span, StringBuilder sb, List<TextPoint> map)
    {
        for (long line = span.Start.Line; line <= span.End.Line && line < rows.EndLine; line++)
        {
            var row = rows.Row(line);
            var (from, to, lineBreak) = CharRange(row, line, span);
            for (int c = from; c < to; c++)
            {
                var ch = row.CharText(c);
                sb.Append(ch);
                for (int k = 0; k < ch.Length; k++) map.Add(new TextPoint(line, row.Column(c)));
            }
            if (!lineBreak) continue;
            sb.Append('\n');
            map.Add(new TextPoint(line, row.Column(to)));
        }
    }

    /// <summary>
    /// The characters of <paramref name="row"/> (line <paramref name="line"/>) that <paramref name="span"/> covers, and
    /// whether a line break follows them: the line ends inside the span, and then loses its trailing blanks, unless it
    /// is soft-wrapped and runs on into the next row.
    /// </summary>
    private static (int From, int To, bool LineBreak) CharRange(AccessibleRow row, long line, TextSpan span)
    {
        int from = line == span.Start.Line ? row.CharAt(span.Start.Col) : 0;
        if (line == span.End.Line) return (from, row.CharAt(span.End.Col), false);
        if (row.Wrapped) return (from, row.Count, false);
        return (from, Math.Max(from, Math.Min(row.Count, row.TextEnd)), true);
    }

    // ---- Characters and words ------------------------------------------------------------------

    private static TextPoint NextChar(IAccessibleRows rows, TextPoint p)
    {
        var row = rows.Row(p.Line);
        int i = row.CharAt(p.Col) + 1;
        if (i < row.Count) return p with { Col = row.Column(i) };
        return TextPoint.Min(new TextPoint(p.Line + 1, 0), DocumentEnd(rows));
    }

    private static TextPoint PreviousChar(IAccessibleRows rows, TextPoint p)
    {
        if (p.Col > 0)
        {
            var row = rows.Row(p.Line);
            return p with { Col = row.Column(Math.Max(0, row.CharAt(p.Col) - 1)) };
        }
        var above = rows.Row(p.Line - 1);
        return new TextPoint(p.Line - 1, above.Count > 0 ? above.Column(above.Count - 1) : 0);
    }

    /// <summary>0: blank, 1: letters, digits and the like, 2: punctuation.</summary>
    private static int Class(AccessibleRow row, int index)
    {
        if (index >= row.Count || row.IsBlank(index)) return 0;
        var text = row.CharText(index);
        char c = text[0];
        if (c > 0x7F || char.IsLetterOrDigit(c) || c == '_') return 1;
        return char.IsWhiteSpace(c) ? 0 : 2;
    }

    private static bool IsWordStart(IAccessibleRows rows, TextPoint p)
    {
        var row = rows.Row(p.Line);
        int i = row.CharAt(p.Col);
        if (i == 0)
        {
            var above = rows.Row(p.Line - 1);
            if (p.Line <= rows.FirstLine || !above.Wrapped) return true; // every line starts a word
            int c0 = Class(row, 0);
            return c0 != 0 && c0 != Class(above, above.Count - 1);
        }
        int c = Class(row, i);
        return c != 0 && c != Class(row, i - 1);
    }

    // ---- Paragraphs ----------------------------------------------------------------------------

    private static TextPoint NextParagraph(IAccessibleRows rows, TextPoint p, TextPoint end)
    {
        long line = p.Line;
        for (int n = 0; n < MaxParagraphRows && line + 1 < end.Line && rows.Row(line).Wrapped; n++) line++;
        return TextPoint.Min(new TextPoint(line + 1, 0), end);
    }

    private static TextPoint PreviousParagraph(IAccessibleRows rows, TextPoint p, TextPoint start)
    {
        long line = p.Col > 0 ? p.Line : p.Line - 1;
        for (int n = 0; n < MaxParagraphRows && line > start.Line && rows.Row(line - 1).Wrapped; n++) line--;
        return TextPoint.Max(new TextPoint(line, 0), start);
    }
}
