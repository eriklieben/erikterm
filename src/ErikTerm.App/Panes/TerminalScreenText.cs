using System.Text;
using ErikTerm.Core;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Panes;

/// <summary>
/// The live screen of a terminal as text rows for the agent screen rules. Rows are decoded lazily into one reused
/// buffer each, so a poll allocates nothing once the buffers have grown to the screen's size. Call under the
/// terminal's lock; reuse one instance for every pane (main thread only).
/// </summary>
internal sealed class TerminalScreenText : IScreenText
{
    private Terminal? _term;
    private char[][] _rows = [];
    private int[] _lengths = [];
    private int[] _decodedVersion = [];
    private Row?[] _decodedRow = [];

    public void Attach(Terminal term)
    {
        _term = term;
        int n = term.Rows;
        if (_rows.Length < n)
        {
            Array.Resize(ref _rows, n);
            Array.Resize(ref _lengths, n);
            Array.Resize(ref _decodedVersion, n);
            Array.Resize(ref _decodedRow, n);
        }
        Array.Clear(_decodedRow); // another terminal's rows: decode afresh
    }

    public int RowCount => _term?.Rows ?? 0;

    public ReadOnlySpan<char> Row(int y)
    {
        var row = _term!.GetScreenRow(y);
        if (!ReferenceEquals(_decodedRow[y], row) || _decodedVersion[y] != row.Version)
        {
            Decode(y, row);
            _decodedRow[y] = row;
            _decodedVersion[y] = row.Version;
        }
        return _rows[y].AsSpan(0, _lengths[y]);
    }

    private void Decode(int y, Row row)
    {
        var cells = row.Cells;
        int need = cells.Length * 2 + 8;
        if (_rows[y] is null || _rows[y].Length < need) _rows[y] = new char[need];
        var buf = _rows[y];
        int n = 0, end = 0;
        for (int x = 0; x < cells.Length; x++)
        {
            ref readonly var c = ref cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0) continue;
            if (!AppendCell(row, x, in c, buf, ref n)) break;
            if (buf[n - 1] != ' ') end = n;
            if (n + 2 > buf.Length) break;
        }
        _lengths[y] = end;
    }

    /// <summary>False when the cell's cluster doesn't fit in what is left of the row's buffer.</summary>
    private static bool AppendCell(Row row, int x, in Cell c, char[] buf, ref int n)
    {
        if ((c.Flags & CellAttributes.Cluster) != 0 && row.TryGetCluster(x, out var cluster))
        {
            if (n + cluster.Length > buf.Length) return false;
            cluster.CopyTo(buf.AsSpan(n));
            n += cluster.Length;
        }
        else if (c.Rune <= 0x20) buf[n++] = ' ';
        else if (Rune.TryCreate(c.Rune, out var r) && r.TryEncodeToUtf16(buf.AsSpan(n), out int w)) n += w;
        else buf[n++] = '?';
        return true;
    }
}
