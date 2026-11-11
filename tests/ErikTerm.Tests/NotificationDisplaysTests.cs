using ErikTerm.App.Notifications;

namespace ErikTerm.Tests;

public class NotificationDisplaysTests
{
    private const nint Left = 11, Right = 22;
    private const string Dell = "DELL U2720Q", Lg = "LG 27UK850";

    private static readonly string[] Two = [Dell, Lg];

    [Test]
    public async Task AWindowOnAnotherDisplayNamesIt()
    {
        // Act
        string? name = NotificationDisplays.OtherDisplayName(Left, Right, Dell, Two);

        // Assert
        await Assert.That(name).IsEqualTo(Dell);
    }

    [Test]
    public async Task AWindowOnTheDisplayInUseNamesNothing()
    {
        // Act
        string? name = NotificationDisplays.OtherDisplayName(Left, Left, Dell, Two);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task OneDisplayNamesNothing()
    {
        // Act
        string? name = NotificationDisplays.OtherDisplayName(Left, Right, Dell, [Dell]);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    [Arguments(0, 22)]
    [Arguments(11, 0)]
    [Arguments(0, 0)]
    public async Task AnUnknownMonitorNamesNothing(int window, int current)
    {
        // Act
        string? name = NotificationDisplays.OtherDisplayName(window, current, Dell, Two);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task TwoDisplaysWithTheSameNameNameNothing()
    {
        // Act
        string? name = NotificationDisplays.OtherDisplayName(Left, Right, Dell, [Dell, "dell  u2720q"]);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("\u0007‮\n")]
    public async Task ANameThatCleansToNothingNamesNothing(string? displayName)
    {
        // Act
        string? name = NotificationDisplays.OtherDisplayName(Left, Right, displayName, Two);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task ControlCharactersAndBidiMarksAreRemovedFromTheName()
    {
        // Arrange
        const string raw = "DELL\u001b‮ U2720Q\n";

        // Act
        string? name = NotificationDisplays.OtherDisplayName(Left, Right, raw, [raw, Lg]);

        // Assert
        await Assert.That(name).IsEqualTo(Dell);
    }

    [Test]
    public async Task ADisplayMissingFromTheListNamesNothing()
    {
        // Act
        string? name = NotificationDisplays.OtherDisplayName(Left, Right, "Gone", Two);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task TheLabelSaysWhichDisplay()
    {
        // Act
        string? label = NotificationDisplays.Label(Dell);

        // Assert
        await Assert.That(label).IsEqualTo("On DELL U2720Q");
    }

    [Test]
    public async Task NoDisplayMeansNoLabel()
    {
        // Act
        string? label = NotificationDisplays.Label(null);

        // Assert
        await Assert.That(label).IsNull();
    }
}
