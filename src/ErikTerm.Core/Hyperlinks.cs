using System.Text;

namespace ErikTerm.Core;

/// <summary>An OSC 8 hyperlink target. <see cref="Key"/> is the optional <c>id=</c> parameter.</summary>
public sealed record Hyperlink(int Id, string Uri, string? Key);

/// <summary>What <see cref="Hyperlinks"/> needs from the terminal: every row that can hold link spans.</summary>
internal interface ILinkedRows
{
    /// <summary>The scrollback, the main screen and the alternate screen.</summary>
    IEnumerable<Row?> AllRows();
}

/// <summary>
/// The OSC 8 link table: one id per distinct (id=, URI), which rows store in their <see cref="Row.Links"/> spans. The
/// link printed characters belong to is the terminal's (it is part of the pen, checked on every printed run).
/// </summary>
internal sealed class Hyperlinks(ILinkedRows rows)
{
    private const int MaxLinkTable = 4096;
    private const int MaxUriBytes = 8192;
    private readonly Dictionary<int, Hyperlink> _byId = [];
    private readonly Dictionary<(string? Key, string Uri), int> _ids = [];
    private int _nextId = 1;

    public Hyperlink? Get(int id) => id != 0 && _byId.TryGetValue(id, out var h) ? h : null;

    /// <summary>
    /// OSC 8 ; params ; URI: the id printed characters belong to from now on (0 = none: an empty URI ends the link),
    /// or <paramref name="current"/> for a malformed sequence. A full table first drops the links no row refers to.
    /// </summary>
    public int Open(ReadOnlySpan<byte> arg, int current)
    {
        int semi = arg.IndexOf((byte)';');
        if (semi < 0) return current;
        var parameters = arg[..semi];
        var uri = arg[(semi + 1)..];
        if (uri.IsEmpty || uri.Length > MaxUriBytes) return 0;

        string? key = null;
        foreach (var range in parameters.Split((byte)':'))
        {
            var p = parameters[range];
            if (p.StartsWith("id="u8) && p.Length > 3) key = Encoding.UTF8.GetString(p[3..]);
        }
        string text = Encoding.UTF8.GetString(uri);
        if (_ids.TryGetValue((key, text), out int id)) return id;

        if (_byId.Count >= MaxLinkTable) Prune(current);
        id = _nextId++;
        if (_nextId == int.MaxValue) _nextId = 1;
        _byId[id] = new Hyperlink(id, text, key);
        _ids[(key, text)] = id;
        return id;
    }

    /// <summary>Forgets links no row refers to any more (scrolled out of the history, overwritten).</summary>
    private void Prune(int current)
    {
        var used = new HashSet<int> { current };
        foreach (var row in rows.AllRows())
            if (row?.Links is { } links) foreach (var s in links) used.Add(s.Id);

        foreach (var id in _byId.Keys.ToList())
        {
            if (used.Contains(id)) continue;
            Remove(id);
        }
        // Still full (a screen full of distinct links): drop the oldest half; their cells just stop being links.
        if (_byId.Count >= MaxLinkTable)
            foreach (var id in _byId.Keys.Order().Take(MaxLinkTable / 2).ToList())
                Remove(id);
    }

    private void Remove(int id)
    {
        var h = _byId[id];
        _byId.Remove(id);
        _ids.Remove((h.Key, h.Uri));
    }
}
