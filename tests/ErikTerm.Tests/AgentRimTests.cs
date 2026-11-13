using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>
/// Where the waiting rim goes (AgentBadge.RimRect): in the gutter just outside the pane, never over its first and last
/// columns and rows, and clear of a neighbour's rim across the split gap.
/// </summary>
public class AgentRimTests
{
    private static readonly RectF Window = new(0, 0, 1000, 800);

    [Test]
    [Arguments(1f)]
    [Arguments(1.5f)]
    [Arguments(2f)]
    public async Task TheRimLineLiesOutsideThePane(float scale)
    {
        // Arrange
        var th = new Theme { Scale = scale };
        var pane = new RectF(100, 80, 400, 300);
        float thickness = MathF.Max(1, MathF.Round(th.S(1.5f)));

        // Act
        var rim = AgentBadge.RimRect(pane, Window, th);

        // Assert
        await Assert.That(rim.X + thickness).IsLessThanOrEqualTo(pane.X);
        await Assert.That(rim.Y + thickness).IsLessThanOrEqualTo(pane.Y);
        await Assert.That(rim.Right - thickness).IsGreaterThanOrEqualTo(pane.Right);
        await Assert.That(rim.Bottom - thickness).IsGreaterThanOrEqualTo(pane.Bottom);
    }

    [Test]
    [Arguments(1f)]
    [Arguments(1.5f)]
    [Arguments(2f)]
    public async Task TheRimStaysInTheDefaultWindowPaddingAndBelowTheTabBar(float scale)
    {
        // Arrange: 6 px of window padding (scaled) to the left, 4 px under the tab bar
        var th = new Theme { Scale = scale };
        var pane = new RectF(MathF.Round(th.S(6)), 40 + MathF.Round(th.S(4)), 400, 300);

        // Act
        var rim = AgentBadge.RimRect(pane, Window, th);

        // Assert
        await Assert.That(rim.X).IsGreaterThanOrEqualTo(0f);
        await Assert.That(rim.Y).IsGreaterThanOrEqualTo(40f);
    }

    [Test]
    [Arguments(1f)]
    [Arguments(1.5f)]
    [Arguments(2f)]
    public async Task TwoWaitingNeighboursKeepTheirRimsApartInTheSplitGap(float scale)
    {
        // Arrange: side by side with the window's gap between them (TermWindow.Gap)
        var th = new Theme { Scale = scale };
        int gap = Math.Max(3, (int)MathF.Round(9 * scale));
        float thickness = MathF.Max(1, MathF.Round(th.S(1.5f)));
        var left = new RectF(6, 40, 400, 300);
        var right = left with { X = left.Right + gap };

        // Act
        var a = AgentBadge.RimRect(left, Window, th);
        var b = AgentBadge.RimRect(right, Window, th);

        // Assert: the left rim's right line ends before the right rim's left line starts
        await Assert.That(a.Right).IsLessThanOrEqualTo(b.X);
        await Assert.That(a.Right - thickness).IsGreaterThanOrEqualTo(left.Right);
        await Assert.That(b.X + thickness).IsLessThanOrEqualTo(right.X);
    }

    [Test]
    public async Task WithoutRoomTheRimStaysInsideTheWindow()
    {
        // Arrange: window_padding = 0, the pane fills the window
        var th = new Theme { Scale = 1 };
        var pane = new RectF(0, 0, 1000, 800);

        // Act
        var rim = AgentBadge.RimRect(pane, Window, th);

        // Assert
        await Assert.That(rim).IsEqualTo(Window);
    }
}
