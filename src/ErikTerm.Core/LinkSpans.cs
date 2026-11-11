namespace ErikTerm.Core;

/// <summary>FinalTerm / OSC 133 semantic prompt marks recorded on the row where the shell emitted them.</summary>
[Flags]
public enum SemanticMarks : byte
{
    None = 0,
    /// <summary>OSC 133;A — the prompt starts on this row.</summary>
    Prompt = 1 << 0,
    /// <summary>OSC 133;B — the user's command starts on this row (at <see cref="Row.CommandColumn"/>).</summary>
    Input = 1 << 1,
    /// <summary>OSC 133;C — command output starts on this row.</summary>
    Output = 1 << 2,
    /// <summary>OSC 133;D — a command finished here.</summary>
    End = 1 << 3,
}

/// <summary>A run of cells [Start, End) that belongs to OSC 8 hyperlink <see cref="Id"/> (see <see cref="Terminal.GetHyperlink"/>).</summary>
public struct LinkSpan
{
    public int Start { get; set; }
    public int End { get; set; }
    public int Id { get; set; }
    public LinkSpan(int start, int end, int id) { Start = start; End = end; Id = id; }
}

/// <summary>Edits of a row's OSC 8 spans (<see cref="Row.Links"/>): sorted, non-overlapping, touching spans of one link merged.</summary>
internal static class LinkSpans
{
    /// <summary>Hyperlink id at a column, 0 if none.</summary>
    public static int IdAt(List<LinkSpan> links, int col)
    {
        foreach (var s in links)
        {
            if (col < s.Start) return 0;
            if (col < s.End) return s.Id;
        }
        return 0;
    }

    /// <summary>Marks [start, end) as link <paramref name="id"/>; id 0 removes links from the range.</summary>
    public static void Set(List<LinkSpan> links, int start, int end, int id)
    {
        if (id != 0 && TryExtendLast(links, start, end, id)) return;
        int insertAt = Cut(links, start, end);
        if (id != 0) Insert(links, Math.Min(insertAt, links.Count), start, end, id);
    }

    /// <summary>Fast path: printing a link left to right extends its last span, or starts a new one after it.</summary>
    private static bool TryExtendLast(List<LinkSpan> links, int start, int end, int id)
    {
        if (links.Count == 0) { links.Add(new LinkSpan(start, end, id)); return true; }
        var last = links[^1];
        if (last.Id == id && last.End == start) { last.End = end; links[^1] = last; return true; }
        if (last.End <= start) { links.Add(new LinkSpan(start, end, id)); return true; }
        return false;
    }

    /// <summary>Cuts [start, end) out of every span; returns where a span for that range belongs.</summary>
    private static int Cut(List<LinkSpan> links, int start, int end)
    {
        int insertAt = links.Count;
        int i = 0;
        while (i < links.Count)
        {
            var s = links[i];
            if (s.End <= start) { i++; continue; }
            if (s.Start >= end) return Math.Min(insertAt, i);
            if (s.Start < start && s.End > end)
            {
                // Split around the hole.
                links[i] = new LinkSpan(s.Start, start, s.Id);
                links.Insert(i + 1, new LinkSpan(end, s.End, s.Id));
                return i + 1;
            }
            if (s.Start < start) { links[i] = new LinkSpan(s.Start, start, s.Id); i++; continue; }
            if (s.End > end) { links[i] = new LinkSpan(end, s.End, s.Id); return Math.Min(insertAt, i); }
            links.RemoveAt(i);
            insertAt = Math.Min(insertAt, i);
        }
        return insertAt;
    }

    /// <summary>Inserts a span at <paramref name="insertAt"/>, merging it with a touching neighbour of the same link.</summary>
    private static void Insert(List<LinkSpan> links, int insertAt, int start, int end, int id)
    {
        bool touchesNext = insertAt < links.Count && links[insertAt].Id == id && links[insertAt].Start == end;
        if (insertAt > 0 && links[insertAt - 1].Id == id && links[insertAt - 1].End == start)
        {
            var prev = links[insertAt - 1];
            prev.End = end;
            if (touchesNext)
            {
                prev.End = links[insertAt].End;
                links.RemoveAt(insertAt);
            }
            links[insertAt - 1] = prev;
            return;
        }
        if (touchesNext)
        {
            var next = links[insertAt];
            next.Start = start;
            links[insertAt] = next;
            return;
        }
        links.Insert(insertAt, new LinkSpan(start, end, id));
    }
}
