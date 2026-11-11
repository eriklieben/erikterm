using System.Text;

namespace ErikTerm.Ui.Compare;

public enum TuiKey
{
    None, Char, Enter, Tab, BackTab, Esc, Backspace, Delete, Up, Down, Left, Right, Home, End, PageUp, PageDown,
}

public enum MouseKind { Press, Release, Drag, WheelUp, WheelDown }

/// <summary>A key press. <see cref="Rune"/> is the character for <see cref="TuiKey.Char"/>; for Ctrl+letter it's the lowercase letter with <see cref="Ctrl"/> set.</summary>
public readonly record struct KeyEvent(TuiKey Key, int Rune = 0, bool Ctrl = false, bool Alt = false)
{
    public static KeyEvent OfChar(char c) => new(TuiKey.Char, c);
    public static KeyEvent CtrlOf(char c) => new(TuiKey.Char, c, Ctrl: true);
    public bool Is(char c) => Key == TuiKey.Char && Rune == c && !Ctrl && !Alt;
}

public readonly record struct MouseEvent(MouseKind Kind, int X, int Y, int Button = 0);

/// <summary>What the input decoder produced.</summary>
public readonly record struct TuiEvent(KeyEvent? Key = null, MouseEvent? Mouse = null, uint? BackgroundColor = null, bool? Focused = null, string? Paste = null);

/// <summary>Bytes from a terminal to keys and mouse events: CSI and SS3 sequences, SGR mouse, OSC 11 replies, UTF-8 text.</summary>
public sealed class TuiInputDecoder
{
    private const int MaxPasteBytes = 1 << 20;

    private readonly List<byte> _buf = [];
    private readonly List<byte> _paste = [];
    private bool _inPaste;

    /// <summary>True when a lone ESC is buffered: the caller gives it a moment, then calls <see cref="FlushEscape"/>.</summary>
    public bool EscapePending => !_inPaste && _buf.Count == 1 && _buf[0] == 0x1B;

    public void Feed(ReadOnlySpan<byte> bytes, List<TuiEvent> events)
    {
        _buf.AddRange(bytes);
        Pump(events);
    }

    /// <summary>The lone ESC really was the Esc key.</summary>
    public void FlushEscape(List<TuiEvent> events)
    {
        if (!EscapePending) return;
        _buf.Clear();
        events.Add(new TuiEvent(new KeyEvent(TuiKey.Esc)));
    }

    private void Pump(List<TuiEvent> events)
    {
        int i = 0;
        while (i < _buf.Count)
        {
            if (_inPaste)
            {
                i = TakePaste(i, events);
                if (_inPaste) break;
                continue;
            }
            int used = TakeInput(i, events);
            if (used == 0) break; // incomplete: more bytes coming
            i += used;
        }
        if (i > 0) _buf.RemoveRange(0, i);
    }

    /// <summary>Bytes consumed by the input at <paramref name="i"/>, or 0 when it's still incomplete.</summary>
    private int TakeInput(int i, List<TuiEvent> events)
    {
        byte b = _buf[i];
        if (b == 0x1B) return ParseEscape(i, events);
        if (b < 0x20 || b == 0x7F)
        {
            events.Add(new TuiEvent(Control(b)));
            return 1;
        }
        int rune = Utf8Rune(i, out int len);
        if (rune >= 0) events.Add(new TuiEvent(new KeyEvent(TuiKey.Char, rune)));
        return len;
    }

    /// <summary>A bracketed paste is one event: newlines in it must not be taken for Enter, nor its letters for commands.</summary>
    private int TakePaste(int i, List<TuiEvent> events)
    {
        var bytes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf)[i..];
        var terminator = "\e[201~"u8;
        int end = bytes.IndexOf(terminator);
        if (end < 0)
        {
            int partial = PartialTerminator(bytes, terminator);
            AddPaste(bytes[..^partial]);
            return _buf.Count - partial;
        }
        AddPaste(bytes[..end]);
        _inPaste = false;
        events.Add(new TuiEvent(Paste: Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_paste))));
        _paste.Clear();
        return i + end + terminator.Length;
    }

    private void AddPaste(ReadOnlySpan<byte> bytes)
    {
        int room = MaxPasteBytes - _paste.Count;
        if (room > 0) _paste.AddRange(bytes[..Math.Min(room, bytes.Length)]);
    }

    /// <summary>How many of the last bytes could be the start of a terminator split across two reads.</summary>
    private static int PartialTerminator(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> terminator)
    {
        for (int n = Math.Min(terminator.Length - 1, bytes.Length); n > 0; n--)
            if (bytes[^n..].SequenceEqual(terminator[..n])) return n;
        return 0;
    }

    private static KeyEvent Control(byte b) => b switch
    {
        0x0D or 0x0A => new KeyEvent(TuiKey.Enter),
        0x09 => new KeyEvent(TuiKey.Tab),
        0x7F or 0x08 => new KeyEvent(TuiKey.Backspace),
        _ => new KeyEvent(TuiKey.Char, 0x60 + b, Ctrl: true), // Ctrl+letter: 0x13 is Ctrl+S
    };

    private int Utf8Rune(int i, out int len)
    {
        byte b = _buf[i];
        int need = b switch
        {
            < 0x80 => 1,
            < 0xC0 => 0,
            < 0xE0 => 2,
            < 0xF0 => 3,
            _ => 4,
        };
        if (need == 0) { len = 1; return -1; } // a stray continuation byte: skip it
        if (i + need > _buf.Count) { len = 0; return -1; }
        Span<byte> tmp = stackalloc byte[4];
        for (int k = 0; k < need; k++) tmp[k] = _buf[i + k];
        len = need;
        return Rune.DecodeFromUtf8(tmp[..need], out var r, out int _) == System.Buffers.OperationStatus.Done ? r.Value : -1;
    }

    /// <summary>Bytes consumed by the escape sequence at <paramref name="i"/>, or 0 when it's still incomplete.</summary>
    private int ParseEscape(int i, List<TuiEvent> events)
    {
        if (i + 1 >= _buf.Count) return 0;
        byte next = _buf[i + 1];
        if (next == (byte)'[') return ParseCsi(i, events);
        if (next == (byte)']') return ParseOsc(i, events);
        if (next == (byte)'O')
        {
            if (i + 2 >= _buf.Count) return 0;
            var key = SimpleKey((char)_buf[i + 2], "");
            if (key is { } k) events.Add(new TuiEvent(k));
            return 3;
        }
        // Alt+key: ESC followed by a byte.
        if (next == 0x1B) { events.Add(new TuiEvent(new KeyEvent(TuiKey.Esc))); return 1; }
        if (next >= 0x20)
        {
            int rune = Utf8Rune(i + 1, out int len);
            if (len == 0) return 0;
            if (rune >= 0) events.Add(new TuiEvent(new KeyEvent(TuiKey.Char, rune, Alt: true)));
            return 1 + len;
        }
        return 1;
    }

    private int ParseCsi(int i, List<TuiEvent> events)
    {
        int end = i + 2;
        while (end < _buf.Count && !(_buf[end] >= 0x40 && _buf[end] <= 0x7E)) end++;
        if (end >= _buf.Count) return 0;
        byte final = _buf[end];
        var body = Encoding.ASCII.GetString(_buf.GetRange(i + 2, end - i - 2).ToArray());

        if (body.StartsWith('<') && final is (byte)'M' or (byte)'m')
        {
            AddSgrMouse(body, final, events);
            return end - i + 1;
        }

        if (body.Length == 0 && final is (byte)'I' or (byte)'O') // focus report (mode 1004)
        {
            events.Add(new TuiEvent(Focused: final == (byte)'I'));
            return end - i + 1;
        }

        if (body == "200" && final == (byte)'~') { _inPaste = true; return end - i + 1; }

        var key = SimpleKey((char)final, body);
        if (key is { } k) events.Add(new TuiEvent(k));
        return end - i + 1;
    }

    private static void AddSgrMouse(string body, byte final, List<TuiEvent> events)
    {
        var parts = body[1..].Split(';');
        if (parts.Length >= 3 && int.TryParse(parts[0], out int code) && int.TryParse(parts[1], out int mx) && int.TryParse(parts[2], out int my))
            events.Add(new TuiEvent(Mouse: new MouseEvent(MouseKindOf(code, final == (byte)'m'), mx - 1, my - 1, code & 3)));
    }

    private int ParseOsc(int i, List<TuiEvent> events)
    {
        int end = i + 2, terminatorLength = 0;
        for (; end < _buf.Count; end++)
        {
            if (_buf[end] == 0x07) { terminatorLength = 1; break; }
            if (_buf[end] == 0x1B && end + 1 < _buf.Count && _buf[end + 1] == (byte)'\\') { terminatorLength = 2; break; }
        }
        if (terminatorLength == 0)
        {
            if (_buf.Count - i > 512) return _buf.Count - i; // not a reply we asked for: don't buffer forever
            return 0;
        }
        var body = Encoding.ASCII.GetString(_buf.GetRange(i + 2, end - i - 2).ToArray());
        if (ParseColorReply(body) is { } color) events.Add(new TuiEvent(BackgroundColor: color));
        return end - i + terminatorLength;
    }

    /// <summary>The color of an OSC 11 reply: <c>11;rgb:RRRR/GGGG/BBBB</c>.</summary>
    public static uint? ParseColorReply(string body)
    {
        if (!body.StartsWith("11;rgb:", StringComparison.Ordinal)) return null;
        var parts = body["11;rgb:".Length..].Split('/');
        if (parts.Length != 3) return null;
        uint color = 0;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 4 || !uint.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
            uint max = (1u << (part.Length * 4)) - 1;
            color = (color << 8) | (v * 255 / max);
        }
        return color;
    }

    private static MouseKind MouseKindOf(int code, bool release)
    {
        if ((code & 64) != 0) return (code & 1) == 0 ? MouseKind.WheelUp : MouseKind.WheelDown;
        if (release) return MouseKind.Release;
        return (code & 32) != 0 ? MouseKind.Drag : MouseKind.Press;
    }

    private static KeyEvent? SimpleKey(char final, string body)
    {
        bool shift = false, alt = false, ctrl = false;
        var fields = body.Split(';');
        if (fields.Length >= 2 && int.TryParse(fields[1], out int mods))
        {
            mods--;
            shift = (mods & 1) != 0;
            alt = (mods & 2) != 0;
            ctrl = (mods & 4) != 0;
        }
        _ = shift;
        TuiKey key = final switch
        {
            'A' => TuiKey.Up,
            'B' => TuiKey.Down,
            'C' => TuiKey.Right,
            'D' => TuiKey.Left,
            'H' => TuiKey.Home,
            'F' => TuiKey.End,
            'Z' => TuiKey.BackTab,
            '~' => fields[0] switch
            {
                "1" or "7" => TuiKey.Home,
                "4" or "8" => TuiKey.End,
                "3" => TuiKey.Delete,
                "5" => TuiKey.PageUp,
                "6" => TuiKey.PageDown,
                _ => TuiKey.None,
            },
            _ => TuiKey.None,
        };
        return key == TuiKey.None ? null : new KeyEvent(key, Ctrl: ctrl, Alt: alt);
    }
}
