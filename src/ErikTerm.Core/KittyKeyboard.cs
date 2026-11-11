namespace ErikTerm.Core;

/// <summary>
/// Progressive enhancement flags of the kitty keyboard protocol
/// (https://sw.kovidgoyal.net/kitty/keyboard-protocol/).
/// </summary>
[Flags]
public enum KittyKeyboardModes : byte
{
    None = 0,
    /// <summary>Esc, alt+key, ctrl+key … are sent as <c>CSI code;mods u</c> instead of ambiguous legacy bytes.</summary>
    Disambiguate = 1,
    /// <summary>Repeat and release events are reported (<c>CSI code;mods:event u</c>).</summary>
    ReportEventTypes = 2,
    /// <summary>Text keys report their shifted and base-layout keys (<c>CSI code:shifted:base;mods u</c>).</summary>
    ReportAlternateKeys = 4,
    /// <summary>Every key, including text keys, Enter, Tab and Backspace, is sent as an escape code.</summary>
    ReportAllKeys = 8,
    /// <summary>With <see cref="ReportAllKeys"/>: text keys carry the text they produced (<c>CSI code;mods;codepoints u</c>).</summary>
    ReportAssociatedText = 16,

    /// <summary>The flags erikterm implements; others are dropped on push/set so a query reports the truth.</summary>
    Supported = Disambiguate | ReportEventTypes | ReportAlternateKeys | ReportAllKeys | ReportAssociatedText,
}

/// <summary>
/// The keyboard mode stack of one screen (main and alternate screens each have their own).
/// Entry 0 is the base, so <c>CSI = flags u</c> works with nothing pushed.
/// </summary>
internal sealed class KittyKeyboardStack
{
    private const int MaxDepth = 16;
    private readonly KittyKeyboardModes[] _entries = new KittyKeyboardModes[MaxDepth + 1];
    private int _depth;

    public KittyKeyboardModes Current => _entries[_depth];

    /// <summary>CSI &gt; flags u</summary>
    public void Push(int flags)
    {
        if (_depth == MaxDepth) Array.Copy(_entries, 2, _entries, 1, MaxDepth - 1); // evict the oldest pushed entry
        else _depth++;
        _entries[_depth] = (KittyKeyboardModes)flags & KittyKeyboardModes.Supported;
    }

    /// <summary>CSI &lt; n u. Popping everything resets the flags.</summary>
    public void Pop(int n)
    {
        if (n >= _depth) Reset();
        else _depth -= Math.Max(0, n);
    }

    /// <summary>CSI = flags ; mode u — mode 1 replaces, 2 adds, 3 removes.</summary>
    public void Set(int flags, int mode)
    {
        var f = (KittyKeyboardModes)flags & KittyKeyboardModes.Supported;
        ref var cur = ref _entries[_depth];
        cur = mode switch
        {
            2 => cur | f,
            3 => cur & ~f,
            _ => f,
        };
    }

    public void Reset()
    {
        Array.Clear(_entries);
        _depth = 0;
    }
}
