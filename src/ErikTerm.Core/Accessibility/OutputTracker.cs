using System.Text;

namespace ErikTerm.Core.Accessibility;

/// <summary>What changed on a terminal since the last look, as text to announce.</summary>
/// <param name="Text">New text, lines separated by '\n'.</param>
/// <param name="AlternateScreen">It happened on the alternate screen (a full-screen program).</param>
/// <param name="RewriteOnly">Every line in it is a line that was rewritten in place (a progress bar, a status line), none is new.</param>
public readonly record struct OutputChange(string Text, bool AlternateScreen, bool RewriteOnly);

/// <summary>
/// Works out what a terminal has written since it was last asked, for screen readers to announce: lines that are
/// new, text added to a line, and lines rewritten in place. Lines are compared by absolute line number, so output
/// that scrolled the screen is still found, up to <see cref="MaxLines"/> lines back from the cursor (a flood only
/// needs its end). Deleting text (Backspace) announces nothing. Rows are only decoded when their version changed.
/// Reflow, a resize and a switch between the screens start over silently. Call under the terminal's lock.
/// </summary>
public sealed class OutputTracker
{
    /// <summary>Lines looked at, counting back from the cursor.</summary>
    public const int MaxLines = 60;

    private long _top = -1;
    private string[] _texts = [];
    private Row?[] _rows = [];
    private int[] _versions = [];
    private bool _alt;
    private int _reflow = -1, _columns = -1;
    private long _version = -1;

    /// <summary>Forgets what was seen: the next <see cref="Update"/> only takes a baseline.</summary>
    public void Reset() => _top = -1;

    /// <summary>The change since the last call, or null when there is nothing to say (or this call took the baseline).</summary>
    public OutputChange? Update(Terminal t)
    {
        if (_top < 0 || t.IsAlternateScreen != _alt || t.ReflowCount != _reflow || t.Columns != _columns)
        {
            Capture(t);
            return null;
        }
        if (t.ContentVersion == _version) return null;
        long cursorLine = t.ScreenTopLine + t.CursorY;
        long from = Math.Max(Math.Max(_top, t.FirstLine), cursorLine - MaxLines + 1);
        string changes = Changes(t, from, cursorLine, out bool rewriteOnly);
        Capture(t);
        return changes.Length == 0 ? null : new OutputChange(changes, t.IsAlternateScreen, rewriteOnly);
    }

    /// <summary>
    /// What changed on the lines <paramref name="from"/> to <paramref name="to"/> since the last look: a '\n' between
    /// lines, none where a line wraps onto the next. <paramref name="rewriteOnly"/>: every piece was a rewritten line.
    /// </summary>
    private string Changes(Terminal t, long from, long to, out bool rewriteOnly)
    {
        var sb = new StringBuilder();
        rewriteOnly = true;
        bool joinNext = false;
        for (long line = from; line <= to; line++)
        {
            var row = t.GetLine(line);
            if (row is null) continue;
            string? old = Seen(line);
            string text = old is not null && IsSameRow(line, row) ? old : RowText(row, t.Columns);
            var (piece, rewrite) = Changed(old, text);
            if (piece.Length > 0)
            {
                if (sb.Length > 0 && !joinNext) sb.Append('\n');
                sb.Append(piece);
                rewriteOnly &= rewrite;
            }
            joinNext = piece.Length > 0 && row.Wrapped;
        }
        return sb.ToString();
    }

    /// <summary>
    /// What is new in a line that read <paramref name="old"/> (null: it wasn't there) and now reads
    /// <paramref name="now"/>: all of a new line, what was inserted (typing, appending), nothing for a deletion, and
    /// all of a line rewritten otherwise.
    /// </summary>
    public static (string Text, bool Rewrite) Changed(string? old, string now)
    {
        if (old is null) return (now, false);
        if (old == now) return ("", false);
        int prefix = 0, max = Math.Min(old.Length, now.Length);
        while (prefix < max && old[prefix] == now[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && old[^(suffix + 1)] == now[^(suffix + 1)]) suffix++;
        if (prefix + suffix == old.Length) return (now[prefix..^suffix], false); // inserted (or appended)
        if (prefix + suffix == now.Length) return ("", false); // deleted
        return (now, true);
    }

    private string? Seen(long line)
    {
        long i = line - _top;
        return i >= 0 && i < _texts.Length ? _texts[i] : null;
    }

    private bool IsSameRow(long line, Row row)
    {
        long i = line - _top;
        return ReferenceEquals(_rows[i], row) && _versions[i] == row.Version;
    }

    private void Capture(Terminal t)
    {
        int n = t.Rows;
        var texts = new string[n];
        var rows = new Row?[n];
        var versions = new int[n];
        long top = t.ScreenTopLine;
        for (int y = 0; y < n; y++)
        {
            var row = t.GetScreenRow(y);
            long line = top + y;
            bool same = _top >= 0 && line - _top >= 0 && line - _top < _texts.Length && IsSameRow(line, row);
            texts[y] = same ? _texts[line - _top] : RowText(row, t.Columns);
            rows[y] = row;
            versions[y] = row.Version;
        }
        (_top, _texts, _rows, _versions) = (top, texts, rows, versions);
        (_alt, _reflow, _columns, _version) = (t.IsAlternateScreen, t.ReflowCount, t.Columns, t.ContentVersion);
    }

    private static string RowText(Row row, int columns)
    {
        var decoded = AccessibleRow.From(row, columns);
        return decoded.Text(0, row.Wrapped ? decoded.Count : decoded.TextEnd).ToString();
    }
}
