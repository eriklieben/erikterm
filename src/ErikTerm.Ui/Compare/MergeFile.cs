using System.Text;

namespace ErikTerm.Ui.Compare;

/// <summary>A person's choice for one conflict.</summary>
public enum ConflictChoice { None, Ours, Theirs, Both, Custom }

/// <summary>A choice plus the text when it was edited by hand.</summary>
public readonly record struct ChunkDecision(ConflictChoice Choice, string[]? Custom = null);

/// <summary>Where a line of the result comes from (decides its color).</summary>
public enum OutputOrigin { Stable, AutoOurs, AutoTheirs, AutoBoth, Ours, Theirs, Custom, Unresolved }

/// <summary>One line of the result; <see cref="Raw"/> keeps its terminator.</summary>
public readonly record struct OutputLine(string Raw, OutputOrigin Origin, int Chunk);

/// <summary>One aligned row of the two columns; a null side is a gap, numbers are 1-based.</summary>
public readonly record struct MergeRow(string? Ours, string? Theirs, int OursNo, int TheirsNo, int Chunk);

/// <summary>What kind of decision a file needs.</summary>
public enum MergeFileKind { Text, Binary, DeleteModify }

/// <summary>The choice for a file that isn't merged line by line (binary, delete/modify).</summary>
public enum FileChoice { None, Ours, Theirs, Delete }

/// <summary>Which side's version a delete/modify conflict still has.</summary>
public enum DeletedSide { Ours, Theirs }

/// <summary>
/// One file of a merge: the chunks from <see cref="ThreeWayMerge"/>, the decisions on them, the aligned columns and
/// the result. Mutators go through a snapshot so <see cref="Undo"/> can step back; columns never change, only the result.
/// </summary>
public sealed class MergeFile
{
    private ChunkDecision[] _decisions;
    private string[]? _wholeEdit;
    private FileChoice _fileChoice;
    private readonly List<Snapshot> _undo = [];
    private int _savedDepth;
    private OutputLine[]? _output;
    private readonly Dictionary<int, LineChanges> _intraline = [];
    private int[]? _firstOutputLineOfChunk;

    private readonly record struct Snapshot(ChunkDecision[] Decisions, string[]? WholeEdit, FileChoice FileChoice);

    private MergeFile(string path, string oursLabel, string theirsLabel, TextCodec codec, IReadOnlyList<MergeChunk> chunks, MergeFileKind kind)
    {
        Path = path;
        OursLabel = oursLabel;
        TheirsLabel = theirsLabel;
        Codec = codec;
        Chunks = chunks;
        Kind = kind;
        _decisions = new ChunkDecision[chunks.Count];
        var conflicts = new List<int>();
        for (int i = 0; i < chunks.Count; i++) if (chunks[i].Kind == MergeChunkKind.Conflict) conflicts.Add(i);
        Conflicts = conflicts;
        Rows = BuildRows(chunks);
    }

    public string Path { get; }
    public string OursLabel { get; }
    public string TheirsLabel { get; }
    public TextCodec Codec { get; }
    public MergeFileKind Kind { get; }
    public IReadOnlyList<MergeChunk> Chunks { get; }
    public IReadOnlyList<int> Conflicts { get; }
    public MergeRow[] Rows { get; }

    /// <summary>For Binary: each side's bytes. For DeleteModify: the surviving side's bytes.</summary>
    public byte[]? OursBytes { get; private init; }
    public byte[]? TheirsBytes { get; private init; }
    public DeletedSide DeletedSide { get; private init; }

    public static MergeFile FromText(string path, string oursLabel, string theirsLabel, TextDocument baseDoc, TextDocument ours, TextDocument theirs, bool baseKnown)
    {
        var chunks = ThreeWayMerge.Merge(baseDoc.RawLines, ours.RawLines, theirs.RawLines, baseKnown);
        return new MergeFile(path, oursLabel, theirsLabel, ours.Codec, chunks, MergeFileKind.Text);
    }

    public static MergeFile Binary(string path, string oursLabel, string theirsLabel, byte[] ours, byte[] theirs) =>
        new(path, oursLabel, theirsLabel, TextCodec.Utf8, [], MergeFileKind.Binary) { OursBytes = ours, TheirsBytes = theirs };

    public static MergeFile DeleteModify(string path, string oursLabel, string theirsLabel, DeletedSide deleted, byte[] surviving) =>
        new(path, oursLabel, theirsLabel, TextCodec.Utf8, [], MergeFileKind.DeleteModify)
        {
            DeletedSide = deleted,
            OursBytes = deleted == DeletedSide.Theirs ? surviving : null,
            TheirsBytes = deleted == DeletedSide.Ours ? surviving : null,
        };

    /// <summary>The surviving side's text in a delete/modify conflict, for showing it.</summary>
    public string SurvivingText()
    {
        var bytes = (DeletedSide == DeletedSide.Ours ? TheirsBytes : OursBytes) ?? [];
        var doc = TextDocument.Decode(bytes);
        return doc.Problem == DocumentProblem.None ? string.Concat(doc.RawLines) : "(binary content)";
    }

    public bool WholeEdited => _wholeEdit is not null;
    public FileChoice FileChoice => _fileChoice;
    public bool Dirty => _undo.Count != _savedDepth;

    /// <summary>Conflicts still needing a decision (0 when edited whole without markers left).</summary>
    public int UnresolvedCount
    {
        get
        {
            if (Kind != MergeFileKind.Text) return _fileChoice == FileChoice.None ? 1 : 0;
            if (_wholeEdit is { } edited) return MarkerGroups(edited);
            int n = 0;
            foreach (int i in Conflicts) if (_decisions[i].Choice == ConflictChoice.None) n++;
            return n;
        }
    }

    public int AutoCount => Chunks.Count(c => c.Kind is MergeChunkKind.AutoOurs or MergeChunkKind.AutoTheirs or MergeChunkKind.AutoBoth);

    public bool IsConflictFree => Kind == MergeFileKind.Text && Conflicts.Count == 0;

    public ChunkDecision DecisionOf(int chunk) => _decisions[chunk];

    private void Push() => _undo.Add(new Snapshot((ChunkDecision[])_decisions.Clone(), _wholeEdit, _fileChoice));

    private void Changed()
    {
        _output = null;
        _firstOutputLineOfChunk = null;
    }

    public void Decide(int chunk, ConflictChoice choice, string[]? custom = null)
    {
        if (Chunks[chunk].Kind != MergeChunkKind.Conflict || _wholeEdit is not null) return;
        var next = new ChunkDecision(choice, choice == ConflictChoice.Custom ? custom : null);
        if (_decisions[chunk] == next) return;
        Push();
        _decisions[chunk] = next;
        Changed();
    }

    /// <summary>Replaces the whole result with hand-edited text (conflict markers it still has count as unresolved).</summary>
    public void ApplyWholeEdit(string text)
    {
        var lines = TextDocument.SplitLines(text);
        Push();
        _wholeEdit = lines;
        Changed();
    }

    public void Choose(FileChoice choice)
    {
        if (_fileChoice == choice) return;
        Push();
        _fileChoice = choice;
        Changed();
    }

    /// <summary>Steps back one decision. Returns false when there's nothing to undo.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _decisions = last.Decisions;
        _wholeEdit = last.WholeEdit;
        _fileChoice = last.FileChoice;
        Changed();
        return true;
    }

    public void MarkSaved() => _savedDepth = _undo.Count;

    /// <summary>The result lines; unresolved conflicts appear as one placeholder (never written).</summary>
    public IReadOnlyList<OutputLine> Output => _output ??= BuildOutput();

    private OutputLine[] BuildOutput()
    {
        var lines = new List<OutputLine>();
        if (_wholeEdit is { } edited)
        {
            AddLines(lines, edited, OutputOrigin.Custom, -1);
            return [.. lines];
        }
        for (int i = 0; i < Chunks.Count; i++) AddChunkOutput(lines, i);
        return [.. lines];
    }

    private void AddChunkOutput(List<OutputLine> lines, int index)
    {
        var chunk = Chunks[index];
        switch (chunk.Kind)
        {
            case MergeChunkKind.Stable: AddLines(lines, chunk.Ours, OutputOrigin.Stable, index); break;
            case MergeChunkKind.AutoOurs: AddLines(lines, chunk.Ours, OutputOrigin.AutoOurs, index); break;
            case MergeChunkKind.AutoTheirs: AddLines(lines, chunk.Theirs, OutputOrigin.AutoTheirs, index); break;
            case MergeChunkKind.AutoBoth: AddLines(lines, chunk.Ours, OutputOrigin.AutoBoth, index); break;
            case MergeChunkKind.Conflict: AddConflictOutput(lines, chunk, index); break;
        }
    }

    private void AddConflictOutput(List<OutputLine> lines, MergeChunk chunk, int index)
    {
        var d = _decisions[index];
        switch (d.Choice)
        {
            case ConflictChoice.Ours: AddLines(lines, chunk.Ours, OutputOrigin.Ours, index); break;
            case ConflictChoice.Theirs: AddLines(lines, chunk.Theirs, OutputOrigin.Theirs, index); break;
            case ConflictChoice.Both:
                AddLines(lines, chunk.Ours, OutputOrigin.Ours, index);
                AddLines(lines, chunk.Theirs, OutputOrigin.Theirs, index);
                break;
            case ConflictChoice.Custom: AddLines(lines, d.Custom ?? [], OutputOrigin.Custom, index); break;
            default: lines.Add(new OutputLine("unresolved: press o, t, b or e", OutputOrigin.Unresolved, index)); break;
        }
    }

    private static void AddLines(List<OutputLine> lines, string[] text, OutputOrigin origin, int chunk)
    {
        foreach (var l in text) lines.Add(new OutputLine(l, origin, chunk));
    }

    /// <summary>The output line where a chunk starts, or the first after it when it has none.</summary>
    public int FirstOutputLine(int chunk)
    {
        if (_firstOutputLineOfChunk is null)
        {
            var first = new int[Chunks.Count];
            Array.Fill(first, -1);
            var output = Output;
            for (int i = output.Count - 1; i >= 0; i--) if (output[i].Chunk >= 0) first[output[i].Chunk] = i;
            int next = output.Count;
            for (int c = Chunks.Count - 1; c >= 0; c--) { if (first[c] < 0) first[c] = next; else next = first[c]; }
            _firstOutputLineOfChunk = first;
        }
        return chunk < _firstOutputLineOfChunk.Length ? _firstOutputLineOfChunk[chunk] : 0;
    }

    /// <summary>The first row belonging to a chunk.</summary>
    public int FirstRow(int chunk)
    {
        for (int i = 0; i < Rows.Length; i++) if (Rows[i].Chunk >= chunk) return i;
        return Math.Max(0, Rows.Length - 1);
    }

    /// <summary>Word-level changes on a row, cached (computed only for rows on screen).</summary>
    public LineChanges Changes(int rowIndex)
    {
        if (_intraline.TryGetValue(rowIndex, out var cached)) return cached;
        var row = Rows[rowIndex];
        if (row.Ours is null || row.Theirs is null || Chunks[row.Chunk].Kind == MergeChunkKind.Stable) return LineChanges.None;
        var changes = Intraline.Compute(TextDocument.ContentString(row.Ours), TextDocument.ContentString(row.Theirs));
        if (_intraline.Count > 2048) _intraline.Clear();
        _intraline[rowIndex] = changes;
        return changes;
    }

    private static MergeRow[] BuildRows(IReadOnlyList<MergeChunk> chunks)
    {
        var rows = new List<MergeRow>();
        int oursNo = 1, theirsNo = 1, budget = LineAligner.NewBudget();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (c.Kind is MergeChunkKind.Stable or MergeChunkKind.AutoBoth)
            {
                foreach (var l in c.Ours) rows.Add(new MergeRow(l, l, oursNo++, theirsNo++, i));
                continue;
            }
            foreach (var (a, b) in LineAligner.Align(c.Ours, c.Theirs, ref budget))
            {
                string? ours = a >= 0 ? c.Ours[a] : null;
                string? theirs = b >= 0 ? c.Theirs[b] : null;
                rows.Add(new MergeRow(ours, theirs, ours is null ? 0 : oursNo++, theirs is null ? 0 : theirsNo++, i));
            }
        }
        return [.. rows];
    }

    /// <summary>The bytes to save. Text with unresolved conflicts only with <paramref name="withMarkers"/>; null for a chosen deletion.</summary>
    public byte[]? Serialize(bool withMarkers)
    {
        if (Kind != MergeFileKind.Text)
        {
            return _fileChoice switch
            {
                FileChoice.Delete => null,
                FileChoice.Theirs => TheirsBytes,
                _ => OursBytes,
            };
        }
        if (_wholeEdit is { } edited) return TextDocument.Encode(string.Concat(edited), Codec);
        var sb = new StringBuilder();
        for (int i = 0; i < Chunks.Count; i++) AppendChunk(sb, i, withMarkers);
        return TextDocument.Encode(sb.ToString(), Codec);
    }

    private void AppendChunk(StringBuilder sb, int index, bool withMarkers)
    {
        var chunk = Chunks[index];
        switch (chunk.Kind)
        {
            case MergeChunkKind.Stable or MergeChunkKind.AutoOurs or MergeChunkKind.AutoBoth: AppendAll(sb, chunk.Ours); break;
            case MergeChunkKind.AutoTheirs: AppendAll(sb, chunk.Theirs); break;
            default: AppendConflict(sb, chunk, _decisions[index], withMarkers); break;
        }
    }

    private void AppendConflict(StringBuilder sb, MergeChunk chunk, ChunkDecision d, bool withMarkers)
    {
        switch (d.Choice)
        {
            case ConflictChoice.Ours: AppendAll(sb, chunk.Ours); break;
            case ConflictChoice.Theirs: AppendAll(sb, chunk.Theirs); break;
            case ConflictChoice.Both: AppendAll(sb, chunk.Ours); AppendAll(sb, chunk.Theirs); break;
            case ConflictChoice.Custom: AppendAll(sb, d.Custom ?? []); break;
            default:
                if (withMarkers) AppendMarkers(sb, chunk);
                break;
        }
    }

    private static void AppendAll(StringBuilder sb, string[] lines)
    {
        foreach (var l in lines) sb.Append(l);
    }

    private void AppendMarkers(StringBuilder sb, MergeChunk chunk)
    {
        string sample = "\n";
        if (chunk.Ours.Length > 0) sample = chunk.Ours[0];
        else if (chunk.Theirs.Length > 0) sample = chunk.Theirs[0];
        string eol = sample.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        sb.Append("<<<<<<< ").Append(OursLabel).Append(eol);
        foreach (var l in chunk.Ours) AppendLine(sb, l, eol);
        if (chunk.BaseKnown)
        {
            sb.Append("||||||| base").Append(eol);
            foreach (var l in chunk.Base) AppendLine(sb, l, eol);
        }
        sb.Append("=======").Append(eol);
        foreach (var l in chunk.Theirs) AppendLine(sb, l, eol);
        sb.Append(">>>>>>> ").Append(TheirsLabel).Append(eol);
    }

    private static void AppendLine(StringBuilder sb, string line, string eol)
    {
        sb.Append(line);
        if (!line.EndsWith('\n')) sb.Append(eol); // a side's unterminated last line can't run into the marker
    }

    /// <summary>The result as text with markers on the unresolved conflicts (seeding the editor).</summary>
    public string CurrentText()
    {
        if (_wholeEdit is { } edited) return string.Concat(edited);
        return DecodeForEdit(Serialize(withMarkers: true) ?? [], Codec);
    }

    private static string DecodeForEdit(byte[] bytes, TextCodec codec) => codec switch
    {
        TextCodec.Latin1 => Encoding.Latin1.GetString(bytes),
        TextCodec.Utf8Bom => Encoding.UTF8.GetString(bytes.AsSpan(3)),
        TextCodec.Utf16Le => Encoding.Unicode.GetString(bytes.AsSpan(2)),
        TextCodec.Utf16Be => Encoding.BigEndianUnicode.GetString(bytes.AsSpan(2)),
        _ => Encoding.UTF8.GetString(bytes),
    };

    /// <summary>The text of a conflict's current content for the editor: the decision's text, or the sides with markers.</summary>
    public string HunkText(int chunk)
    {
        var c = Chunks[chunk];
        var d = _decisions[chunk];
        return d.Choice switch
        {
            ConflictChoice.Ours => string.Concat(c.Ours),
            ConflictChoice.Theirs => string.Concat(c.Theirs),
            ConflictChoice.Both => string.Concat(c.Ours) + string.Concat(c.Theirs),
            ConflictChoice.Custom => string.Concat(d.Custom ?? []),
            _ => MarkedHunk(c),
        };
    }

    private string MarkedHunk(MergeChunk chunk)
    {
        var sb = new StringBuilder();
        AppendMarkers(sb, chunk);
        return sb.ToString();
    }

    /// <summary>
    /// Applies text edited for a conflict. Returns false (nothing changes) when it's identical to what was there or
    /// still carries conflict markers, which would be saved as if resolved.
    /// </summary>
    public bool ApplyHunkEdit(int chunk, string text)
    {
        if (text == HunkText(chunk)) return false;
        var lines = TextDocument.SplitLines(text);
        if (MarkerGroups(lines) > 0) return false;
        Decide(chunk, ConflictChoice.Custom, lines);
        return true;
    }

    /// <summary>Conflict marker groups (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; </c> lines) in hand-edited text.</summary>
    public static int MarkerGroups(IEnumerable<string> lines)
    {
        int n = 0;
        foreach (string line in lines) if (line.StartsWith("<<<<<<<", StringComparison.Ordinal) && (line.Length == 7 || line[7] is ' ' or '\r' or '\n')) n++;
        return n;
    }
}
