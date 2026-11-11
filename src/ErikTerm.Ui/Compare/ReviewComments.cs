using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Ui.Compare;

/// <summary>Which side of the diff a comment is on: <see cref="Old"/> for a line only the base has.</summary>
public enum ReviewCommentSide { New, Old }

/// <summary>
/// A review comment on lines of a file. <see cref="StartLine"/> and <see cref="EndLine"/> are 1-based and were right when
/// it was written; <see cref="Quoted"/> holds those lines, so the comment can follow them when the file changes.
/// </summary>
/// <param name="Commit">The commit it was written on, null when it's on the branch as a whole.</param>
public sealed record ReviewComment(long Id, string Path, ReviewCommentSide Side, int StartLine, int EndLine, string[] Quoted, string Body, string? Commit);

/// <summary>A coding agent's pane a review can send to.</summary>
/// <param name="Socket">The control socket of the erikterm the pane is in.</param>
/// <param name="Agent">Its display name.</param>
/// <param name="Place">Where it is, in words.</param>
/// <param name="State">waiting / working / done / idle.</param>
public sealed record ReviewAgentTarget(string Socket, int PaneId, string Agent, string Place, string State);

/// <summary>The agents running in a worktree; without any, <see cref="Problem"/> may say why none could be asked for.</summary>
public sealed record ReviewAgentSearch(IReadOnlyList<ReviewAgentTarget> Targets, string? Problem = null);

/// <summary>Where review comments live between runs and where they go. Find and Send run on a background thread, Load and Save are short and don't.</summary>
public interface IReviewCommentHost
{
    IReadOnlyList<ReviewComment> Load(string root, string branch);
    /// <summary>Null on success, else what went wrong.</summary>
    string? Save(string root, string branch, IReadOnlyList<ReviewComment> comments);
    ReviewAgentSearch FindAgents(string root);
    /// <summary>Pastes and submits <paramref name="prompt"/> in the agent's pane; null on success, else why it didn't.</summary>
    string? Send(ReviewAgentTarget target, string prompt);
}

/// <summary>For a review with nowhere to keep or send comments.</summary>
internal sealed class NoReviewCommentHost : IReviewCommentHost
{
    public IReadOnlyList<ReviewComment> Load(string root, string branch) => [];
    public string? Save(string root, string branch, IReadOnlyList<ReviewComment> comments) => null;
    public ReviewAgentSearch FindAgents(string root) => new([]);
    public string? Send(ReviewAgentTarget target, string prompt) => "Comments can't be sent from here";
}

/// <summary>A comment and the line it starts on now; null when its lines are gone.</summary>
public readonly record struct PlacedComment(ReviewComment Comment, int? StartLine);

/// <summary>The rules of comments that don't need a screen.</summary>
public static class ReviewCommentRules
{
    /// <summary>A longer range is remembered, and found again, by its first lines.</summary>
    public const int MaxQuotedLines = 20;

    /// <summary>The lines from <paramref name="start"/> to <paramref name="end"/> (1-based, inclusive) as a comment remembers them.</summary>
    public static string[] Quote(TextDocument document, int start, int end)
    {
        int last = Math.Min(Math.Min(end, document.RawLines.Length), start + MaxQuotedLines - 1);
        var quoted = new List<string>(Math.Max(0, last - start + 1));
        for (int line = start; line <= last; line++) quoted.Add(Normal(document.RawLines[line - 1]));
        return [.. quoted];
    }

    private static string Normal(string rawLine) => TextDocument.ContentString(rawLine).TrimEnd();

    /// <summary>Where the comment's lines are in <paramref name="document"/> now: the same place if they're still there, else the nearest copy, else null.</summary>
    public static int? Locate(ReviewComment comment, TextDocument document)
    {
        var lines = document.RawLines;
        if (comment.Quoted.Length == 0) return comment.StartLine <= lines.Length ? comment.StartLine : null;
        if (MatchesAt(comment.Quoted, lines, comment.StartLine - 1)) return comment.StartLine;
        int best = -1, bestDistance = int.MaxValue;
        for (int i = 0; i + comment.Quoted.Length <= lines.Length; i++)
        {
            int distance = Math.Abs(i + 1 - comment.StartLine);
            if (distance < bestDistance && MatchesAt(comment.Quoted, lines, i)) (best, bestDistance) = (i, distance);
        }
        return best >= 0 ? best + 1 : null;
    }

    private static bool MatchesAt(string[] quoted, string[] lines, int index)
    {
        if (index < 0 || index + quoted.Length > lines.Length) return false;
        for (int q = 0; q < quoted.Length; q++)
            if (Normal(lines[index + q]) != quoted[q]) return false;
        return true;
    }

    /// <summary>"L12" or "L12-14".</summary>
    public static string LineLabel(int start, int end) => end > start ? $"L{start}-{end}" : $"L{start}";

    /// <summary>A pasted or typed comment body: line endings as \n, tabs as spaces, nothing that could be terminal control.</summary>
    public static string CleanBody(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.Replace("\r\n", "\n").Replace('\r', '\n').EnumerateRunes())
        {
            if (rune.Value == '\t') sb.Append("    ");
            else if (rune.Value == '\n' || !Frame.IsControl(rune.Value)) sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}

/// <summary>Comments as the one prompt an agent gets.</summary>
public static class ReviewPrompt
{
    private const int MaxQuoteLines = 3, MaxQuoteChars = 120;

    public static string Format(string branch, string @base, IEnumerable<PlacedComment> comments)
    {
        var ordered = comments.OrderBy(c => c.Comment.Path, StringComparer.Ordinal).ThenBy(c => c.StartLine ?? c.Comment.StartLine).ToList();
        var sb = new StringBuilder();
        sb.Append("Review comments on branch ").Append(branch).Append(" (compared with ").Append(@base).Append("). ");
        sb.Append("Please address each one, then say what you changed.\n");
        for (int i = 0; i < ordered.Count; i++) AppendComment(sb, i + 1, ordered[i]);
        return sb.ToString().TrimEnd();
    }

    private static void AppendComment(StringBuilder sb, int number, PlacedComment placed)
    {
        var c = placed.Comment;
        int start = placed.StartLine ?? c.StartLine;
        int end = start + (c.EndLine - c.StartLine);
        sb.Append('\n').Append(number).Append(". ").Append(c.Path).Append(':').Append(start);
        if (end > start) sb.Append('-').Append(end);
        if (Qualifier(placed) is { } qualifier) sb.Append(" (").Append(qualifier).Append(')');
        sb.Append('\n');
        foreach (string line in c.Quoted.Take(MaxQuoteLines))
        {
            string text = line.Trim();
            sb.Append("   > ").Append(text.Length <= MaxQuoteChars ? text : text[..MaxQuoteChars] + "...").Append('\n');
        }
        foreach (string line in c.Body.Split('\n')) sb.Append("   ").Append(line).Append('\n');
    }

    private static string? Qualifier(PlacedComment placed)
    {
        var c = placed.Comment;
        var parts = new List<string>();
        if (c.Side == ReviewCommentSide.Old) parts.Add("removed code: this line number is in the base version");
        if (c.Commit is { } commit) parts.Add($"as of commit {commit[..Math.Min(7, commit.Length)]}");
        if (placed.StartLine is null) parts.Add("this code has changed since the comment; the quote shows what it was");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}

/// <summary>Text wrapping in terminal cells.</summary>
public static class TuiText
{
    /// <summary>The rows <paramref name="text"/> takes at <paramref name="width"/> cells: UTF-16 ranges, broken at newlines and between words where it can.</summary>
    public static List<(int Start, int End)> Wrap(string text, int width)
    {
        var rows = new List<(int, int)>();
        width = Math.Max(1, width);
        int start = 0, i = 0, cells = 0, lastSpace = -1;
        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                rows.Add((start, i));
                start = ++i;
                cells = 0;
                lastSpace = -1;
                continue;
            }
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int consumed);
            int w = Math.Max(0, CharWidth.Of(rune.Value));
            if (cells + w > width && i > start)
            {
                int breakAt = lastSpace > start ? lastSpace + 1 : i;
                rows.Add((start, breakAt));
                start = i = breakAt;
                cells = 0;
                lastSpace = -1;
                continue;
            }
            if (rune.Value == ' ') lastSpace = i;
            cells += w;
            i += consumed;
        }
        rows.Add((start, i));
        return rows;
    }

    /// <summary>The cells <paramref name="text"/> takes on one row.</summary>
    public static int Width(ReadOnlySpan<char> text)
    {
        int cells = 0;
        foreach (var rune in text.EnumerateRunes()) cells += Math.Max(0, CharWidth.Of(rune.Value));
        return cells;
    }
}

/// <summary>A comment's text while it's being written: one string and a caret, with movement by wrapped rows.</summary>
public sealed class CommentEditor(string text)
{
    public const int MaxChars = 20_000;

    private readonly string _original = text;

    public string Text { get; private set; } = text;
    /// <summary>UTF-16 index into <see cref="Text"/>.</summary>
    public int Caret { get; private set; } = text.Length;
    public bool Changed => Text != _original;

    public void Insert(string text)
    {
        string clean = ReviewCommentRules.CleanBody(text);
        clean = clean[..Math.Min(clean.Length, Math.Max(0, MaxChars - Text.Length))];
        Text = Text.Insert(Caret, clean);
        Caret += clean.Length;
    }

    /// <summary>Editing keys; false when the key isn't one of them.</summary>
    public bool OnKey(KeyEvent key, int width)
    {
        switch (key.Key)
        {
            case TuiKey.Backspace: DeleteBack(); return true;
            case TuiKey.Delete: DeleteForward(); return true;
            case TuiKey.Left: Caret = Previous(Caret); return true;
            case TuiKey.Right: Caret = Next(Caret); return true;
            case TuiKey.Up: MoveRows(-1, width); return true;
            case TuiKey.Down: MoveRows(+1, width); return true;
            case TuiKey.Home: Caret = TuiText.Wrap(Text, width)[RowOfCaret(width)].Start; return true;
            case TuiKey.End: Caret = TuiText.Wrap(Text, width)[RowOfCaret(width)].End; return true;
            case TuiKey.Char when key.Ctrl && key.Rune == 'n': Insert("\n"); return true;
            case TuiKey.Char when !key.Ctrl && !key.Alt && key.Rune >= 0x20: Insert(char.ConvertFromUtf32(key.Rune)); return true;
        }
        return false;
    }

    private int Previous(int index)
    {
        if (index <= 0) return 0;
        return index >= 2 && char.IsLowSurrogate(Text[index - 1]) && char.IsHighSurrogate(Text[index - 2]) ? index - 2 : index - 1;
    }

    private int Next(int index)
    {
        if (index >= Text.Length) return Text.Length;
        return index + 1 < Text.Length && char.IsHighSurrogate(Text[index]) && char.IsLowSurrogate(Text[index + 1]) ? index + 2 : index + 1;
    }

    private void DeleteBack()
    {
        int from = Previous(Caret);
        Text = Text.Remove(from, Caret - from);
        Caret = from;
    }

    private void DeleteForward()
    {
        int to = Next(Caret);
        Text = Text.Remove(Caret, to - Caret);
    }

    /// <summary>The wrapped row the caret is on.</summary>
    public int RowOfCaret(int width)
    {
        var rows = TuiText.Wrap(Text, width);
        for (int r = rows.Count - 1; r >= 0; r--) if (Caret >= rows[r].Start) return r;
        return 0;
    }

    private void MoveRows(int delta, int width)
    {
        var rows = TuiText.Wrap(Text, width);
        int current = RowOfCaret(width);
        int target = current + delta;
        if (target < 0 || target >= rows.Count) return;
        int column = TuiText.Width(Text.AsSpan(rows[current].Start, Caret - rows[current].Start));
        int index = rows[target].Start;
        while (index < rows[target].End && TuiText.Width(Text.AsSpan(rows[target].Start, Next(index) - rows[target].Start)) <= column) index = Next(index);
        Caret = index;
    }
}
