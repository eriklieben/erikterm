namespace ErikTerm.Ui.VimMode;

/// <summary>
/// One complete normal/visual-mode command: <c>[count] [operator [count]] key [argument]</c>. <see cref="Key"/> is the
/// command's name — a motion ("j", "w", "gg", "&lt;C-u&gt;"), a text object ("iw", "ip"), a doubled operator ("yy") or an
/// action ("v", "gx", "&lt;C-w&gt;"); <see cref="Arg"/> is the character after f/F/t/T/m/'/` or Ctrl-w.
/// </summary>
public readonly record struct VimCommand(int Count, char Operator, string Key, char Arg = '\0')
{
    /// <summary>The count, or 1 when none was typed.</summary>
    public int N => Count > 0 ? Count : 1;
    public bool HasCount => Count > 0;
}

public enum VimParse { Pending, Complete, Invalid, Cancelled }

/// <summary>
/// Collects keys into a <see cref="VimCommand"/>: counts (a leading 0 is the "0" motion, counts before and after the
/// operator multiply), the y operator, two-key commands (g…, z…, [[, ]], Ctrl-w …), character arguments (f, t, m, ')
/// and text objects (i/a + w, W, p — after y, or in visual mode, where a lone i/a would otherwise leave vim mode).
/// </summary>
public sealed class VimKeyParser
{
    private int _count, _opCount;
    private char _op;
    private string? _prefix;          // "g", "z", "[", "]", "f", "F", "t", "T", "m", "'", "`", "<C-w>", "i", "a"
    private readonly List<string> _typed = [];

    /// <summary>The keys of the command being typed (vim's 'showcmd'), e.g. "5y" or "g".</summary>
    public string Pending => _pending ??= _typed.Count == 0 ? "" : VimKeys.Format(_typed); // cached: read every frame
    private string? _pending;
    public bool IsPending => _typed.Count > 0;

    public void Reset()
    {
        _count = _opCount = 0;
        _op = '\0';
        _prefix = null;
        _typed.Clear();
        _pending = null;
    }

    /// <summary>Feeds one key. <paramref name="visual"/>: i/a start text objects instead of being commands.</summary>
    public VimParse Feed(string key, bool visual, out VimCommand command)
    {
        command = default;
        if (key == VimKeys.Esc) return Escape(out command);
        _typed.Add(key);
        _pending = null;

        if (_prefix is { } prefix) return Second(prefix, key, out command);
        if (TakeCountDigit(key)) return VimParse.Pending;
        if (AfterOperatorOrVisual(key, visual, out command) is { } handled) return handled;

        if (key is "g" or "z" or "[" or "]" or "f" or "F" or "t" or "T" or "m" or "'" or "`" or "<C-w>")
        {
            _prefix = key;
            return VimParse.Pending;
        }
        return Done(key, '\0', out command);
    }

    private VimParse Escape(out VimCommand command)
    {
        command = default;
        bool had = IsPending;
        Reset();
        if (had) return VimParse.Cancelled;
        command = new VimCommand(0, '\0', VimKeys.Esc);
        return VimParse.Complete;
    }

    /// <summary>Adds a digit to the count before or after the operator. A 0 with no count yet is not a digit: it is the "0" motion.</summary>
    private bool TakeCountDigit(string key)
    {
        if (!VimKeys.IsChar(key, out char c) || c is not (>= '0' and <= '9')) return false;
        int current = _op == '\0' ? _count : _opCount;
        if (c == '0' && current <= 0) return false;
        int updated = Math.Min(current * 10 + (c - '0'), 99_999);
        if (_op == '\0') _count = updated;
        else _opCount = updated;
        return true;
    }

    /// <summary>What a key means because an operator is waiting or the mode is visual; null when it means nothing special.</summary>
    private VimParse? AfterOperatorOrVisual(string key, bool visual, out VimCommand command)
    {
        command = default;
        if (_op != '\0')
        {
            // y + y / o / c: whole line, the command's output, the command line.
            if (key == _op.ToString()) return Done($"{_op}{_op}", '\0', out command);
            if (_op == 'y' && key is "o" or "c") return Done("y" + key, '\0', out command);
            if (key is not ("i" or "a")) return null;
            _prefix = key;
            return VimParse.Pending;
        }
        if (key == "y" && !visual) // in visual mode y yanks the selection right away
        {
            _op = 'y';
            return VimParse.Pending;
        }
        if (visual && key is "i" or "a")
        {
            _prefix = key;
            return VimParse.Pending;
        }
        return null;
    }

    private VimParse Second(string prefix, string key, out VimCommand command)
    {
        command = default;
        _prefix = null;
        switch (prefix)
        {
            case "g":
                return key is "g" or "e" or "E" or "x" or "f" or "o" or "O" or "h" or "t" or "T" or "?" or "_" or "m"
                    ? Done("g" + key, '\0', out command)
                    : Invalid();
            case "z":
                return AfterZ(key, out command);
            case "[" or "]":
                return key == prefix ? Done(prefix + key, '\0', out command) : Invalid();
            case "i" or "a":
                return key is "w" or "W" or "p" or "s" ? Done(prefix + key, '\0', out command) : Invalid();
            case "<C-w>":
                return AfterCtrlW(key, out command);
            default: // f F t T m ' `: one character
                return VimKeys.IsChar(key, out char arg) ? Done(prefix, arg, out command) : Invalid();
        }
    }

    private VimParse AfterZ(string key, out VimCommand command)
    {
        command = default;
        if (key == VimKeys.Enter) return Done("zt", '\0', out command);
        if (key == ".") return Done("zz", '\0', out command);
        if (key == "-") return Done("zb", '\0', out command);
        return key is "z" or "t" or "b" ? Done("z" + key, '\0', out command) : Invalid();
    }

    /// <summary>Ctrl-w Ctrl-h is Ctrl-w h; counts may come after Ctrl-w too (Ctrl-w 5 >).</summary>
    private VimParse AfterCtrlW(string key, out VimCommand command)
    {
        command = default;
        if (VimKeys.IsCtrl(key, out char cc)) return Done("<C-w>", cc, out command);
        if (VimKeys.IsChar(key, out char wc))
        {
            if (wc is >= '0' and <= '9' && (wc != '0' || _count > 0))
            {
                _count = Math.Min(_count * 10 + (wc - '0'), 99_999);
                _prefix = "<C-w>";
                return VimParse.Pending;
            }
            return Done("<C-w>", wc, out command);
        }
        return key switch
        {
            VimKeys.Left => Done("<C-w>", 'h', out command),
            VimKeys.Down => Done("<C-w>", 'j', out command),
            VimKeys.Up => Done("<C-w>", 'k', out command),
            VimKeys.Right => Done("<C-w>", 'l', out command),
            _ => Invalid(),
        };
    }

    private VimParse Done(string key, char arg, out VimCommand command)
    {
        int count = _count > 0 && _opCount > 0 ? _count * _opCount : Math.Max(_count, _opCount);
        command = new VimCommand(Math.Min(count, 99_999), _op, key, arg);
        Reset();
        return VimParse.Complete;
    }

    private VimParse Invalid()
    {
        Reset();
        return VimParse.Invalid;
    }
}
