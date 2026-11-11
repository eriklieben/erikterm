using ErikTerm.App.Native;
using ErikTerm.Core;

namespace ErikTerm.App;

/// <summary>Kind of key event, numbered as in the kitty keyboard protocol.</summary>
internal enum KeyEventKind : byte { Press = 1, Repeat = 2, Release = 3 }

/// <summary>
/// What else the window system knows about a text key, for kitty flag 4 (report alternate keys).
/// <paramref name="LayoutKey"/> is the key's unshifted character in the active layout (0 if unknown) — the key code
/// passed to <see cref="KeyEncoder.Encode"/> is the latin_letters keycode, which becomes the base-layout key when
/// the two differ (e.g. Cyrillic ф → base a). <paramref name="ShiftedKey"/> is the character with Shift (0 if unknown).
/// </summary>
internal readonly record struct KeyAlternates(uint LayoutKey, uint ShiftedKey);

/// <summary>
/// Encodes keys for the program. Printable text normally arrives separately via SDL text-input events (so IME,
/// dead keys and AltGr work), and <see cref="Encode"/> returns null for it. With no kitty flags this is xterm's
/// legacy encoding; with flags set it is the kitty keyboard protocol
/// (https://sw.kovidgoyal.net/kitty/keyboard-protocol/): flags 1 (disambiguate), 2 (event types),
/// 4 (alternate keys: <c>CSI key:shifted:base ; mods u</c>), 8 (report all keys as escape codes) and
/// 16 (associated text: <c>CSI key ; mods ; codepoints u</c>) are implemented.
/// </summary>
internal static class KeyEncoder
{
    private static readonly Dictionary<char, char> ShiftedUs = new()
    {
        ['1'] = '!', ['2'] = '@', ['3'] = '#', ['4'] = '$', ['5'] = '%', ['6'] = '^', ['7'] = '&', ['8'] = '*',
        ['9'] = '(', ['0'] = ')', ['-'] = '_', ['='] = '+', ['['] = '{', [']'] = '}', ['\\'] = '|', [';'] = ':',
        ['\''] = '"', [','] = '<', ['.'] = '>', ['/'] = '?', ['`'] = '~',
    };

    private static readonly int[] FunctionKeyCodes = [15, 17, 18, 19, 20, 21, 23, 24]; // F5..F12

    // SDL3 keycodes not in Sdl.cs (only used here).
    private const uint K_CAPSLOCK = 0x40000039, K_PRINTSCREEN = 0x40000046, K_SCROLLLOCK = 0x40000047, K_PAUSE = 0x40000048;
    private const uint K_NUMLOCKCLEAR = 0x40000053, K_KP_DIVIDE = 0x40000054, K_KP_MULTIPLY = 0x40000055;
    private const uint K_KP_MINUS = 0x40000056, K_KP_PLUS = 0x40000057, K_KP_1 = 0x40000059, K_KP_9 = 0x40000061;
    private const uint K_KP_0 = 0x40000062, K_KP_PERIOD = 0x40000063, K_APPLICATION = 0x40000065, K_KP_EQUALS = 0x40000067;
    private const uint K_F13 = 0x40000068, K_F24 = 0x40000073, K_MENU = 0x40000076;
    private const uint K_LCTRL = 0x400000E0, K_RGUI = 0x400000E7;
    private const ushort KMOD_NUM = 0x1000, KMOD_CAPS = 0x2000;

    private const int ModShift = 1, ModAlt = 2, ModCtrl = 4, ModSuper = 8, ModCapsLock = 64, ModNumLock = 128;

    /// <summary>
    /// Bytes to send for a key event, or null when the key sends nothing here (e.g. plain text, which comes from
    /// the text-input event; or a release when event types aren't requested).
    /// </summary>
    /// <param name="alternates">Layout and shifted keys (used with flag 4).</param>
    /// <param name="text">The text the key produced (used with flags 8 + 16; see <see cref="WantsText"/>).</param>
    public static string? Encode(uint key, ushort mod, bool applicationCursor,
        KittyKeyboardModes kitty = KittyKeyboardModes.None, KeyEventKind kind = KeyEventKind.Press,
        KeyAlternates alternates = default, string? text = null)
    {
        if ((kitty & (KittyKeyboardModes.Disambiguate | KittyKeyboardModes.ReportAllKeys)) == 0)
            return kind == KeyEventKind.Release ? null : Legacy(key, mod, applicationCursor);
        return Kitty(key, mod, applicationCursor, kitty, kind, alternates, text);
    }

    /// <summary>
    /// What Claude Code reads as "insert a newline" when the kitty keyboard protocol is off: ESC CR (Alt+Enter), the
    /// sequence its /terminal-setup binds Shift+Enter to in VS Code, Zed and other terminals without the protocol.
    /// </summary>
    public const string AgentNewline = "\e\r";

    /// <summary>Agents (AgentCatalog ids) whose input box takes ESC CR as a newline.</summary>
    private static readonly string[] AgentsWithEscNewline = ["claude-code"];

    /// <summary>
    /// The SendAgentNewline action (Shift+Enter by default): <see cref="AgentNewline"/> when the pane runs an agent that
    /// wants it and the kitty keyboard protocol is off — legacy encoding sends a plain CR for Shift+Enter, which
    /// submits the prompt. Null otherwise: the key is encoded as usual (with the protocol on that is <c>CSI 13;2u</c>,
    /// which the agent already reads as Shift+Enter; in a shell it stays a CR).
    /// </summary>
    public static string? ShiftEnterForAgent(string? agentId, KittyKeyboardModes kitty)
    {
        if (agentId is null || Array.IndexOf(AgentsWithEscNewline, agentId) < 0) return null;
        return (kitty & (KittyKeyboardModes.Disambiguate | KittyKeyboardModes.ReportAllKeys)) == 0 ? AgentNewline : null;
    }

    private const KittyKeyboardModes AllKeysWithText = KittyKeyboardModes.ReportAllKeys | KittyKeyboardModes.ReportAssociatedText;

    /// <summary>
    /// True when the escape code for this key press carries its associated text (kitty flags 8 + 16), so the caller
    /// should wait for the text-input event that follows the key-down before encoding. Ctrl/Super chords produce no
    /// text, so they are not held back.
    /// </summary>
    public static bool WantsText(uint key, ushort mod, KittyKeyboardModes kitty, KeyEventKind kind)
    {
        if ((kitty & AllKeysWithText) != AllKeysWithText || kind == KeyEventKind.Release) return false;
        if ((mod & (Sdl.KMOD_CTRL | Sdl.KMOD_GUI)) != 0) return false;
        bool numLock = (mod & KMOD_NUM) != 0;
        return Functional(key, numLock) is null && !IsModifierOrLockKey(key) && TextKeyCode(key) >= 0;
    }

    // ---- Kitty keyboard protocol ------------------------------------------------------------------

    private static string? Kitty(uint key, ushort mod, bool applicationCursor, KittyKeyboardModes flags, KeyEventKind kind,
        KeyAlternates alternates, string? text)
    {
        bool all = (flags & KittyKeyboardModes.ReportAllKeys) != 0;
        if ((flags & KittyKeyboardModes.ReportEventTypes) == 0)
        {
            if (kind == KeyEventKind.Release) return null;
            kind = KeyEventKind.Press; // repeats are reported as presses
        }

        int mods = KittyModifiers(mod);
        // Lock modifiers are only reported in "all keys" mode, to keep legacy-looking keys usable.
        int locks = all ? KittyLockModifiers(mod) : 0;

        if (Functional(key, (mod & KMOD_NUM) != 0) is { } functional)
            return KittyFunctional(functional, key, mod, applicationCursor, all, kind, mods, locks);

        if (IsModifierOrLockKey(key))
            return all ? Csi(ModifierKeyCode(key), mods | locks, kind, 'u') : null;

        return KittyTextKey(key, flags, kind, alternates, text, all, mods, locks);
    }

    private static int KittyModifiers(ushort mod)
    {
        int mods = 0;
        if ((mod & Sdl.KMOD_SHIFT) != 0) mods |= ModShift;
        if ((mod & Sdl.KMOD_LALT) != 0) mods |= ModAlt;
        if ((mod & Sdl.KMOD_CTRL) != 0) mods |= ModCtrl;
        if ((mod & Sdl.KMOD_GUI) != 0) mods |= ModSuper;
        return mods;
    }

    private static int KittyLockModifiers(ushort mod)
    {
        int locks = 0;
        if ((mod & KMOD_CAPS) != 0) locks |= ModCapsLock;
        if ((mod & KMOD_NUM) != 0) locks |= ModNumLock;
        return locks;
    }

    private static string? KittyFunctional((int Code, char Final, string? LegacyText) functional, uint key, ushort mod, bool applicationCursor,
        bool all, KeyEventKind kind, int mods, int locks)
    {
        if (!all)
        {
            // Enter, Tab and Backspace keep their legacy bytes without modifiers, and have no release events.
            if (functional.LegacyText is not null && kind == KeyEventKind.Release) return null;
            if (functional.LegacyText is not null && mods == 0) return functional.LegacyText;
            // Keys with an unambiguous legacy form (arrows, F-keys, Home, …) keep it for plain presses.
            if (functional.Final != 'u' && mods == 0 && kind == KeyEventKind.Press && Legacy(key, mod, applicationCursor) is { } legacy)
                return legacy;
        }
        return Csi(functional.Code, mods | locks, kind, functional.Final);
    }

    /// <summary>Text keys: SDL keycodes are the unshifted character of the key in the current layout.</summary>
    private static string? KittyTextKey(uint key, KittyKeyboardModes flags, KeyEventKind kind, KeyAlternates alternates, string? text,
        bool all, int mods, int locks)
    {
        int textCode = TextKeyCode(key);
        if (textCode < 0) return null;
        if (!all && (mods & (ModAlt | ModCtrl | ModSuper)) == 0) return null; // plain/shifted text: sent as text

        int shifted = 0, baseKey = 0;
        if ((flags & KittyKeyboardModes.ReportAlternateKeys) != 0 && textCode == (int)key)
            (textCode, shifted, baseKey) = AlternateKeys(key, textCode, alternates, mods);
        string? codepoints = (flags & AllKeysWithText) == AllKeysWithText && kind != KeyEventKind.Release ? Codepoints(text) : null;
        return Csi(textCode, mods | locks, kind, 'u', shifted, baseKey, codepoints);
    }

    /// <summary>The key code to report and its shifted and base-layout alternates (0: none) for kitty flag 4.</summary>
    private static (int TextCode, int Shifted, int BaseKey) AlternateKeys(uint key, int textCode, KeyAlternates alternates, int mods)
    {
        int shifted = 0, baseKey = 0;
        // The keycode is latin_letters-mapped: on a non-Latin layout the real key is LayoutKey and the keycode
        // is the base-layout (latin) key.
        if (IsTextChar(alternates.LayoutKey) && alternates.LayoutKey != key)
        {
            textCode = (int)alternates.LayoutKey;
            baseKey = (int)key;
        }
        if ((mods & ModShift) != 0)
        {
            int s = IsTextChar(alternates.ShiftedKey) ? (int)alternates.ShiftedKey : UsShifted(textCode);
            if (s != textCode) shifted = s;
        }
        return (textCode, shifted, baseKey);
    }

    private static bool IsTextChar(uint c) => c is >= 0x20 and < 0x40000000 and not 0x7F;

    /// <summary>Shifted key on a US layout (used when the window system gave none).</summary>
    private static int UsShifted(int c) => c switch
    {
        >= 'a' and <= 'z' => c - 32,
        < 0x80 => ShiftedUs.GetValueOrDefault((char)c, (char)c),
        <= 0xFFFF => char.ToUpperInvariant((char)c),
        _ => c,
    };

    /// <summary>The printable text as colon-separated decimal codepoints, or null if there is none.</summary>
    private static string? Codepoints(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (int value in text.EnumerateRunes().Select(r => r.Value))
        {
            if (value < 0x20 || value is >= 0x7F and < 0xA0) continue; // control characters are not text
            if (sb.Length > 0) sb.Append(':');
            sb.Append(value);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string Csi(int code, int mods, KeyEventKind kind, char final, int shifted, int baseKey, string? text)
    {
        if (shifted == 0 && baseKey == 0 && text is null) return Csi(code, mods, kind, final);
        // CSI code[:shifted[:base]] ; [mods[:event]] [; text] u — the mods field may be empty when only text follows.
        var sb = new System.Text.StringBuilder("\e[").Append(code);
        if (shifted != 0 || baseKey != 0) AppendAlternates(sb, shifted, baseKey);
        bool withMods = mods != 0 || kind != KeyEventKind.Press;
        if (withMods || text is not null)
        {
            sb.Append(';');
            if (withMods) sb.Append(mods + 1);
            if (kind != KeyEventKind.Press) sb.Append(':').Append((int)kind);
        }
        if (text is not null) sb.Append(';').Append(text);
        return sb.Append(final).ToString();
    }

    private static string Csi(int code, int mods, KeyEventKind kind, char final)
    {
        bool withMods = mods != 0 || kind != KeyEventKind.Press;
        string number = code == 1 && !withMods && final != 'u' && final != '~' ? "" : code.ToString();
        if (!withMods) return $"\e[{number}{final}";
        string evt = kind == KeyEventKind.Press ? "" : $":{(int)kind}";
        return $"\e[{code};{mods + 1}{evt}{final}";
    }

    private static void AppendAlternates(System.Text.StringBuilder sb, int shifted, int baseKey)
    {
        sb.Append(':');
        if (shifted != 0) sb.Append(shifted);
        if (baseKey != 0) sb.Append(':').Append(baseKey);
    }

    /// <summary>Kitty number + final byte for functional keys; legacy text for Enter/Tab/Backspace.</summary>
    private static (int Code, char Final, string? LegacyText)? Functional(uint key, bool numLock) => key switch
    {
        Sdl.K_ESCAPE => (27, 'u', null),
        Sdl.K_RETURN => (13, 'u', "\r"),
        Sdl.K_TAB => (9, 'u', "\t"),
        Sdl.K_BACKSPACE => (127, 'u', "\x7f"),
        Sdl.K_INSERT => (2, '~', null),
        Sdl.K_DELETE => (3, '~', null),
        Sdl.K_LEFT => (1, 'D', null),
        Sdl.K_RIGHT => (1, 'C', null),
        Sdl.K_UP => (1, 'A', null),
        Sdl.K_DOWN => (1, 'B', null),
        Sdl.K_PAGEUP => (5, '~', null),
        Sdl.K_PAGEDOWN => (6, '~', null),
        Sdl.K_HOME => (1, 'H', null),
        Sdl.K_END => (1, 'F', null),
        Sdl.K_F1 => (1, 'P', null),
        Sdl.K_F1 + 1 => (1, 'Q', null),
        Sdl.K_F1 + 2 => (13, '~', null),
        Sdl.K_F1 + 3 => (1, 'S', null),
        > Sdl.K_F1 + 3 and <= Sdl.K_F12 => (FunctionKeyCodes[(int)(key - Sdl.K_F1) - 4], '~', null),
        >= K_F13 and <= K_F24 => (57376 + (int)(key - K_F13), 'u', null),
        K_PRINTSCREEN => (57361, 'u', null),
        K_PAUSE => (57362, 'u', null),
        K_APPLICATION or K_MENU => (57363, 'u', null),
        Sdl.K_KP_ENTER => (57414, 'u', null),
        // Without Num Lock the keypad digits are navigation keys (no text).
        K_KP_1 + 4 when !numLock => (1, 'E', null), // KP_BEGIN
        >= K_KP_1 and <= K_KP_PERIOD when !numLock => (KeypadNavigation[(int)(key - K_KP_1)], 'u', null),
        _ => null,
    };

    // KP_1..KP_9, KP_0, KP_PERIOD without Num Lock: End, Down, PageDown, Left, Begin, Right, Home, Up, PageUp, Insert, Delete.
    private static readonly int[] KeypadNavigation = [57424, 57420, 57422, 57417, 57427, 57418, 57423, 57419, 57421, 57425, 57426];

    /// <summary>Kitty code for a text-producing key (keypad keys get their own codes), or -1.</summary>
    private static int TextKeyCode(uint key) => key switch
    {
        >= K_KP_1 and <= K_KP_9 => 57400 + (int)(key - K_KP_1),
        K_KP_0 => 57399,
        K_KP_PERIOD => 57409,
        K_KP_DIVIDE => 57410,
        K_KP_MULTIPLY => 57411,
        K_KP_MINUS => 57412,
        K_KP_PLUS => 57413,
        K_KP_EQUALS => 57415,
        >= 0x20 and < 0x40000000 and not 0x7F => (int)key,
        _ => -1,
    };

    private static bool IsModifierOrLockKey(uint key) =>
        key is >= K_LCTRL and <= K_RGUI or K_CAPSLOCK or K_NUMLOCKCLEAR or K_SCROLLLOCK;

    private static int ModifierKeyCode(uint key) => key switch
    {
        K_CAPSLOCK => 57358,
        K_SCROLLLOCK => 57359,
        K_NUMLOCKCLEAR => 57360,
        // SDL: LCTRL, LSHIFT, LALT, LGUI, RCTRL, RSHIFT, RALT, RGUI → kitty LEFT_CONTROL, LEFT_SHIFT, …
        _ => (int)(key - K_LCTRL) switch
        {
            0 => 57442, 1 => 57441, 2 => 57443, 3 => 57444,
            4 => 57448, 5 => 57447, 6 => 57449, _ => 57450,
        },
    };

    // ---- Legacy (xterm) encoding ------------------------------------------------------------------

    private static string? Legacy(uint key, ushort mod, bool applicationCursor)
    {
        var chord = new LegacyChord(mod, applicationCursor);
        return LegacyNamedKey(key, chord) ?? LegacyCtrlChord(key, chord) ?? LegacyAltChord(key, chord);
    }

    /// <summary>The modifiers of a key and the sequence forms they select.</summary>
    private readonly struct LegacyChord(ushort mod, bool applicationCursor)
    {
        public readonly bool Shift = (mod & Sdl.KMOD_SHIFT) != 0;
        public readonly bool Ctrl = (mod & Sdl.KMOD_CTRL) != 0;
        public readonly bool Alt = (mod & Sdl.KMOD_LALT) != 0;

        public int Parameter => 1 + (Shift ? 1 : 0) + (Alt ? 2 : 0) + (Ctrl ? 4 : 0);

        public string Cursor(char final)
        {
            if (Parameter != 1) return $"\e[1;{Parameter}{final}";
            return applicationCursor ? $"\eO{final}" : $"\e[{final}";
        }

        public string Tilde(int n) => Parameter != 1 ? $"\e[{n};{Parameter}~" : $"\e[{n}~";

        public string Meta(string s) => Alt ? "\e" + s : s;
    }

    private static string? LegacyNamedKey(uint key, in LegacyChord chord) => key switch
    {
        Sdl.K_UP => chord.Cursor('A'),
        Sdl.K_DOWN => chord.Cursor('B'),
        Sdl.K_RIGHT => chord.Cursor('C'),
        Sdl.K_LEFT => chord.Cursor('D'),
        Sdl.K_HOME => chord.Cursor('H'),
        Sdl.K_END => chord.Cursor('F'),
        Sdl.K_INSERT => chord.Tilde(2),
        Sdl.K_DELETE => chord.Tilde(3),
        Sdl.K_PAGEUP => chord.Tilde(5),
        Sdl.K_PAGEDOWN => chord.Tilde(6),
        Sdl.K_RETURN or Sdl.K_KP_ENTER => chord.Meta("\r"),
        Sdl.K_TAB => chord.Shift ? "\e[Z" : chord.Meta("\t"),
        Sdl.K_BACKSPACE => chord.Ctrl ? chord.Meta("\b") : chord.Meta("\x7f"),
        Sdl.K_ESCAPE => chord.Meta("\e"),
        >= Sdl.K_F1 and <= Sdl.K_F12 => LegacyFunctionKey((int)(key - Sdl.K_F1), chord),
        _ => null,
    };

    private static string LegacyFunctionKey(int f, in LegacyChord chord)
    {
        if (f < 4) return chord.Parameter != 1 ? $"\e[1;{chord.Parameter}{(char)('P' + f)}" : $"\eO{(char)('P' + f)}";
        return chord.Tilde(FunctionKeyCodes[f - 4]);
    }

    private static string? LegacyCtrlChord(uint key, in LegacyChord chord)
    {
        if (!chord.Ctrl || key >= 0x80) return null;
        int code = CtrlCode(char.ToLowerInvariant((char)key));
        return code >= 0 ? chord.Meta(((char)code).ToString()) : null;
    }

    private static int CtrlCode(char c) => c switch
    {
        >= 'a' and <= 'z' => c - 'a' + 1,
        ' ' or '@' or '2' => 0,
        '[' or '3' => 0x1B,
        '\\' or '4' => 0x1C,
        ']' or '5' => 0x1D,
        '^' or '6' => 0x1E,
        '_' or '/' or '7' => 0x1F,
        _ => -1,
    };

    private static string? LegacyAltChord(uint key, in LegacyChord chord)
    {
        if (!chord.Alt || key is < 0x20 or >= 0x7F) return null;
        // Keycodes are unmodified, so apply Shift ourselves (US layout) for Meta sequences.
        char c = (char)key;
        if (chord.Shift) c = c is >= 'a' and <= 'z' ? char.ToUpperInvariant(c) : ShiftedUs.GetValueOrDefault(c, c);
        return "\e" + c;
    }
}
