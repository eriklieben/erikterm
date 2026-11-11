namespace ErikTerm.Core;

/// <summary>
/// The modes that change what the terminal sends rather than how it draws: cursor keys and keypad (DECCKM, DECKPAM),
/// mouse reporting (DECSET 1000 / 1002 / 1003 / 1006 / 1007), focus events (1004), bracketed paste (2004) and the kitty
/// keyboard protocol's flag stacks (one per screen). The app reads them when it encodes input.
/// </summary>
internal sealed class InputModes
{
    private readonly KittyKeyboardStack _keyboardMain = new(), _keyboardAlt = new();

    public bool CursorKeysApplication { get; private set; }
    public bool KeypadApplication { get; set; }
    public bool BracketedPaste { get; private set; }
    public bool FocusReporting { get; private set; }
    public bool AlternateScroll { get; private set; } = true;
    public MouseTracking MouseTracking { get; private set; }
    public bool MouseSgr { get; private set; }

    /// <summary>The kitty keyboard flag stack of the main or the alternate screen.</summary>
    public KittyKeyboardStack Keyboard(bool alt) => alt ? _keyboardAlt : _keyboardMain;

    /// <summary>The DEC private modes this class keeps.</summary>
    public static bool Owns(int mode) => mode is 1 or 1000 or 1002 or 1003 or 1004 or 1006 or 1007 or 2004;

    /// <summary>DECSET / DECRST of a mode <see cref="Owns"/> is true for.</summary>
    public void Set(int mode, bool on)
    {
        switch (mode)
        {
            case 1: CursorKeysApplication = on; break;
            case 1000 or 1002 or 1003: MouseTracking = on ? MouseModeTracking(mode) : MouseTracking.None; break;
            case 1004: FocusReporting = on; break;
            case 1006: MouseSgr = on; break;
            case 1007: AlternateScroll = on; break;
            case 2004: BracketedPaste = on; break;
        }
    }

    private static MouseTracking MouseModeTracking(int mode) => mode switch
    {
        1000 => MouseTracking.Press,
        1002 => MouseTracking.ButtonMotion,
        _ => MouseTracking.AnyMotion,
    };

    /// <summary>DECRQM: whether a mode <see cref="Owns"/> is true for is set.</summary>
    public bool IsSet(int mode) => mode switch
    {
        1 => CursorKeysApplication,
        1000 or 1002 or 1003 => MouseTracking == MouseModeTracking(mode),
        1004 => FocusReporting,
        1006 => MouseSgr,
        1007 => AlternateScroll,
        _ => BracketedPaste,
    };

    /// <summary>DECSTR: cursor keys and keypad back to normal.</summary>
    public void SoftReset()
    {
        CursorKeysApplication = false;
        KeypadApplication = false;
    }

    /// <summary>RIS / a new program: no mouse, focus or paste reporting, legacy keyboard encoding.</summary>
    public void Reset()
    {
        BracketedPaste = FocusReporting = MouseSgr = false;
        AlternateScroll = true;
        MouseTracking = MouseTracking.None;
        _keyboardMain.Reset();
        _keyboardAlt.Reset();
    }
}
