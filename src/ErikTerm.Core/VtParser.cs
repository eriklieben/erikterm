namespace ErikTerm.Core;

/// <summary>Numeric parameters of a CSI sequence. A value of 0 means "omitted / default".</summary>
public readonly ref struct CsiParams
{
    private readonly ReadOnlySpan<int> _values;
    private readonly ReadOnlySpan<bool> _isSub;

    public CsiParams(ReadOnlySpan<int> values, ReadOnlySpan<bool> isSub)
    {
        _values = values;
        _isSub = isSub;
    }

    public int Count => _values.Length;
    public int this[int index] => _values[index];

    /// <summary>True when the parameter was introduced by ':' (a sub-parameter, e.g. 38:2::r:g:b).</summary>
    public bool IsSub(int index) => _isSub[index];

    /// <summary>Value at <paramref name="index"/>, or <paramref name="fallback"/> if absent or zero.</summary>
    public int Get(int index, int fallback) =>
        index < _values.Length && _values[index] != 0 ? _values[index] : fallback;

    /// <summary>Raw value at <paramref name="index"/> (0 if absent).</summary>
    public int Raw(int index) => index < _values.Length ? _values[index] : 0;
}

public static class VtUtf8
{
    /// <summary>
    /// Decodes one well-formed UTF-8 sequence at the start of <paramref name="text"/> (lead byte non-ASCII). Returns its
    /// length, or 0 for anything else (bad lead or continuation byte, overlong form, surrogate, past U+10FFFF, or a
    /// sequence that continues in the next chunk), which is left to the parser's state machine.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
#pragma warning disable S3776 // one inlined method on the UTF-8 print path; splitting it by sequence length measured 6-9% slower on the unicode benchmark
    public static int Decode(ReadOnlySpan<byte> text, out int codepoint)
#pragma warning restore S3776
    {
        byte b = text[0];
        codepoint = 0;
        if (b is >= 0xC2 and <= 0xDF)
        {
            if (text.Length < 2 || (text[1] & 0xC0) != 0x80) return 0;
            codepoint = (b & 0x1F) << 6 | (text[1] & 0x3F);
            return 2;
        }
        if (b is >= 0xE0 and <= 0xEF)
        {
            if (text.Length < 3 || (text[1] & 0xC0) != 0x80 || (text[2] & 0xC0) != 0x80) return 0;
            codepoint = (b & 0x0F) << 12 | (text[1] & 0x3F) << 6 | (text[2] & 0x3F);
            return codepoint is >= 0x800 and not (>= 0xD800 and <= 0xDFFF) ? 3 : 0;
        }
        if (b is >= 0xF0 and <= 0xF4)
        {
            if (text.Length < 4 || (text[1] & 0xC0) != 0x80 || (text[2] & 0xC0) != 0x80 || (text[3] & 0xC0) != 0x80) return 0;
            codepoint = (b & 0x07) << 18 | (text[1] & 0x3F) << 12 | (text[2] & 0x3F) << 6 | (text[3] & 0x3F);
            return codepoint is >= 0x10000 and <= 0x10FFFF ? 4 : 0;
        }
        return 0;
    }
}

public interface IVtHandler
{
    /// <summary>A run of printable ASCII (0x20..0x7E) — the hot path.</summary>
    void PrintAscii(ReadOnlySpan<byte> text);

    /// <summary>
    /// Text that starts with a non-ASCII byte, in the ground state. Prints what it can of it (see
    /// <see cref="VtUtf8.Decode"/>) and returns the bytes it took; 0 means the parser handles the text byte by byte.
    /// </summary>
    int PrintUtf8(ReadOnlySpan<byte> text);

    void Print(int codepoint);
    void Execute(byte control);
    void EscDispatch(ReadOnlySpan<byte> intermediates, byte final);
    void CsiDispatch(CsiParams parameters, ReadOnlySpan<byte> intermediates, byte privateMarker, byte final);

    /// <summary>CSI row ; col H with both values given and non-zero, the most common sequence from full-screen programs.</summary>
    void CursorPosition(int row, int col);

    void OscDispatch(ReadOnlySpan<byte> data);

    /// <summary>An APC string (<c>ESC _ … ST</c>), e.g. kitty graphics (<c>G…</c>).</summary>
    void ApcDispatch(ReadOnlySpan<byte> data);

    /// <summary>
    /// A DCS string (<c>ESC P … ST</c>): parameters, intermediates, final byte and data, unparsed (e.g. sixel
    /// <c>0;1;0q#0;2;…</c>).
    /// </summary>
    void DcsDispatch(ReadOnlySpan<byte> data);
}

/// <summary>
/// DEC ANSI-compatible parser (Paul Williams' state machine, https://vt100.net/emu/dec_ansi_parser),
/// with UTF-8 decoding in the ground state. C1 controls are not recognised as 8-bit bytes (UTF-8 mode).
/// DCS and APC strings are collected and dispatched whole (sixel, kitty graphics); SOS / PM are ignored.
/// tmux passthrough (<c>ESC P tmux; … ESC \</c>, with every ESC of the wrapped sequences doubled) is unwrapped
/// and the payload parsed as if it had been sent directly — programs that believe they run inside tmux (the
/// tmux emulation) wrap images and OSC 52 this way.
/// OSC strings are capped at 64 KB, except iTerm2 image transfers (<c>1337;File=</c> / <c>1337;FilePart=</c>),
/// which, like DCS and APC, may grow to 64 MB; a buffer grown past 64 KB is dropped after its dispatch.
/// Allocation-free after construction, except when a string grows its buffer.
/// Generic over a struct handler so Native AOT specialises it: every callback is a direct, inlinable
/// call instead of interface dispatch (which only the JIT's dynamic PGO could otherwise devirtualise).
/// </summary>
public sealed class VtParser<THandler> where THandler : struct, IVtHandler
{
    private enum State : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsString,
        DcsEscape, // ESC inside a tmux passthrough string: ESC ESC is a literal ESC, ESC \ ends it
        ApcString,
        IgnoreString,
    }

    private const int MaxParams = 32;
    private const int MaxIntermediates = 4;
    private const int MaxOsc = 64 * 1024;
    /// <summary>Cap for image payloads: iTerm2 OSC 1337 File=, DCS (sixel) and APC (kitty graphics).</summary>
    private const int MaxImageString = 64 * 1024 * 1024;
    private const int MaxParamValue = 65535;

    private THandler _handler; // not readonly: avoids defensive copies of the struct
    private State _state;

    private readonly int[] _params = new int[MaxParams];
    private readonly bool[] _isSub = new bool[MaxParams];
    private int _paramCount;
    private byte _private;
    private readonly byte[] _intermediates = new byte[MaxIntermediates];
    private int _intermediateCount;

    private byte[] _osc = new byte[256];
    private int _oscLength;

    // DCS / APC string being collected (only one can be active).
    private byte[] _str = new byte[256];
    private int _strLength;
    private bool _strOverflow;

    private int _passthroughDepth;

    private int _utf8Codepoint;
    private int _utf8Remaining;
    private int _utf8Min;

    public VtParser(THandler handler) => _handler = handler;

    /// <summary>
    /// While an OSC is being dispatched: it ended with BEL rather than ST. xterm answers a query with the terminator it
    /// came with, and some programs only understand that one.
    /// </summary>
    public bool OscEndedWithBel { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length)
        {
            if (_state == State.Ground && _utf8Remaining == 0)
            {
                // Fast path: vectorised scan for the end of a printable-ASCII run.
                var rest = data[i..];
                int run = PrintableRun(rest);
                if (run > 0)
                {
                    _handler.PrintAscii(rest[..run]);
                    i += run;
                    if (i >= data.Length) break;
                }
                if (TryGroundRun(data, ref i)) continue;
            }
            Advance(data[i++]);
        }
    }

    /// <summary>The length of the printable ASCII at the start of <paramref name="text"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int PrintableRun(ReadOnlySpan<byte> text)
    {
        int run = text.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
        return run < 0 ? text.Length : run;
    }

    /// <summary>A UTF-8 run or a whole CSI sequence starting at <paramref name="i"/>, handled without the byte-at-a-time path.</summary>
    private bool TryGroundRun(ReadOnlySpan<byte> data, ref int i)
    {
        byte b = data[i];
        if (b >= 0x80)
        {
            int used = _handler.PrintUtf8(data[i..]);
            i += used;
            return used > 0;
        }
        return b == 0x1B && TryDispatchWholeCsi(data, ref i);
    }

    /// <summary>
    /// ESC [ params final, entirely within <paramref name="data"/> and with nothing but digits, ';' and ':' before the
    /// final byte, dispatched here. Anything else (a private marker, intermediates, a control character, a sequence
    /// that continues in the next chunk) returns false, leaving it to the state machine, whose rules this mirrors.
    /// </summary>
    private bool TryDispatchWholeCsi(ReadOnlySpan<byte> data, ref int i)
    {
        int p = i + 1;
        if (p >= data.Length || data[p] != (byte)'[') return false;
        Span<int> values = _params;
        Span<bool> subs = _isSub;
        int count = 0, value = 0;
        for (p++; p < data.Length; p++)
        {
            byte b = data[p];
            int digit = b - '0';
            if ((uint)digit <= 9)
            {
                count = OpenFirstParam(subs, count);
                value = Math.Min(value * 10 + digit, MaxParamValue);
            }
            else if (b is (byte)';' or (byte)':')
            {
                count = OpenFirstParam(subs, count);
                values[count - 1] = value;
                if (count == MaxParams) continue; // further parameters are dropped; digits keep adding to the last one
                value = 0;
                subs[count] = b == ':';
                count++;
            }
            else
            {
                if (count > 0) values[count - 1] = value;
                return EndWholeCsi(b, p, count, ref i);
            }
        }
        return false;
    }

    /// <summary>The parameter count once a digit or separator has been seen: the first parameter exists from then on.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int OpenFirstParam(Span<bool> subs, int count)
    {
        if (count != 0) return count;
        subs[0] = false;
        return 1;
    }

    private bool EndWholeCsi(byte final, int p, int count, ref int i)
    {
        if (final is < 0x40 or > 0x7E) return false;
        i = p + 1;
        if (final == (byte)'H' && count == 2 && _params[0] != 0 && _params[1] != 0 && !_isSub[1])
        {
            _handler.CursorPosition(_params[0], _params[1]);
            return true;
        }
        _paramCount = count;
        _intermediateCount = 0;
        _private = 0;
        _handler.CsiDispatch(new CsiParams(_params.AsSpan(0, count), _isSub.AsSpan(0, count)), default, 0, final);
        return true;
    }

#pragma warning disable S3776 // the parser's per-byte state machine, kept in one method for the hottest path
    private void Advance(byte b)
#pragma warning restore S3776
    {
        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8Codepoint = (_utf8Codepoint << 6) | (b & 0x3F);
                if (--_utf8Remaining == 0)
                {
                    int cp = _utf8Codepoint;
                    if (cp < _utf8Min || cp is >= 0xD800 and <= 0xDFFF || cp > 0x10FFFF) cp = 0xFFFD;
                    _handler.Print(cp);
                }
                return;
            }
            _utf8Remaining = 0;
            _handler.Print(0xFFFD);
        }

        // "Anywhere" transitions.
        if (b == 0x18 || b == 0x1A)
        {
            _handler.Execute(b);
            if (_state is State.DcsString or State.DcsEscape or State.ApcString) ResetString(); // cancelled: not dispatched
            _state = State.Ground;
            return;
        }
        if (b == 0x1B)
        {
            if (_state == State.DcsEscape) { StrPut(b); _state = State.DcsString; return; } // doubled ESC
            if (_state == State.DcsString && IsTmuxPassthrough()) { _state = State.DcsEscape; return; }
            if (_state == State.OscString) DispatchOsc();
            else if (_state == State.DcsString) DispatchString(dcs: true);
            else if (_state == State.ApcString) DispatchString(dcs: false);
            Clear();
            _state = State.Escape;
            return;
        }

        if (_state == State.DcsEscape)
        {
            // ESC + anything but ESC ends the passthrough: ST is ESC \, anything else starts a new sequence.
            _state = State.Ground;
            DispatchString(dcs: true);
            if (b == (byte)'\\') return;
            Clear();
            _state = State.Escape;
        }

        if (b >= 0x80)
        {
            if (_state == State.Ground) Ground(b);
            else if (_state == State.OscString) OscPut(b);
            else if (_state is State.DcsString or State.ApcString) StrPut(b);
            return;
        }

        switch (_state)
        {
            case State.Ground: Ground(b); break;
            case State.Escape: Escape(b); break;
            case State.EscapeIntermediate: EscapeIntermediate(b); break;
            case State.CsiEntry:
            case State.CsiParam: CsiParam(b); break;
            case State.CsiIntermediate: CsiIntermediate(b); break;
            case State.CsiIgnore: CsiIgnore(b); break;
            case State.OscString: Osc(b); break;
            case State.DcsString:
            case State.ApcString:
                if (b >= 0x20 && b != 0x7F) StrPut(b); // C0 controls (line breaks in sixel data) are ignored
                else if (_state == State.DcsString && IsTmuxPassthrough()) StrPut(b); // …but not in what tmux passes on
                break;
            case State.IgnoreString: break;
        }
    }

    private void Ground(byte b)
    {
        if (b < 0x20) { _handler.Execute(b); return; }
        if (b == 0x7F) return;
        if (b < 0x80) { _handler.Print(b); return; }

        if (b is >= 0xC2 and <= 0xDF) { _utf8Codepoint = b & 0x1F; _utf8Remaining = 1; _utf8Min = 0x80; }
        else if (b is >= 0xE0 and <= 0xEF) { _utf8Codepoint = b & 0x0F; _utf8Remaining = 2; _utf8Min = 0x800; }
        else if (b is >= 0xF0 and <= 0xF4) { _utf8Codepoint = b & 0x07; _utf8Remaining = 3; _utf8Min = 0x10000; }
        else _handler.Print(0xFFFD);
    }

    private void Escape(byte b)
    {
        if (b < 0x20) { _handler.Execute(b); return; }
        if (b <= 0x2F) { Collect(b); _state = State.EscapeIntermediate; return; }
        switch (b)
        {
            case (byte)'[': Clear(); _state = State.CsiEntry; return;
            case (byte)']': _oscLength = 0; _state = State.OscString; return;
            case (byte)'P': ResetString(); _state = State.DcsString; return;
            case (byte)'_': ResetString(); _state = State.ApcString; return;
            case (byte)'X': case (byte)'^': _state = State.IgnoreString; return;
            case 0x7F: return;
        }
        _handler.EscDispatch(_intermediates.AsSpan(0, _intermediateCount), b);
        _state = State.Ground;
    }

    private void EscapeIntermediate(byte b)
    {
        if (b < 0x20) { _handler.Execute(b); return; }
        if (b <= 0x2F) { Collect(b); return; }
        if (b == 0x7F) return;
        _handler.EscDispatch(_intermediates.AsSpan(0, _intermediateCount), b);
        _state = State.Ground;
    }

    private void CsiParam(byte b)
    {
        if (b < 0x20) { _handler.Execute(b); return; }
        if (b is >= (byte)'0' and <= (byte)'9')
        {
            EnsureParam();
            ref int p = ref _params[_paramCount - 1];
            p = Math.Min(p * 10 + (b - '0'), MaxParamValue);
            _state = State.CsiParam;
            return;
        }
        if (b == ';' || b == ':')
        {
            EnsureParam();
            if (_paramCount < MaxParams)
            {
                _params[_paramCount] = 0;
                _isSub[_paramCount] = b == ':';
                _paramCount++;
            }
            _state = State.CsiParam;
            return;
        }
        if (b is >= 0x3C and <= 0x3F)
        {
            if (_state == State.CsiEntry) { _private = b; _state = State.CsiParam; }
            else _state = State.CsiIgnore;
            return;
        }
        if (b <= 0x2F) { Collect(b); _state = State.CsiIntermediate; return; }
        if (b == 0x7F) return;
        DispatchCsi(b);
    }

    private void CsiIntermediate(byte b)
    {
        if (b < 0x20) { _handler.Execute(b); return; }
        if (b <= 0x2F) { Collect(b); return; }
        if (b <= 0x3F) { _state = State.CsiIgnore; return; }
        if (b == 0x7F) return;
        DispatchCsi(b);
    }

    private void CsiIgnore(byte b)
    {
        if (b < 0x20) { _handler.Execute(b); return; }
        if (b is >= 0x40 and <= 0x7E) _state = State.Ground;
    }

    private void Osc(byte b)
    {
        if (b == 0x07) { OscEndedWithBel = true; DispatchOsc(); OscEndedWithBel = false; _state = State.Ground; return; }
        if (b < 0x20) return;
        OscPut(b);
    }

    private void EnsureParam()
    {
        if (_paramCount == 0)
        {
            _params[0] = 0;
            _isSub[0] = false;
            _paramCount = 1;
        }
    }

    private void Collect(byte b)
    {
        if (_intermediateCount < MaxIntermediates) _intermediates[_intermediateCount++] = b;
    }

    private void Clear()
    {
        _paramCount = 0;
        _intermediateCount = 0;
        _private = 0;
    }

    private void DispatchCsi(byte final)
    {
        _handler.CsiDispatch(
            new CsiParams(_params.AsSpan(0, _paramCount), _isSub.AsSpan(0, _paramCount)),
            _intermediates.AsSpan(0, _intermediateCount),
            _private,
            final);
        _state = State.Ground;
    }

    private void OscPut(byte b)
    {
        if (_oscLength == _osc.Length)
        {
            int cap = _osc.Length >= MaxOsc && IsImageOsc() ? MaxImageString : MaxOsc;
            if (_osc.Length >= cap) return;
            Array.Resize(ref _osc, Math.Min(_osc.Length * 2, cap));
        }
        _osc[_oscLength++] = b;
    }

    /// <summary>iTerm2 image transfers may exceed the normal OSC cap.</summary>
    private bool IsImageOsc() => _osc.AsSpan(0, _oscLength).StartsWith("1337;File"u8);

    private void DispatchOsc()
    {
        _handler.OscDispatch(_osc.AsSpan(0, _oscLength));
        _oscLength = 0;
        if (_osc.Length > MaxOsc) _osc = new byte[256]; // don't keep an image-sized buffer around
    }

    private void ResetString()
    {
        _strLength = 0;
        _strOverflow = false;
        if (_str.Length > MaxOsc) _str = new byte[256];
    }

    private void StrPut(byte b)
    {
        if (_strLength == _str.Length)
        {
            if (_str.Length >= MaxImageString) { _strOverflow = true; return; }
            Array.Resize(ref _str, Math.Min(_str.Length * 2, MaxImageString));
        }
        _str[_strLength++] = b;
    }

    /// <summary>The DCS being collected is tmux passthrough: <c>tmux;</c> then the wrapped sequences.</summary>
    private bool IsTmuxPassthrough() => _strLength >= 5 && _str.AsSpan(0, 5).SequenceEqual("tmux;"u8);

    private void DispatchString(bool dcs)
    {
        if (dcs && IsTmuxPassthrough())
        {
            byte[] payload = _str.AsSpan(5, _strLength - 5).ToArray();
            bool overflow = _strOverflow;
            ResetString();
            if (overflow || _passthroughDepth >= 4) return; // nested passthrough is fine, a loop isn't
            _passthroughDepth++;
            try { Feed(payload); }
            finally { _passthroughDepth--; }
            return;
        }
        if (!_strOverflow)
        {
            if (dcs) _handler.DcsDispatch(_str.AsSpan(0, _strLength));
            else _handler.ApcDispatch(_str.AsSpan(0, _strLength));
        }
        ResetString();
    }
}
