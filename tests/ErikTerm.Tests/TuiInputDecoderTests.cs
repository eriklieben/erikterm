using System.Text;
using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>The compare screens' input decoder: every key form, mouse report, OSC reply and broken input.</summary>
public class TuiInputDecoderTests
{
    private static List<TuiEvent> Decode(params string[] chunks)
    {
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();
        foreach (var chunk in chunks) decoder.Feed(Encoding.UTF8.GetBytes(chunk), events);
        return events;
    }

    private static List<TuiEvent> DecodeBytes(params byte[] bytes)
    {
        var events = new List<TuiEvent>();
        new TuiInputDecoder().Feed(bytes, events);
        return events;
    }

    [Test]
    [Arguments("\eOA", TuiKey.Up)]
    [Arguments("\eOB", TuiKey.Down)]
    [Arguments("\eOH", TuiKey.Home)]
    [Arguments("\e[C", TuiKey.Right)]
    [Arguments("\e[F", TuiKey.End)]
    [Arguments("\e[Z", TuiKey.BackTab)]
    [Arguments("\e[1~", TuiKey.Home)]
    [Arguments("\e[7~", TuiKey.Home)]
    [Arguments("\e[4~", TuiKey.End)]
    [Arguments("\e[8~", TuiKey.End)]
    [Arguments("\e[3~", TuiKey.Delete)]
    [Arguments("\e[5~", TuiKey.PageUp)]
    [Arguments("\e[6~", TuiKey.PageDown)]
    [Arguments("\n", TuiKey.Enter)]
    [Arguments("\t", TuiKey.Tab)]
    [Arguments("\b", TuiKey.Backspace)]
    [Arguments("\x7f", TuiKey.Backspace)]
    public async Task KeySequences(string input, TuiKey key)
    {
        // Act
        var events = Decode(input);

        // Assert
        await Assert.That(events).IsEquivalentTo(new[] { new TuiEvent(new KeyEvent(key)) }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("\e[2~")]   // Insert: nothing the screens use
    [Arguments("\e[9~")]
    [Arguments("\e[X")]
    [Arguments("\eOx")]
    public async Task SequencesForKeysTheScreensDontUseAreDropped(string input)
    {
        // Act
        var events = Decode(input);

        // Assert
        await Assert.That(events).IsEmpty();
    }

    [Test]
    [Arguments("\e[1;3A", false, true)]
    [Arguments("\e[1;5B", true, false)]
    [Arguments("\e[1;7D", true, true)]
    [Arguments("\e[1;2C", false, false)]   // shift alone isn't reported
    [Arguments("\e[3;x~", false, false)]   // a modifier field that isn't a number
    public async Task ModifiersOnCursorKeys(string input, bool ctrl, bool alt)
    {
        // Act
        var events = Decode(input);

        // Assert
        var e = await Assert.That(events).HasSingleItem();
        await Assert.That((e.Key!.Value.Ctrl, e.Key.Value.Alt)).IsEqualTo((ctrl, alt));
    }

    [Test]
    public async Task CtrlLettersCarryTheLetter()
    {
        // Act
        var events = DecodeBytes(0x13);

        // Assert
        await Assert.That(events[0].Key).IsEqualTo(KeyEvent.CtrlOf('s'));
    }

    [Test]
    public async Task AltLetterIsEscThenTheLetter()
    {
        // Act
        var events = Decode("\ex");

        // Assert
        await Assert.That(events[0].Key).IsEqualTo(new KeyEvent(TuiKey.Char, 'x', Alt: true));
    }

    [Test]
    public async Task AltWithAMultibyteCharacter()
    {
        // Act
        var events = Decode("\eé");

        // Assert
        await Assert.That(events[0].Key).IsEqualTo(new KeyEvent(TuiKey.Char, 'é', Alt: true));
    }

    [Test]
    public async Task AltWithAnUnfinishedCharacterWaitsForTheRest()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();
        decoder.Feed([0x1B, 0xC3], events);
        int before = events.Count;

        // Act
        decoder.Feed([0xA9], events);

        // Assert
        await Assert.That(before).IsEqualTo(0);
        await Assert.That(events[0].Key).IsEqualTo(new KeyEvent(TuiKey.Char, 'é', Alt: true));
    }

    [Test]
    public async Task EscEscIsEscThenWhatFollows()
    {
        // Act
        var events = Decode("\e\ex");

        // Assert
        await Assert.That(events.Select(e => e.Key)).IsEquivalentTo(
            new KeyEvent?[] { new KeyEvent(TuiKey.Esc), new KeyEvent(TuiKey.Char, 'x', Alt: true) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EscBeforeAControlCharacterIsDropped()
    {
        // Act: ESC, then Ctrl+A on its own
        var events = DecodeBytes(0x1B, 0x01);

        // Assert
        await Assert.That(events.Select(e => e.Key)).IsEquivalentTo(new KeyEvent?[] { KeyEvent.CtrlOf('a') }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Ss3SplitAcrossReadsWaits()
    {
        // Act
        var events = Decode("\eO", "A");

        // Assert
        await Assert.That(events[0].Key).IsEqualTo(new KeyEvent(TuiKey.Up));
    }

    [Test]
    public async Task LoneEscIsPendingUntilFlushed()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();
        decoder.Feed([0x1B], events);
        bool pending = decoder.EscapePending;

        // Act
        decoder.FlushEscape(events);

        // Assert
        await Assert.That(pending).IsTrue();
        await Assert.That(events.Single().Key).IsEqualTo(new KeyEvent(TuiKey.Esc));
    }

    [Test]
    public async Task FlushWithoutAPendingEscDoesNothing()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();

        // Act
        decoder.FlushEscape(events);

        // Assert
        await Assert.That(events).IsEmpty();
    }

    [Test]
    public async Task StrayContinuationBytesAreSkipped()
    {
        // Act
        var events = DecodeBytes(0x80, (byte)'a');

        // Assert
        await Assert.That(events.Select(e => e.Key)).IsEquivalentTo(new KeyEvent?[] { KeyEvent.OfChar('a') }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidUtf8IsDropped()
    {
        // Act: a three-byte lead with a non-continuation byte
        var events = DecodeBytes(0xE2, 0x41, 0x41, (byte)'b');

        // Assert
        await Assert.That(events.Select(e => e.Key)).IsEquivalentTo(new KeyEvent?[] { KeyEvent.OfChar('b') }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FourByteCharacters()
    {
        // Act
        var events = Decode("\U0001F600");

        // Assert
        await Assert.That(events[0].Key).IsEqualTo(new KeyEvent(TuiKey.Char, 0x1F600));
    }

    [Test]
    [Arguments("\e[<0;5;3M", MouseKind.Press, 4, 2, 0)]
    [Arguments("\e[<2;1;1M", MouseKind.Press, 0, 0, 2)]
    [Arguments("\e[<0;5;3m", MouseKind.Release, 4, 2, 0)]
    [Arguments("\e[<32;9;9M", MouseKind.Drag, 8, 8, 0)]
    [Arguments("\e[<64;2;2M", MouseKind.WheelUp, 1, 1, 0)]
    [Arguments("\e[<65;2;2M", MouseKind.WheelDown, 1, 1, 1)]
    public async Task SgrMouseReports(string input, MouseKind kind, int x, int y, int button)
    {
        // Act
        var events = Decode(input);

        // Assert
        await Assert.That(events.Single().Mouse).IsEqualTo(new MouseEvent(kind, x, y, button));
    }

    [Test]
    [Arguments("\e[<0;5M")]
    [Arguments("\e[<?;5;3M")]   // a parameter byte that is no number
    [Arguments("\e[<0;?;3M")]
    [Arguments("\e[<0;5;?M")]
    public async Task BrokenMouseReportsAreDropped(string input)
    {
        // Act
        var events = Decode(input);

        // Assert
        await Assert.That(events).IsEmpty();
    }

    [Test]
    [Arguments("\e[I", true)]
    [Arguments("\e[O", false)]
    public async Task FocusReports(string input, bool focused)
    {
        // Act
        var events = Decode(input);

        // Assert
        await Assert.That(events.Single().Focused).IsEqualTo(focused);
    }

    [Test]
    public async Task UnfinishedCsiWaitsForItsFinalByte()
    {
        // Act
        var events = Decode("\e[1;5", "A");

        // Assert
        await Assert.That(events.Single().Key).IsEqualTo(new KeyEvent(TuiKey.Up, Ctrl: true));
    }

    [Test]
    [Arguments("\e]11;rgb:ffff/8000/0000\a", 0xFF7F00u)]   // 0x8000 of 0xFFFF is just under half
    [Arguments("\e]11;rgb:ff/80/00\e\\", 0xFF8000u)]
    [Arguments("\e]11;rgb:f/8/0\a", 0xFF8800u)]   // one hex digit per channel: scaled
    public async Task BackgroundColorReplies(string input, uint color)
    {
        // Act
        var events = Decode(input);

        // Assert
        await Assert.That(events.Single().BackgroundColor).IsEqualTo(color);
    }

    [Test]
    [Arguments("10;rgb:ffff/ffff/ffff")]
    [Arguments("11;rgb:ffff/ffff")]
    [Arguments("11;rgb:ffff//ffff")]
    [Arguments("11;rgb:fffff/ffff/ffff")]
    [Arguments("11;rgb:zz/00/00")]
    public async Task ColorRepliesThatArentOneAreRefused(string body)
    {
        // Act
        uint? color = TuiInputDecoder.ParseColorReply(body);

        // Assert
        await Assert.That(color).IsNull();
    }

    [Test]
    public async Task OscSplitAcrossReadsWaitsForItsTerminator()
    {
        // Act
        var events = Decode("\e]11;rgb:00/00/", "ff\a");

        // Assert
        await Assert.That(events.Single().BackgroundColor).IsEqualTo(0x0000FFu);
    }

    [Test]
    public async Task OscThatNeverEndsIsDroppedAfterItsLimit()
    {
        // Act: an OSC without terminator longer than any reply, then a key
        var events = Decode("\e]52;" + new string('x', 600), "j");

        // Assert
        await Assert.That(events.Select(e => e.Key)).IsEquivalentTo(new KeyEvent?[] { KeyEvent.OfChar('j') }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PasteTerminatorSplitAcrossReads()
    {
        // Act
        var events = Decode("\e[200~line one\nline", " two\e[2", "01~q");

        // Assert
        await Assert.That(events.Select(e => e.Paste ?? "key:" + (char)e.Key!.Value.Rune)).IsEquivalentTo(
            new[] { "line one\nline two", "key:q" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PasteWithAnEscThatIsNoTerminatorKeepsIt()
    {
        // Act
        var events = Decode("\e[200~a\e[2x", "b\e[201~");

        // Assert
        await Assert.That(events.Single().Paste).IsEqualTo("a\e[2xb");
    }
}
