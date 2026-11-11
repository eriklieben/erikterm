using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class ParserTests
{
    private sealed class Log
    {
        public readonly List<string> Events = [];
    }

    private readonly struct Recorder(Log log) : IVtHandler
    {
        public void PrintAscii(ReadOnlySpan<byte> text) { foreach (byte b in text) log.Events.Add($"print {(char)b}"); }
        public void Print(int cp) => log.Events.Add($"print {char.ConvertFromUtf32(cp)}");
        public int PrintUtf8(ReadOnlySpan<byte> text) => 0;
        public void Execute(byte c) => log.Events.Add($"exec {c:X2}");
        public void EscDispatch(ReadOnlySpan<byte> inter, byte final) =>
            log.Events.Add($"esc {Encoding.ASCII.GetString(inter)}{(char)final}");

        public void CsiDispatch(CsiParams p, ReadOnlySpan<byte> inter, byte priv, byte final)
        {
            var sb = new StringBuilder("csi ");
            if (priv != 0) sb.Append((char)priv);
            for (int i = 0; i < p.Count; i++)
            {
                if (i > 0) sb.Append(p.IsSub(i) ? ':' : ';');
                sb.Append(p[i]);
            }
            sb.Append(Encoding.ASCII.GetString(inter)).Append((char)final);
            log.Events.Add(sb.ToString());
        }

        public void CursorPosition(int row, int col) => log.Events.Add($"csi {row};{col}H");
        public void OscDispatch(ReadOnlySpan<byte> data) => log.Events.Add($"osc {Encoding.UTF8.GetString(data)}");
        public void ApcDispatch(ReadOnlySpan<byte> data) => log.Events.Add($"apc {Encoding.UTF8.GetString(data)}");
        public void DcsDispatch(ReadOnlySpan<byte> data) => log.Events.Add($"dcs {Encoding.UTF8.GetString(data)}");
    }

    private static List<string> Parse(params string[] chunks)
    {
        var log = new Log();
        var parser = new VtParser<Recorder>(new Recorder(log));
        foreach (var c in chunks) parser.Feed(Encoding.UTF8.GetBytes(c));
        return log.Events;
    }

    [Test]
    public async Task PrintsAsciiAndControls()
    {
        // Act
        var events = Parse("a\r\nb");

        // Assert
        await Assert.That(events).IsEquivalentTo(["print a", "exec 0D", "exec 0A", "print b"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CsiWithParams()
    {
        // Act
        var events = Parse("\e[12;34H");

        // Assert
        await Assert.That(events).IsEquivalentTo(["csi 12;34H"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CsiPrivateMarker()
    {
        // Act
        var events = Parse("\e[?1049h");

        // Assert
        await Assert.That(events).IsEquivalentTo(["csi ?1049h"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CsiIntermediate()
    {
        // Act
        var events = Parse("\e[2 q");

        // Assert
        await Assert.That(events).IsEquivalentTo(["csi 2 q"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ColonSubParameters()
    {
        // Act
        var events = Parse("\e[38:2::10:20:30m");

        // Assert
        await Assert.That(events).IsEquivalentTo(["csi 38:2:0:10:20:30m"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task EmptyParamsAreZero()
    {
        // Act
        var events = Parse("\e[;5H");

        // Assert
        await Assert.That(events).IsEquivalentTo(["csi 0;5H"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SequenceSplitAcrossChunks()
    {
        // Act
        var events = Parse("\e[3", "1", "mx");

        // Assert
        await Assert.That(events).IsEquivalentTo(["csi 31m", "print x"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OscTerminatedByBel()
    {
        // Act
        var events = Parse("\e]0;hello\a");

        // Assert
        await Assert.That(events).IsEquivalentTo(["osc 0;hello"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OscTerminatedBySt()
    {
        // Act
        var events = Parse("\e]2;tïtle\e\\");

        // Assert
        await Assert.That(events).IsEquivalentTo(["osc 2;tïtle", @"esc \"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task EscWithIntermediate()
    {
        // Act
        var events = Parse("\e(0");

        // Assert
        await Assert.That(events).IsEquivalentTo(["esc (0"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Utf8MultiByte()
    {
        // Act
        var events = Parse("é日🦀");

        // Assert
        await Assert.That(events).IsEquivalentTo(["print é", "print 日", "print 🦀"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Utf8SplitAcrossFeeds()
    {
        // Arrange
        var log = new Log();
        var parser = new VtParser<Recorder>(new Recorder(log));
        byte[] crab = Encoding.UTF8.GetBytes("🦀");

        // Act
        parser.Feed(crab.AsSpan(0, 2));
        parser.Feed(crab.AsSpan(2));

        // Assert
        await Assert.That(log.Events).IsEquivalentTo(["print 🦀"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidUtf8BecomesReplacement()
    {
        // Arrange
        var log = new Log();
        var parser = new VtParser<Recorder>(new Recorder(log));

        // Act
        parser.Feed([0xC3, (byte)'a', 0xFF]);

        // Assert
        await Assert.That(log.Events).IsEquivalentTo(["print �", "print a", "print �"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CanAbortsSequence()
    {
        // Act
        var events = Parse("\e[31\x18m");

        // Assert
        await Assert.That(events).IsEquivalentTo(["exec 18", "print m"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DcsIsCollectedUntilSt()
    {
        // Act
        var events = Parse("\ePq#0;2;0;\r\n0;0\e\\z");

        // Assert
        await Assert.That(events).IsEquivalentTo(["dcs q#0;2;0;0;0", @"esc \", "print z"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ApcIsCollectedAcrossFeeds()
    {
        // Act
        var events = Parse("\e_Ga=T,f=1", "00;AA", "AA\e", "\\");

        // Assert
        await Assert.That(events).IsEquivalentTo(["apc Ga=T,f=100;AAAA", @"esc \"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CanCancelsApcWithoutDispatch()
    {
        // Act
        var events = Parse("\e_Gabc\x18x");

        // Assert
        await Assert.That(events).IsEquivalentTo(["exec 18", "print x"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PmAndSosAreIgnored()
    {
        // Act
        var events = Parse("\e^pm\e\\\eXsos\e\\y");

        // Assert
        await Assert.That(events).IsEquivalentTo([@"esc \", @"esc \", "print y"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OrdinaryOscStaysCapped()
    {
        // Act
        var events = Parse("\e]2;" + new string('x', 100_000) + "\a");

        // Assert
        string osc = await Assert.That(events).HasSingleItem();
        await Assert.That(osc.Length - "osc ".Length).IsEqualTo(64 * 1024);
    }

    [Test]
    public async Task ImageOscMayExceedTheCap()
    {
        // Act
        var events = Parse("\e]1337;File=inline=1:" + new string('A', 200_000) + "\a");

        // Assert
        string osc = await Assert.That(events).HasSingleItem();
        await Assert.That(osc.Length - "osc ".Length).IsEqualTo("1337;File=inline=1:".Length + 200_000);
    }
}
