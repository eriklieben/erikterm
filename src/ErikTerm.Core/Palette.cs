namespace ErikTerm.Core;

/// <summary>Which of the OSC 10 / 11 / 12 dynamic colors.</summary>
public enum DynamicColor : byte { Foreground, Background, Cursor }

/// <summary>
/// 256-color palette plus default foreground / background / cursor. Colors are 0xRRGGBB.
/// Two layers: the color scheme's colors (the setters and the indexer, written by the config / <c>:theme</c>) and the
/// colors a program set with OSC 4 / 10 / 11 / 12 (<see cref="SetOverride"/>, <see cref="SetDynamic"/>). The getters
/// return what is shown: a program's color wins over the scheme's until the program resets it (OSC 104 / 110–112) or
/// the terminal is reset (RIS). Changing the scheme therefore keeps colors a program set, as in kitty and wezterm.
/// </summary>
public sealed class Palette
{
    /// <summary>The colors shown (scheme, or a program's override).</summary>
    private readonly uint[] _colors = new uint[256];
    /// <summary>The scheme's colors.</summary>
    private readonly uint[] _scheme = new uint[256];
    private readonly bool[] _overridden = new bool[256];
    private int _overrideCount;

    public uint Foreground { get => _fgOverride ?? _foreground; set { _foreground = value; Version++; } }
    private uint _foreground = 0xD4D7DE;
    public uint Background { get => _bgOverride ?? _background; set { _background = value; Version++; } }
    private uint _background = 0x16181D;
    public uint Cursor { get => _cursorOverride ?? _cursor; set { _cursor = value; Version++; } }
    private uint _cursor = 0xF5C26B;
    private uint? _fgOverride, _bgOverride, _cursorOverride;
    /// <summary>Text color under a block cursor.</summary>
    public uint CursorText { get => _cursorText; set { _cursorText = value; Version++; } }
    private uint _cursorText = 0x16181D;
    public uint Selection { get => _selection; set { _selection = value; Version++; } }
    private uint _selection = 0x3A4150;

    /// <summary>Render bold text in the bright variant of colors 0–7 (classic xterm behaviour).</summary>
    public bool BoldIsBright { get => _boldIsBright; set { _boldIsBright = value; Version++; } }
    private bool _boldIsBright = true;

    /// <summary>Bumped by every change, so renderers can cache resolved colors.</summary>
    public int Version { get; private set; }

    /// <summary>The scheme's background, ignoring an OSC 11 override.</summary>
    public uint SchemeBackground => _background;

    /// <summary>A program changed the background with OSC 11 (so it differs from the window's).</summary>
    public bool HasBackgroundOverride => _bgOverride is not null;

    /// <summary>A program changed any color (OSC 4 / 10 / 11 / 12).</summary>
    public bool HasOverrides => _overrideCount > 0 || _fgOverride is not null || _bgOverride is not null || _cursorOverride is not null;

    public Palette()
    {
        uint[] ansi =
        [
            0x23262E, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xC8CCD4,
            0x5C6370, 0xEF7B85, 0xA9D48A, 0xF0CF8F, 0x7CC0F5, 0xD391EA, 0x6CCAD6, 0xFFFFFF,
        ];
        ansi.CopyTo(_scheme, 0);

        ReadOnlySpan<int> levels = [0, 95, 135, 175, 215, 255];
        for (int i = 0; i < 216; i++)
        {
            int r = levels[i / 36], g = levels[i / 6 % 6], b = levels[i % 6];
            _scheme[16 + i] = (uint)(r << 16 | g << 8 | b);
        }
        for (int i = 0; i < 24; i++)
        {
            int v = 8 + 10 * i;
            _scheme[232 + i] = (uint)(v << 16 | v << 8 | v);
        }
        _scheme.CopyTo(_colors, 0);
    }

    /// <summary>Palette entry as shown. Setting it sets the scheme's color (kept under a program's OSC 4 override).</summary>
    public uint this[int index]
    {
        get => _colors[index & 0xFF];
        set
        {
            int i = index & 0xFF;
            _scheme[i] = value & 0xFFFFFF;
            if (!_overridden[i]) _colors[i] = _scheme[i];
            Version++;
        }
    }

    /// <summary>OSC 4: a program sets palette entry <paramref name="index"/>.</summary>
    public void SetOverride(int index, uint rgb)
    {
        int i = index & 0xFF;
        if (!_overridden[i]) { _overridden[i] = true; _overrideCount++; }
        _colors[i] = rgb & 0xFFFFFF;
        Version++;
    }

    /// <summary>OSC 104 ; n: back to the scheme's color.</summary>
    public void ResetOverride(int index)
    {
        int i = index & 0xFF;
        if (!_overridden[i]) return;
        _overridden[i] = false;
        _overrideCount--;
        _colors[i] = _scheme[i];
        Version++;
    }

    /// <summary>OSC 104 without parameters: every palette entry back to the scheme's.</summary>
    public void ResetOverrides()
    {
        if (_overrideCount == 0) return;
        Array.Clear(_overridden);
        _overrideCount = 0;
        _scheme.CopyTo(_colors, 0);
        Version++;
    }

    /// <summary>OSC 10 / 11 / 12 with a color.</summary>
    public void SetDynamic(DynamicColor which, uint rgb)
    {
        rgb &= 0xFFFFFF;
        switch (which)
        {
            case DynamicColor.Foreground: _fgOverride = rgb; break;
            case DynamicColor.Background: _bgOverride = rgb; break;
            default: _cursorOverride = rgb; break;
        }
        Version++;
    }

    /// <summary>OSC 110 / 111 / 112.</summary>
    public void ResetDynamic(DynamicColor which)
    {
        switch (which)
        {
            case DynamicColor.Foreground: _fgOverride = null; break;
            case DynamicColor.Background: _bgOverride = null; break;
            default: _cursorOverride = null; break;
        }
        Version++;
    }

    public uint GetDynamic(DynamicColor which) => which switch
    {
        DynamicColor.Foreground => Foreground,
        DynamicColor.Background => Background,
        _ => Cursor,
    };

    /// <summary>RIS: drops everything programs set.</summary>
    public void ResetAllOverrides()
    {
        ResetOverrides();
        _fgOverride = _bgOverride = _cursorOverride = null;
        Version++;
    }

    public uint ResolveForeground(uint color, bool bold)
    {
        if (TermColor.IsDefault(color)) return Foreground;
        if (TermColor.IsIndexed(color))
        {
            int i = TermColor.Index(color);
            if (bold && BoldIsBright && i < 8) i += 8;
            return _colors[i];
        }
        return color & 0xFFFFFF;
    }

    public uint ResolveBackground(uint color)
    {
        if (TermColor.IsDefault(color)) return Background;
        if (TermColor.IsIndexed(color)) return _colors[TermColor.Index(color)];
        return color & 0xFFFFFF;
    }

    /// <summary>
    /// The background reads as dark: DECSET 2031 / <c>CSI ? 996 n</c> report 1 (else 2). Same test as Neovim applies to
    /// an OSC 11 reply (Rec. 601 luma below one half), so both agree on 'background'.
    /// </summary>
    public bool IsDark => Luma(Background) < 0.5;

    /// <summary>Rec. 601 luma of 0xRRGGBB, 0–1.</summary>
    public static double Luma(uint rgb) =>
        (0.299 * (rgb >> 16 & 0xFF) + 0.587 * (rgb >> 8 & 0xFF) + 0.114 * (rgb & 0xFF)) / 255.0;
}
