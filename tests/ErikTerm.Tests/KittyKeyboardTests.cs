using System.Text;
using ErikTerm.App;
using ErikTerm.App.Native;
using ErikTerm.Core;

namespace ErikTerm.Tests;

// Internal: the encoder's KeyEventKind (an app type) is a test parameter.
internal class KittyKeyboardTests
{
    private const ushort Shift = 0x0001, Ctrl = 0x0040, Alt = 0x0100, Super = 0x0400, NumLock = 0x1000, CapsLock = 0x2000;
    private const ushort CtrlShift = Ctrl | Shift;
    private const uint LeftShiftKey = 0x400000E1, RightCtrlKey = 0x400000E4, CapsLockKey = 0x40000039;
    private const uint Kp1 = 0x40000059, Kp5 = 0x4000005D, Kp8 = 0x40000060, KpPlus = 0x40000057, F3 = Sdl.K_F1 + 2, F13 = 0x40000068;

    private static (Terminal, List<string>) Term()
    {
        var t = new Terminal(10, 3);
        var replies = new List<string>();
        t.Response = d => replies.Add(Encoding.ASCII.GetString(d));
        return (t, replies);
    }

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.ASCII.GetBytes(s));

    // ---- Mode stack ---------------------------------------------------------------------------
    // The stack tests feed a sequence and record the flags after every step (each step depends on the one
    // before), then check the whole history.

    [Test]
    public async Task QueryRepliesWithCurrentFlags()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, "\e[?u");
        Feed(t, "\e[>1u\e[?u");
        Feed(t, "\e[>11u\e[?u");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[?0u", "\e[?1u", "\e[?11u"], CollectionOrdering.Matching);
        await Assert.That(t.KeyboardFlags)
            .IsEqualTo(KittyKeyboardModes.Disambiguate | KittyKeyboardModes.ReportEventTypes | KittyKeyboardModes.ReportAllKeys);
    }

    [Test]
    public async Task PushAndPop()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, "\e[>1u\e[>3u\e[>8u");
        var afterThreePushes = t.KeyboardFlags;
        Feed(t, "\e[<u");
        var afterPop = t.KeyboardFlags;
        Feed(t, "\e[<2u");
        var afterPopTwo = t.KeyboardFlags;
        Feed(t, "\e[>1u\e[<10u"); // popping more than pushed resets
        var afterOverPop = t.KeyboardFlags;
        Feed(t, "\e[>u"); // push with flags omitted = 0
        var afterEmptyPush = t.KeyboardFlags;

        // Assert
        await Assert.That(afterThreePushes).IsEqualTo(KittyKeyboardModes.ReportAllKeys);
        await Assert.That(afterPop).IsEqualTo((KittyKeyboardModes)3);
        await Assert.That(afterPopTwo).IsEqualTo(KittyKeyboardModes.None);
        await Assert.That(afterOverPop).IsEqualTo(KittyKeyboardModes.None);
        await Assert.That(afterEmptyPush).IsEqualTo(KittyKeyboardModes.None);
    }

    [Test]
    public async Task SetModes()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, "\e[=3u");
        var afterSet = t.KeyboardFlags;
        Feed(t, "\e[=8;2u"); // or
        var afterOr = t.KeyboardFlags;
        Feed(t, "\e[=2;3u"); // and-not
        var afterAndNot = t.KeyboardFlags;
        Feed(t, "\e[=1;1u"); // replace
        var afterReplace = t.KeyboardFlags;

        // Assert
        await Assert.That(afterSet).IsEqualTo((KittyKeyboardModes)3);
        await Assert.That(afterOr).IsEqualTo((KittyKeyboardModes)11);
        await Assert.That(afterAndNot).IsEqualTo((KittyKeyboardModes)9);
        await Assert.That(afterReplace).IsEqualTo(KittyKeyboardModes.Disambiguate);
    }

    [Test]
    public async Task UnsupportedFlagsAreNotReported()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, "\e[>255u\e[?u");
        Feed(t, "\e[=4u\e[?u");
        Feed(t, "\e[=16;2u\e[?u");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[?31u", "\e[?4u", "\e[?20u"], CollectionOrdering.Matching); // all five flags are implemented
    }

    [Test]
    public async Task MainAndAlternateScreensHaveSeparateStacks()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, "\e[>1u\e[?1049h");
        var onEnteringAlternate = t.KeyboardFlags;
        Feed(t, "\e[>8u");
        var afterPushOnAlternate = t.KeyboardFlags;
        Feed(t, "\e[?1049l");
        var backOnMain = t.KeyboardFlags;

        // Assert
        await Assert.That(onEnteringAlternate).IsEqualTo(KittyKeyboardModes.None);
        await Assert.That(afterPushOnAlternate).IsEqualTo(KittyKeyboardModes.ReportAllKeys);
        await Assert.That(backOnMain).IsEqualTo(KittyKeyboardModes.Disambiguate);
    }

    [Test]
    public async Task FullStackEvictsOldest()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[>1u");
        for (int i = 0; i < 16; i++) Feed(t, "\e[>2u");

        // Act
        Feed(t, "\e[<15u");
        var afterPoppingFifteen = t.KeyboardFlags;
        Feed(t, "\e[<u");
        var afterPoppingTheLast = t.KeyboardFlags;

        // Assert
        await Assert.That(afterPoppingFifteen).IsEqualTo(KittyKeyboardModes.ReportEventTypes); // the 1 was evicted
        await Assert.That(afterPoppingTheLast).IsEqualTo(KittyKeyboardModes.None);
    }

    [Test]
    public async Task ResetClearsFlags()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, "\e[>1u\ec");

        // Assert
        await Assert.That(t.KeyboardFlags).IsEqualTo(KittyKeyboardModes.None);
    }

    [Test]
    public async Task PlainCsiUStillRestoresCursor()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[>1u\ec");

        // Act
        Feed(t, "\e[2;3H\e[s\e[H\e[u");

        // Assert
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((2, 1));
    }

    // ---- Encoding -----------------------------------------------------------------------------

    internal const KittyKeyboardModes D = KittyKeyboardModes.Disambiguate;
    internal const KittyKeyboardModes E = KittyKeyboardModes.ReportEventTypes;
    internal const KittyKeyboardModes All = KittyKeyboardModes.ReportAllKeys;
    internal const KittyKeyboardModes DE = D | E;
    internal const KittyKeyboardModes AllE = All | E;

    private static string? Enc(uint key, ushort mod = 0, KittyKeyboardModes f = 0, KeyEventKind kind = KeyEventKind.Press, bool appCursor = false) =>
        KeyEncoder.Encode(key, mod, appCursor, f, kind);

    [Test]
    [Arguments("\e[A", Sdl.K_UP, (ushort)0, false)]
    [Arguments("\eOA", Sdl.K_UP, (ushort)0, true)]
    [Arguments("\e[1;5C", Sdl.K_RIGHT, Ctrl, false)]
    [Arguments("\x01", (uint)'a', Ctrl, false)]
    [Arguments("\ea", (uint)'a', Alt, false)]
    [Arguments("\e", Sdl.K_ESCAPE, (ushort)0, false)]
    [Arguments("\r", Sdl.K_RETURN, (ushort)0, false)]
    [Arguments(null, (uint)'a', (ushort)0, false)]
    public async Task LegacyEncodingIsUnchangedWithoutFlags(string? expected, uint key, ushort mod, bool appCursor)
    {
        // Act
        string? encoded = Enc(key, mod, appCursor: appCursor);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    public async Task LegacyEncodingHasNoReleaseEvents()
    {
        // Act
        string? encoded = Enc(Sdl.K_UP, kind: KeyEventKind.Release);

        // Assert
        await Assert.That(encoded).IsNull();
    }

    [Test]
    [Arguments("\e[27u", Sdl.K_ESCAPE, (ushort)0)]
    [Arguments("\e[97;5u", (uint)'a', Ctrl)]
    [Arguments("\e[97;3u", (uint)'a', Alt)]
    [Arguments("\e[97;6u", (uint)'a', CtrlShift)]
    [Arguments("\e[105;5u", (uint)'i', Ctrl)] // no longer the same as Tab
    [Arguments("\e[32;5u", (uint)' ', Ctrl)]
    [Arguments("\e[97;9u", (uint)'a', Super)]
    [Arguments(null, (uint)'a', (ushort)0)] // text goes via text input
    [Arguments(null, (uint)'a', Shift)]     // so does shifted text
    [Arguments(null, (uint)'a', CapsLock)]
    public async Task DisambiguateEncodesModifiedTextKeysAndEscape(string? expected, uint key, ushort mod)
    {
        // Act
        string? encoded = Enc(key, mod, D);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\r", Sdl.K_RETURN, (ushort)0)]
    [Arguments("\t", Sdl.K_TAB, (ushort)0)]
    [Arguments("\x7f", Sdl.K_BACKSPACE, (ushort)0)]
    [Arguments("\e[13;2u", Sdl.K_RETURN, Shift)]
    [Arguments("\e[9;5u", Sdl.K_TAB, Ctrl)]
    [Arguments("\e[127;3u", Sdl.K_BACKSPACE, Alt)]
    [Arguments("\e[A", Sdl.K_UP, (ushort)0)]
    [Arguments("\e[A", Sdl.K_UP, NumLock)] // lock modifiers are not reported here
    [Arguments("\e[1;5A", Sdl.K_UP, Ctrl)]
    [Arguments("\e[1;2H", Sdl.K_HOME, Shift)]
    [Arguments("\e[3;5~", Sdl.K_DELETE, Ctrl)]
    [Arguments("\e[1;5P", Sdl.K_F1, Ctrl)]
    [Arguments("\e[13;5~", F3, Ctrl)]
    [Arguments("\e[15~", Sdl.K_F1 + 4, (ushort)0)]
    [Arguments("\e[57376u", F13, (ushort)0)]
    [Arguments("\e[57414u", Sdl.K_KP_ENTER, (ushort)0)]
    public async Task DisambiguateKeepsLegacyEnterTabBackspaceAndPlainFunctionalKeys(string expected, uint key, ushort mod)
    {
        // Act
        string? encoded = Enc(key, mod, D);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\e[57419u", Kp8, (ushort)0)]
    [Arguments("\e[E", Kp5, (ushort)0)]
    [Arguments("\e[1;5E", Kp5, Ctrl)]
    [Arguments(null, Kp8, NumLock)] // with Num Lock it types a digit
    [Arguments("\e[57413;5u", KpPlus, Ctrl)]
    public async Task KeypadWithoutNumLockReportsNavigationKeys(string? expected, uint key, ushort mod)
    {
        // Act
        string? encoded = Enc(key, mod, D);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, LeftShiftKey, Shift, D)]
    [Arguments("\e[57441;2u", LeftShiftKey, Shift, All)]
    [Arguments("\e[57448;5u", RightCtrlKey, Ctrl, All)]
    [Arguments("\e[57358;65u", CapsLockKey, CapsLock, All)]
    public async Task ModifierKeysAreSilentUnlessReportingAllKeys(string? expected, uint key, ushort mod, KittyKeyboardModes flags)
    {
        // Act
        string? encoded = Enc(key, mod, flags);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\e[97;5:3u", (uint)'a', Ctrl, DE, KeyEventKind.Release)]
    [Arguments("\e[97;5:2u", (uint)'a', Ctrl, DE, KeyEventKind.Repeat)]
    [Arguments("\e[1;1:3A", Sdl.K_UP, (ushort)0, DE, KeyEventKind.Release)]
    [Arguments("\e[1;1:2A", Sdl.K_UP, (ushort)0, DE, KeyEventKind.Repeat)]
    [Arguments("\e[27;1:3u", Sdl.K_ESCAPE, (ushort)0, DE, KeyEventKind.Release)]
    [Arguments("\e[13;5:3~", F3, Ctrl, DE, KeyEventKind.Release)]
    // Text keys and Enter/Tab/Backspace have no release events without "report all keys".
    [Arguments(null, (uint)'a', (ushort)0, DE, KeyEventKind.Release)]
    [Arguments(null, Sdl.K_RETURN, (ushort)0, DE, KeyEventKind.Release)]
    // Without the event-type flag releases are dropped and repeats are presses.
    [Arguments(null, (uint)'a', Ctrl, D, KeyEventKind.Release)]
    [Arguments("\e[97;5u", (uint)'a', Ctrl, D, KeyEventKind.Repeat)]
    public async Task EventTypes(string? expected, uint key, ushort mod, KittyKeyboardModes flags, KeyEventKind kind)
    {
        // Act
        string? encoded = Enc(key, mod, flags, kind);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\e[97u", (uint)'a', (ushort)0, All, KeyEventKind.Press)]
    [Arguments("\e[97;2u", (uint)'a', Shift, All, KeyEventKind.Press)]
    [Arguments("\e[49u", (uint)'1', (ushort)0, All, KeyEventKind.Press)]
    [Arguments("\e[13u", Sdl.K_RETURN, (ushort)0, All, KeyEventKind.Press)]
    [Arguments("\e[9u", Sdl.K_TAB, (ushort)0, All, KeyEventKind.Press)]
    [Arguments("\e[127u", Sdl.K_BACKSPACE, (ushort)0, All, KeyEventKind.Press)]
    [Arguments("\e[97;65u", (uint)'a', CapsLock, All, KeyEventKind.Press)]
    [Arguments("\e[1;129A", Sdl.K_UP, NumLock, All, KeyEventKind.Press)]
    [Arguments("\e[A", Sdl.K_UP, (ushort)0, All, KeyEventKind.Press)]
    [Arguments("\e[57400;129u", Kp1, NumLock, All, KeyEventKind.Press)] // KP_1 → its own code, Num Lock reported
    [Arguments("\e[97;1:3u", (uint)'a', (ushort)0, AllE, KeyEventKind.Release)]
    [Arguments("\e[13;1:3u", Sdl.K_RETURN, (ushort)0, AllE, KeyEventKind.Release)]
    [Arguments("\e[57441;1:3u", LeftShiftKey, (ushort)0, AllE, KeyEventKind.Release)]
    public async Task ReportAllKeysAsEscapeCodes(string expected, uint key, ushort mod, KittyKeyboardModes flags, KeyEventKind kind)
    {
        // Act
        string? encoded = Enc(key, mod, flags, kind);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    // ---- Alternate keys (4) and associated text (16) ---------------------------------------------

    internal const KittyKeyboardModes Alt4 = KittyKeyboardModes.ReportAlternateKeys;
    internal const KittyKeyboardModes Text16 = KittyKeyboardModes.ReportAssociatedText;
    internal const KittyKeyboardModes DAlt4 = D | Alt4;
    internal const KittyKeyboardModes AllAlt4 = All | Alt4;
    internal const KittyKeyboardModes AllText16 = All | Text16;
    internal const KittyKeyboardModes DText16 = D | Text16;

    private static string? EncAlt(uint key, ushort mod, KittyKeyboardModes f, KeyAlternates alt, string? text = null,
        KeyEventKind kind = KeyEventKind.Press) => KeyEncoder.Encode(key, mod, false, f, kind, alt, text);

    // Alternates as (layout key, shifted key); (0, 0) is `default`: the window system reported none.
    [Test]
    [Arguments("\e[97:65;6u", (uint)'a', CtrlShift, DAlt4, (uint)'a', (uint)'A')]
    [Arguments("\e[97;5u", (uint)'a', Ctrl, DAlt4, (uint)'a', (uint)'A')] // no shift: nothing extra
    [Arguments("\e[49:33;6u", (uint)'1', CtrlShift, DAlt4, (uint)'1', (uint)'!')]
    // Without the window system's shifted key, the US layout is assumed.
    [Arguments("\e[50:64;6u", (uint)'2', CtrlShift, DAlt4, 0u, 0u)]
    [Arguments("\e[97:65;2u", (uint)'a', Shift, AllAlt4, (uint)'a', (uint)'A')]
    // Without flag 4 the alternates are ignored.
    [Arguments("\e[97;6u", (uint)'a', CtrlShift, D, (uint)'a', (uint)'A')]
    public async Task AlternateKeysReportShiftedKeyOnlyWithShift(string expected, uint key, ushort mod, KittyKeyboardModes flags, uint layoutKey, uint shiftedKey)
    {
        // Act
        string? encoded = EncAlt(key, mod, flags, new KeyAlternates(layoutKey, shiftedKey));

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    // Cyrillic: the key labelled ф is "a" on a US layout; SDL's latin_letters keycode is 'a'.
    [Test]
    [Arguments("\e[1092::97;5u", Ctrl, DAlt4)]
    [Arguments("\e[1092:1060:97;6u", CtrlShift, DAlt4)]
    [Arguments("\e[97;5u", Ctrl, D)] // flag 4 off: latin key only
    public async Task AlternateKeysReportBaseLayoutKeyOnNonLatinLayouts(string expected, ushort mod, KittyKeyboardModes flags)
    {
        // Arrange
        var ef = new KeyAlternates(0x444, 0x424);

        // Act
        string? encoded = EncAlt('a', mod, flags, ef);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    public async Task FunctionalKeysHaveNoAlternates()
    {
        // Act
        string? encoded = EncAlt(Sdl.K_UP, Shift, D | Alt4, new KeyAlternates(Sdl.K_UP, Sdl.K_UP));

        // Assert
        await Assert.That(encoded).IsEqualTo("\e[1;2A");
    }

    [Test]
    public async Task AlternateKeysOnReleaseEvents()
    {
        // Act
        string? encoded = EncAlt('a', Shift, All | E | Alt4, new KeyAlternates('a', 'A'), kind: KeyEventKind.Release);

        // Assert
        await Assert.That(encoded).IsEqualTo("\e[97:65;2:3u");
    }

    [Test]
    [Arguments(true, (uint)'a', (ushort)0, AllText16, KeyEventKind.Press)]
    [Arguments(true, (uint)'a', Shift, AllText16, KeyEventKind.Repeat)]
    [Arguments(false, (uint)'a', (ushort)0, DText16, KeyEventKind.Press)]
    [Arguments(false, (uint)'a', (ushort)0, All, KeyEventKind.Press)]
    [Arguments(false, (uint)'a', (ushort)0, AllText16, KeyEventKind.Release)]
    [Arguments(false, (uint)'a', Ctrl, AllText16, KeyEventKind.Press)]
    [Arguments(false, Sdl.K_RETURN, (ushort)0, AllText16, KeyEventKind.Press)]
    [Arguments(false, LeftShiftKey, Shift, AllText16, KeyEventKind.Press)]
    [Arguments(true, Kp8, NumLock, AllText16, KeyEventKind.Press)]
    [Arguments(false, Kp8, (ushort)0, AllText16, KeyEventKind.Press)] // navigation key
    public async Task AssociatedTextNeedsReportAllKeys(bool expected, uint key, ushort mod, KittyKeyboardModes flags, KeyEventKind kind)
    {
        // Act
        bool wants = KeyEncoder.WantsText(key, mod, flags, kind);

        // Assert
        await Assert.That(wants).IsEqualTo(expected);
    }

    internal const KittyKeyboardModes AllAlt4Text16 = All | Alt4 | Text16;
    internal const KittyKeyboardModes AllEText16 = All | E | Text16;

    [Test]
    [Arguments("\e[97;;97u", (uint)'a', (ushort)0, AllText16, "a", KeyEventKind.Press)]
    [Arguments("\e[97;2;65u", (uint)'a', Shift, AllText16, "A", KeyEventKind.Press)]
    [Arguments("\e[97;1:2;97u", (uint)'a', (ushort)0, AllEText16, "a", KeyEventKind.Repeat)]
    [Arguments("\e[101;;233u", (uint)'e', (ushort)0, AllText16, "é", KeyEventKind.Press)] // dead key / compose
    [Arguments("\e[97;;128512:97u", (uint)'a', (ushort)0, AllText16, "\U0001F600a", KeyEventKind.Press)]
    [Arguments("\e[57400;129;49u", Kp1, NumLock, AllText16, "1", KeyEventKind.Press)] // KP_1
    // No text: no third field. Releases never carry text; neither do control characters.
    [Arguments("\e[97u", (uint)'a', (ushort)0, AllText16, null, KeyEventKind.Press)]
    [Arguments("\e[97;1:3u", (uint)'a', (ushort)0, AllEText16, "a", KeyEventKind.Release)]
    [Arguments("\e[97u", (uint)'a', (ushort)0, AllText16, "\x01", KeyEventKind.Press)]
    // Without flag 8 the text is not reported (flag 16 is undefined on its own).
    [Arguments("\e[97;5u", (uint)'a', Ctrl, DText16, "a", KeyEventKind.Press)]
    public async Task AssociatedTextIsAppendedAsCodepoints(string expected, uint key, ushort mod, KittyKeyboardModes flags, string? text, KeyEventKind kind)
    {
        // Act
        string? encoded = EncAlt(key, mod, flags, default, text, kind);

        // Assert
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    public async Task AssociatedTextWithAlternateKeys()
    {
        // Act
        string? encoded = EncAlt('a', Shift, AllAlt4Text16, new KeyAlternates('a', 'A'), "A");

        // Assert
        await Assert.That(encoded).IsEqualTo("\e[97:65;2;65u");
    }
}
