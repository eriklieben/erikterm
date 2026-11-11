using ErikTerm.Ui.Accessibility;

namespace ErikTerm.Tests;

/// <summary>What of a pane's new output a screen reader is told, and when (Windows UI Automation notifications).</summary>
public class OutputAnnouncerTests
{
    [Test]
    public async Task Output_is_held_for_the_batch_window()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Output("hello", alternateScreen: false, rewriteOnly: false, now: 1000);

        // Act
        var early = a.Take(1000 + OutputAnnouncer.BatchMs - 1);
        var due = a.Take(1000 + OutputAnnouncer.BatchMs);

        // Assert
        await Assert.That(early).IsNull();
        await Assert.That(due).IsEqualTo(new Announcement("hello", Replaceable: false));
    }

    [Test]
    public async Task Output_within_the_window_is_spoken_together()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Output("one", false, false, 1000);
        a.Output("two", false, false, 1050);

        // Act
        var due = a.Take(1100);

        // Assert
        await Assert.That(due?.Text).IsEqualTo("one\ntwo");
    }

    [Test]
    public async Task The_echo_of_typed_characters_is_not_spoken()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Typed("l", 1000);
        a.Typed("s", 1010);

        // Act
        a.Output("l", false, false, 1020);
        a.Output("s", false, false, 1030);

        // Assert
        await Assert.That(a.HasPending).IsFalse();
    }

    [Test]
    public async Task Output_after_the_echo_is_spoken()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Typed("ab", 1000);

        // Act
        a.Output("abc done", false, false, 1010);

        // Assert
        await Assert.That(a.Take(2000)?.Text).IsEqualTo("c done");
    }

    [Test]
    public async Task Typing_whose_echo_never_came_is_forgotten()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Typed("secret", 1000);

        // Act
        a.Output("secret?", false, false, 1000 + OutputAnnouncer.TypedEchoMs + 1);

        // Assert
        await Assert.That(a.Take(5000)?.Text).IsEqualTo("secret?");
    }

    [Test]
    public async Task Nothing_is_said_for_the_alternate_screen()
    {
        // Arrange
        var a = new OutputAnnouncer();

        // Act
        a.Output("-- INSERT --", alternateScreen: true, rewriteOnly: false, now: 1000);

        // Assert
        await Assert.That(a.HasPending).IsFalse();
    }

    [Test]
    public async Task Blank_output_is_not_spoken()
    {
        // Arrange
        var a = new OutputAnnouncer();

        // Act
        a.Output("  \n ", false, false, 1000);

        // Assert
        await Assert.That(a.HasPending).IsFalse();
    }

    [Test]
    public async Task Control_characters_are_removed()
    {
        // Arrange
        var a = new OutputAnnouncer();

        // Act
        a.Output("a\u0007b\u001Bc", false, false, 1000);

        // Assert
        await Assert.That(a.Take(2000)?.Text).IsEqualTo("abc");
    }

    [Test]
    public async Task A_flood_is_cut_to_its_last_lines_and_is_replaceable()
    {
        // Arrange
        var a = new OutputAnnouncer();
        string flood = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line {i}"));
        a.Output(flood, false, false, 1000);

        // Act
        var due = a.Take(1100);

        // Assert
        var lines = due!.Value.Text.Split('\n');
        await Assert.That(lines.Length).IsEqualTo(OutputAnnouncer.MaxLines);
        await Assert.That(lines[^1]).IsEqualTo("line 99");
        await Assert.That(due.Value.Replaceable).IsTrue();
    }

    [Test]
    public async Task After_a_flood_the_next_announcement_waits_longer()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Output(string.Join('\n', Enumerable.Range(0, 100).Select(i => $"{i}")), false, false, 1000);
        a.Take(1100);
        a.Output("more", false, false, 1150);

        // Act
        var early = a.Take(1150 + OutputAnnouncer.BatchMs);
        var late = a.Take(1150 + OutputAnnouncer.SlowMs);

        // Assert
        await Assert.That(early).IsNull();
        await Assert.That(late?.Text).IsEqualTo("more");
    }

    [Test]
    public async Task Very_long_output_is_cut_to_the_character_limit()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Output(new string('x', OutputAnnouncer.MaxChars * 3), false, false, 1000);

        // Act
        var due = a.Take(5000);

        // Assert
        await Assert.That(due!.Value.Text.Length).IsEqualTo(OutputAnnouncer.MaxChars);
        await Assert.That(due.Value.Replaceable).IsTrue();
    }

    [Test]
    public async Task A_progress_line_is_spoken_at_most_once_a_second_and_replaceable()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Output("Downloading 45%", false, rewriteOnly: true, now: 1000);
        a.Output("Downloading 46%", false, rewriteOnly: true, now: 1100);

        // Act
        var early = a.Take(1000 + OutputAnnouncer.BatchMs);
        var due = a.Take(1000 + OutputAnnouncer.SlowMs);

        // Assert
        await Assert.That(early).IsNull();
        await Assert.That(due?.Text).IsEqualTo("Downloading 45%\nDownloading 46%");
        await Assert.That(due?.Replaceable).IsTrue();
    }

    [Test]
    public async Task New_output_after_a_rewrite_makes_the_batch_ordinary()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Output("50%", false, rewriteOnly: true, now: 1000);
        a.Output("done", false, rewriteOnly: false, now: 1010);

        // Act
        var due = a.Take(1000 + OutputAnnouncer.BatchMs);

        // Assert
        await Assert.That(due?.Replaceable).IsFalse();
    }

    [Test]
    public async Task Wake_time_is_when_the_batch_is_due()
    {
        // Arrange
        var a = new OutputAnnouncer();
        int idle = a.WakeMs(0);
        a.Output("x", false, false, 1000);

        // Act
        int wake = a.WakeMs(1040);

        // Assert
        await Assert.That(idle).IsEqualTo(int.MaxValue);
        await Assert.That(wake).IsEqualTo(OutputAnnouncer.BatchMs - 40);
    }

    [Test]
    public async Task Clear_drops_what_was_waiting()
    {
        // Arrange
        var a = new OutputAnnouncer();
        a.Output("x", false, false, 1000);

        // Act
        a.Clear();

        // Assert
        await Assert.That(a.Take(5000)).IsNull();
    }
}
