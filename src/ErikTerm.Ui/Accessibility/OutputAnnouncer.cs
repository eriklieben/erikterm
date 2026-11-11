using System.Text;

namespace ErikTerm.Ui.Accessibility;

/// <summary>Text for a screen reader to speak.</summary>
/// <param name="Text">What to say.</param>
/// <param name="Replaceable">
/// Only the latest of these matters — a flood's tail, a progress line — so a newer one may replace it while it
/// waits to be spoken, instead of queueing behind it.
/// </param>
public readonly record struct Announcement(string Text, bool Replaceable);

/// <summary>
/// Decides what of a terminal's new output a screen reader is told, and when. Pure: the caller feeds it output and
/// typing with the time, and asks it what is due.
/// <list type="bullet">
/// <item>Output is batched: what arrives within <see cref="BatchMs"/> of the first piece is spoken together.</item>
/// <item>Typed characters are not spoken again: the screen reader echoes keys itself, so output that starts with what
/// was just typed loses it (as Windows Terminal does). Echo that never comes (a password) is forgotten after
/// <see cref="TypedEchoMs"/>.</item>
/// <item>A flood is cut to its last <see cref="MaxLines"/> lines and <see cref="MaxChars"/> characters, and while it
/// lasts only one announcement goes out per <see cref="SlowMs"/>, each replacing the last.</item>
/// <item>Lines rewritten in place (progress bars) are spoken at most once per <see cref="SlowMs"/>, replaceable.</item>
/// <item>Nothing is said for the alternate screen: full-screen programs redraw constantly, and reading their
/// redraws would drown everything else. The screen reader can still read the screen.</item>
/// </list>
/// </summary>
public sealed class OutputAnnouncer
{
    public const int BatchMs = 100;
    public const int SlowMs = 1000;
    public const int TypedEchoMs = 1000;
    public const int MaxLines = 20;
    public const int MaxChars = 2000;
    private const int MaxTyped = 256;

    private readonly Queue<char> _typed = new();
    private long _typedAt;
    private readonly StringBuilder _pending = new();
    private long _pendingSince = -1;
    private bool _pendingRewriteOnly = true, _pendingTrimmed;
    private long _lastFloodAt = long.MinValue / 2;

    /// <summary>Whether anything is waiting to be announced.</summary>
    public bool HasPending => _pendingSince >= 0;

    /// <summary>Text the user typed into the pane (its echo is coming).</summary>
    public void Typed(string text, long now)
    {
        if (now - _typedAt > TypedEchoMs) _typed.Clear();
        _typedAt = now;
        foreach (char c in text)
        {
            if (c < ' ') continue;
            if (_typed.Count >= MaxTyped) _typed.Dequeue();
            _typed.Enqueue(c);
        }
    }

    /// <summary>New output (from <c>OutputTracker</c>).</summary>
    public void Output(string text, bool alternateScreen, bool rewriteOnly, long now)
    {
        if (alternateScreen) return;
        text = Sanitize(StripEcho(text, now));
        if (!HasReadable(text)) return;
        if (_pendingSince < 0)
        {
            _pendingSince = now;
            _pendingRewriteOnly = true;
            _pendingTrimmed = false;
        }
        else _pending.Append('\n');
        _pending.Append(text);
        _pendingRewriteOnly &= rewriteOnly;
        if (_pending.Length > MaxChars * 2) // a flood waiting for its slot: keep only the end
        {
            _pending.Remove(0, _pending.Length - MaxChars * 2);
            _pendingTrimmed = true;
        }
    }

    /// <summary>Drops what is waiting and the typing (the pane lost focus, the screen reader went away).</summary>
    public void Clear()
    {
        _pending.Clear();
        _pendingSince = -1;
        _typed.Clear();
    }

    /// <summary>The announcement that is due at <paramref name="now"/>, or null.</summary>
    public Announcement? Take(long now)
    {
        if (_pendingSince < 0 || now < DueAt()) return null;
        string text = Cap(_pending.ToString(), out bool cut);
        bool flood = cut || _pendingTrimmed;
        bool replaceable = flood || _pendingRewriteOnly;
        if (flood) _lastFloodAt = now;
        _pending.Clear();
        _pendingSince = -1;
        return new Announcement(text, replaceable);
    }

    /// <summary>Milliseconds until <see cref="Take"/> has something (int.MaxValue: nothing waits).</summary>
    public int WakeMs(long now) => _pendingSince < 0 ? int.MaxValue : (int)Math.Clamp(DueAt() - now, 0, int.MaxValue);

    private long DueAt()
    {
        bool slow = _pendingRewriteOnly || _pendingSince - _lastFloodAt < SlowMs;
        return _pendingSince + (slow ? SlowMs : BatchMs);
    }

    /// <summary>Takes the typed characters off the front of the output, as long as they match.</summary>
    private string StripEcho(string text, long now)
    {
        if (_typed.Count == 0) return text;
        if (now - _typedAt > TypedEchoMs)
        {
            _typed.Clear();
            return text;
        }
        int i = 0;
        while (i < text.Length && _typed.Count > 0 && _typed.Peek() == text[i])
        {
            _typed.Dequeue();
            i++;
        }
        if (i < text.Length) _typed.Clear(); // something else came: the rest of the typing won't echo in order
        return text[i..];
    }

    /// <summary>Control characters out, except line breaks.</summary>
    private static string Sanitize(string text)
    {
        bool clean = true;
        foreach (char c in text)
            if ((c < ' ' && c != '\n') || c == '\x7F') { clean = false; break; }
        if (clean) return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!((c < ' ' && c != '\n') || c == '\x7F')) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Whether anything in <paramref name="text"/> is more than a space or a control character.</summary>
    private static bool HasReadable(string text) => text.AsSpan().ContainsAnyExceptInRange('\0', ' ');

    /// <summary>The last <see cref="MaxLines"/> lines and <see cref="MaxChars"/> characters, blank lines dropped.</summary>
    private static string Cap(string text, out bool cut)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        cut = lines.Count > MaxLines;
        if (cut) lines.RemoveRange(0, lines.Count - MaxLines);
        string joined = string.Join('\n', lines);
        if (joined.Length <= MaxChars) return joined;
        cut = true;
        int start = joined.Length - MaxChars;
        int nl = joined.IndexOf('\n', start);
        return nl >= 0 && nl < joined.Length - 1 ? joined[(nl + 1)..] : joined[start..];
    }
}
