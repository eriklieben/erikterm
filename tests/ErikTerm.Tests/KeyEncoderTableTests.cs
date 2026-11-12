using ErikTerm.App;
using ErikTerm.App.Native;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>
/// The key encoder's tables key by key: xterm's legacy forms for the named, Ctrl and Alt chords, and the kitty codes
/// of the functional, keypad and modifier keys that <see cref="KittyKeyboardTests"/> doesn't try.
/// </summary>
// Internal: the encoder's KeyEventKind (an app type) is a parameter.
internal class KeyEncoderTableTests
{
    private const ushort None = 0, Shift = 0x0001, Ctrl = 0x0040, Alt = 0x0100, NumLock = 0x1000, CapsLock = 0x2000;
    private const KittyKeyboardModes D = KittyKeyboardModes.Disambiguate, All = KittyKeyboardModes.ReportAllKeys;
    private const KittyKeyboardModes DAlt4 = D | KittyKeyboardModes.ReportAlternateKeys;

    private const uint F2 = Sdl.K_F1 + 1, F4 = Sdl.K_F1 + 3, F5 = Sdl.K_F1 + 4, F6 = Sdl.K_F1 + 5, F24 = 0x40000073;
    private const uint PrintScreen = 0x40000046, ScrollLock = 0x40000047, Pause = 0x40000048, NumLockKey = 0x40000053;
    private const uint KpDivide = 0x40000054, KpMultiply = 0x40000055, KpMinus = 0x40000056, Kp1 = 0x40000059;
    private const uint Kp0 = 0x40000062, KpPeriod = 0x40000063, Application = 0x40000065, KpEquals = 0x40000067, Menu = 0x40000076;
    private const uint LeftCtrl = 0x400000E0, LeftAlt = 0x400000E2, LeftSuper = 0x400000E3, RightShift = 0x400000E5;
    private const uint RightAlt = 0x400000E6, RightSuper = 0x400000E7;

    [Test]
    [Arguments("\e[1;2A", Sdl.K_UP, Shift, false)]
    [Arguments("\e[1;3B", Sdl.K_DOWN, Alt, false)]
    [Arguments("\e[D", Sdl.K_LEFT, None, false)]
    [Arguments("\e[H", Sdl.K_HOME, None, false)]
    [Arguments("\eOF", Sdl.K_END, None, true)]
    [Arguments("\e[1;5F", Sdl.K_END, Ctrl, true)]   // a modified cursor key is CSI also in application mode
    [Arguments("\e[2~", Sdl.K_INSERT, None, false)]
    [Arguments("\e[2;2~", Sdl.K_INSERT, Shift, false)]
    [Arguments("\e[3~", Sdl.K_DELETE, None, false)]
    [Arguments("\e[5~", Sdl.K_PAGEUP, None, false)]
    [Arguments("\e[6;5~", Sdl.K_PAGEDOWN, Ctrl, false)]
    [Arguments("\r", Sdl.K_KP_ENTER, None, false)]
    [Arguments("\e\r", Sdl.K_RETURN, Alt, false)]
    [Arguments("\t", Sdl.K_TAB, None, false)]
    [Arguments("\e[Z", Sdl.K_TAB, Shift, false)]
    [Arguments("\e\t", Sdl.K_TAB, Alt, false)]
    [Arguments("\x7f", Sdl.K_BACKSPACE, None, false)]
    [Arguments("\b", Sdl.K_BACKSPACE, Ctrl, false)]
    [Arguments("\e\x7f", Sdl.K_BACKSPACE, Alt, false)]
    [Arguments("\e\b", Sdl.K_BACKSPACE, (ushort)(Ctrl | Alt), false)]
    [Arguments("\e\e", Sdl.K_ESCAPE, Alt, false)]
    [Arguments("\eOP", Sdl.K_F1, None, false)]
    [Arguments("\e[1;2Q", F2, Shift, false)]
    [Arguments("\eOS", F4, None, false)]
    [Arguments("\e[15~", F5, None, false)]
    [Arguments("\e[17;5~", F6, Ctrl, false)]
    [Arguments("\e[24~", Sdl.K_F12, None, false)]
    public async Task LegacyNamedKeys(string expected, uint key, ushort mod, bool applicationCursor)
    {
        // Act
        string? seq = KeyEncoder.Encode(key, mod, applicationCursor);

        // Assert
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\0", (uint)' ')]
    [Arguments("\0", (uint)'@')]
    [Arguments("\0", (uint)'2')]
    [Arguments("\e", (uint)'[')]
    [Arguments("\e", (uint)'3')]
    [Arguments("\x1c", (uint)'\\')]
    [Arguments("\x1c", (uint)'4')]
    [Arguments("\x1d", (uint)']')]
    [Arguments("\x1d", (uint)'5')]
    [Arguments("\x1e", (uint)'^')]
    [Arguments("\x1e", (uint)'6')]
    [Arguments("\x1f", (uint)'_')]
    [Arguments("\x1f", (uint)'/')]
    [Arguments("\x1f", (uint)'7')]
    [Arguments("\x1a", (uint)'Z')]
    public async Task LegacyCtrlChordsFollowXterm(string expected, uint key)
    {
        // Act
        string? seq = KeyEncoder.Encode(key, Ctrl, false);

        // Assert
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    [Arguments((uint)'1', Ctrl)]          // Ctrl+1 has no control code
    [Arguments(0xE9u, Ctrl)]              // é: not ASCII
    [Arguments(0x1Fu, Alt)]               // a control character is no Meta key
    [Arguments((uint)'a', Shift)]         // text: it comes from the text-input event
    public async Task LegacyChordsWithoutAnEncodingSendNothing(uint key, ushort mod)
    {
        // Act
        string? seq = KeyEncoder.Encode(key, mod, false);

        // Assert
        await Assert.That(seq).IsNull();
    }

    [Test]
    [Arguments("\e\x01", (uint)'a', (ushort)(Ctrl | Alt))]
    [Arguments("\eA", (uint)'a', (ushort)(Alt | Shift))]
    [Arguments("\e!", (uint)'1', (ushort)(Alt | Shift))]
    [Arguments("\e ", (uint)' ', (ushort)(Alt | Shift))]   // no shifted form: the key itself
    public async Task LegacyAltChordsApplyCtrlAndShift(string expected, uint key, ushort mod)
    {
        // Act
        string? seq = KeyEncoder.Encode(key, mod, false);

        // Assert
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    public async Task LegacyReleasesSendNothing()
    {
        // Act
        string? seq = KeyEncoder.Encode(Sdl.K_UP, None, false, KittyKeyboardModes.None, KeyEventKind.Release);

        // Assert
        await Assert.That(seq).IsNull();
    }

    [Test]
    [Arguments("\e[2;5~", Sdl.K_INSERT, Ctrl)]
    [Arguments("\e[5;5~", Sdl.K_PAGEUP, Ctrl)]
    [Arguments("\e[1;5F", Sdl.K_END, Ctrl)]
    [Arguments("\e[1;5D", Sdl.K_LEFT, Ctrl)]
    [Arguments("\e[1;5Q", F2, Ctrl)]
    [Arguments("\e[1;5S", F4, Ctrl)]
    [Arguments("\e[24;5~", Sdl.K_F12, Ctrl)]
    [Arguments("\e[57387u", F24, None)]
    [Arguments("\e[57361u", PrintScreen, None)]
    [Arguments("\e[57362u", Pause, None)]
    [Arguments("\e[57363u", Application, None)]
    [Arguments("\e[57363u", Menu, None)]
    [Arguments("\e[57424u", Kp1, None)]       // without Num Lock: End
    [Arguments("\e[57425u", Kp0, None)]       // Insert
    [Arguments("\e[57426u", KpPeriod, None)]  // Delete
    [Arguments("\e[57399;5u", Kp0, (ushort)(Ctrl | NumLock))]
    [Arguments("\e[57409;5u", KpPeriod, (ushort)(Ctrl | NumLock))]
    [Arguments("\e[57410;5u", KpDivide, Ctrl)]
    [Arguments("\e[57411;5u", KpMultiply, Ctrl)]
    [Arguments("\e[57412;5u", KpMinus, Ctrl)]
    [Arguments("\e[57415;5u", KpEquals, Ctrl)]
    public async Task KittyFunctionalAndKeypadCodes(string expected, uint key, ushort mod)
    {
        // Act
        string? seq = KeyEncoder.Encode(key, mod, false, D);

        // Assert
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    public async Task KittyKeyWithNoCodeSendsNothing()
    {
        // Act: a keycode past every table (no text, no functional key)
        string? seq = KeyEncoder.Encode(0x40000080, Ctrl, false, D);

        // Assert
        await Assert.That(seq).IsNull();
    }

    [Test]
    [Arguments("\e[57359u", ScrollLock, None)]
    [Arguments("\e[57360;129u", NumLockKey, NumLock)]
    [Arguments("\e[57442;5u", LeftCtrl, Ctrl)]
    [Arguments("\e[57443u", LeftAlt, None)]
    [Arguments("\e[57444u", LeftSuper, None)]
    [Arguments("\e[57447u", RightShift, None)]
    [Arguments("\e[57449u", RightAlt, None)]
    [Arguments("\e[57450u", RightSuper, None)]
    [Arguments("\e[27;65u", Sdl.K_ESCAPE, CapsLock)]   // locks are reported in all-keys mode
    public async Task KittyAllKeysReportsModifierAndLockKeys(string expected, uint key, ushort mod)
    {
        // Act
        string? seq = KeyEncoder.Encode(key, mod, false, All);

        // Assert
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\e[233:201;6u", 0xE9u)]         // é: its capital from Unicode
    [Arguments("\e[128512;6u", 0x1F600u)]      // an emoji has no shifted form
    [Arguments("\e[32;6u", (uint)' ')]          // nor has space
    public async Task KittyShiftedAlternateWithoutLayoutInfo(string expected, uint key)
    {
        // Act
        string? seq = KeyEncoder.Encode(key, (ushort)(Ctrl | Shift), false, DAlt4);

        // Assert
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    public async Task KittyAlternatesWithAControlLayoutKeyKeepTheKeycode()
    {
        // Arrange: a layout key that isn't text (a control character) is no alternate
        var alternates = new KeyAlternates(0x1F, 0);

        // Act
        string? seq = KeyEncoder.Encode('a', Ctrl, false, DAlt4, KeyEventKind.Press, alternates);

        // Assert
        await Assert.That(seq).IsEqualTo("\e[97;5u");
    }
}
