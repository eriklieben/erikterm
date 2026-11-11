using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class QuietPollTests
{
    [Test]
    public async Task The_first_check_is_always_due()
    {
        // Arrange
        var poll = new QuietPoll();

        // Act
        bool due = poll.IsDue(contentVersion: 0, now: 1);

        // Assert
        await Assert.That(due).IsTrue();
    }

    [Test]
    public async Task No_output_since_the_last_check_is_not_due()
    {
        // Arrange
        var poll = new QuietPoll();
        poll.Checked(contentVersion: 7, now: 1000);

        // Act
        bool due = poll.IsDue(contentVersion: 7, now: 1000 + QuietPoll.FallbackMs - 1);

        // Assert
        await Assert.That(due).IsFalse();
    }

    [Test]
    public async Task Any_output_makes_it_due()
    {
        // Arrange
        var poll = new QuietPoll();
        poll.Checked(contentVersion: 7, now: 1000);

        // Act
        bool due = poll.IsDue(contentVersion: 8, now: 1001);

        // Assert
        await Assert.That(due).IsTrue();
    }

    [Test]
    public async Task A_quiet_pane_is_still_checked_now_and_then()
    {
        // Arrange
        var poll = new QuietPoll();
        poll.Checked(contentVersion: 7, now: 1000);

        // Act
        bool due = poll.IsDue(contentVersion: 7, now: 1000 + QuietPoll.FallbackMs);

        // Assert
        await Assert.That(due).IsTrue();
    }
}
