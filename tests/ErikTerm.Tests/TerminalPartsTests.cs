using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>The classes Terminal and Row hold, tested on their own through the seams they take from their owner.</summary>
public class TerminalPartsTests
{
    private sealed class RecordingTarget : ISynchronizedOutputTarget
    {
        public bool SynchronizedOutput { get; set; }
        public StringBuilder Parsed { get; } = new();

        public void Parse(ReadOnlySpan<byte> data)
        {
            string text = Encoding.ASCII.GetString(data);
            Parsed.Append(text);
            if (text.EndsWith("\e[?2026h", StringComparison.Ordinal)) SynchronizedOutput = true;
            if (text.EndsWith("\e[?2026l", StringComparison.Ordinal)) SynchronizedOutput = false;
        }
    }

    private sealed class FixedRows(params Row[] rows) : ILinkedRows
    {
        public IEnumerable<Row?> AllRows() => rows;
    }

    [Test]
    public async Task Synchronized_output_is_held_until_the_update_ends()
    {
        // Arrange
        var target = new RecordingTarget();
        var gate = new SynchronizedOutputGate(target);

        // Act
        gate.Feed("a\e[?2026hb"u8, now: 0);
        string whileHeld = target.Parsed.ToString();
        bool holds = gate.Holds;
        gate.Feed("c\e[?2026ld"u8, now: 10);
        string after = target.Parsed.ToString();

        // Assert
        await Assert.That(whileHeld).IsEqualTo("a\e[?2026h");
        await Assert.That(holds).IsTrue();
        await Assert.That(after).IsEqualTo("a\e[?2026hbc\e[?2026ld");
        await Assert.That(gate.Holds).IsFalse();
    }

    [Test]
    public async Task Synchronized_output_is_released_when_the_update_takes_too_long()
    {
        // Arrange
        var target = new RecordingTarget();
        var gate = new SynchronizedOutputGate(target);
        gate.Feed("\e[?2026hheld"u8, now: 0);

        // Act
        bool early = gate.ReleaseExpired(Terminal.SynchronizedOutputTimeoutMs - 1);
        bool late = gate.ReleaseExpired(Terminal.SynchronizedOutputTimeoutMs);

        // Assert
        await Assert.That(early).IsFalse();
        await Assert.That(late).IsTrue();
        await Assert.That(target.Parsed.ToString()).IsEqualTo("\e[?2026hheld");
        await Assert.That(target.SynchronizedOutput).IsFalse();
    }

    [Test]
    public async Task A_link_keeps_its_id_and_an_empty_uri_ends_it()
    {
        // Arrange
        var links = new Hyperlinks(new FixedRows());

        // Act
        int first = links.Open("id=x;https://a.example"u8, current: 0);
        int again = links.Open("id=x;https://a.example"u8, current: first);
        int other = links.Open(";https://a.example"u8, current: first);
        int ended = links.Open(";"u8, current: other);
        int malformed = links.Open("no-separator"u8, current: other);

        // Assert
        await Assert.That(again).IsEqualTo(first);
        await Assert.That(other).IsNotEqualTo(first);
        await Assert.That(links.Get(first)!.Key).IsEqualTo("x");
        await Assert.That(ended).IsEqualTo(0);
        await Assert.That(malformed).IsEqualTo(other);
    }

    [Test]
    public async Task A_full_link_table_keeps_the_links_rows_still_show()
    {
        // Arrange
        var row = new Row(4);
        var links = new Hyperlinks(new FixedRows(row));
        int shown = links.Open(";https://shown.example"u8, current: 0);
        row.SetLink(0, 2, shown);
        int forgotten = links.Open(";https://forgotten.example"u8, current: 0);
        for (int i = 0; i < 4096; i++) links.Open(Encoding.ASCII.GetBytes($";https://{i}.example"), current: 0);

        // Act
        var kept = links.Get(shown);
        var dropped = links.Get(forgotten);

        // Assert
        await Assert.That(kept?.Uri).IsEqualTo("https://shown.example");
        await Assert.That(dropped).IsNull();
    }

    [Test]
    [Arguments("1;bg;red;brightness;255", 0xFF0000u)]
    [Arguments("1;bg;blue;brightness;300", 0x0000FFu)]
    public async Task A_tab_color_component_sets_its_channel(string osc, uint expected)
    {
        // Arrange
        var color = new ProgramTabColor();

        // Act
        bool applied = color.TryApply(Encoding.ASCII.GetBytes(osc));

        // Assert
        await Assert.That(applied).IsTrue();
        await Assert.That(color.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task A_tab_color_default_resets_every_channel()
    {
        // Arrange
        var color = new ProgramTabColor();
        color.TryApply("1;bg;green;brightness;10"u8);

        // Act
        bool applied = color.TryApply("1;bg;red;default"u8);
        bool xterm = color.TryApply("2;rgb:0/0/0"u8);

        // Assert
        await Assert.That(applied).IsTrue();
        await Assert.That(xterm).IsFalse();
        await Assert.That(color.Value).IsNull();
    }

    [Test]
    [Arguments(1000, MouseTracking.Press)]
    [Arguments(1002, MouseTracking.ButtonMotion)]
    [Arguments(1003, MouseTracking.AnyMotion)]
    public async Task A_mouse_mode_is_reported_as_set_until_reset(int mode, MouseTracking tracking)
    {
        // Arrange
        var modes = new InputModes();

        // Act
        modes.Set(mode, on: true);
        var set = modes.MouseTracking;
        bool reported = modes.IsSet(mode);
        modes.Reset();

        // Assert
        await Assert.That(set).IsEqualTo(tracking);
        await Assert.That(reported).IsTrue();
        await Assert.That(modes.IsSet(mode)).IsFalse();
        await Assert.That(modes.MouseTracking).IsEqualTo(MouseTracking.None);
    }

    [Test]
    public async Task Shifted_clusters_that_leave_the_row_are_dropped()
    {
        // Arrange
        var clusters = new RowClusters();
        clusters.Store(1, "é");
        clusters.Store(3, "ä");

        // Act
        clusters.Shift(from: 2, delta: 2, columns: 4);

        // Assert
        await Assert.That(clusters.Count).IsEqualTo(1);
        await Assert.That(clusters.Get(1)).IsEqualTo("é");
        await Assert.That(clusters.Get(3)).IsNull();
    }
}
