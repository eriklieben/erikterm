using ErikTerm.App.Integrations;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Send to Agent's targets and payload, and which panes broadcast input reaches.</summary>
public class SendToAgentTests
{
    private static AgentCandidate Pane(int id, string? agent, uint window = 1, int tab = 1, bool exited = false) => new(id, agent, exited, window, tab);

    [Test]
    public async Task OnlyPanesRunningAnAgentAreOffered()
    {
        // Arrange
        AgentCandidate[] panes = [Pane(1, null), Pane(2, "Claude"), Pane(3, "")];

        // Act
        var targets = AgentSend.Targets(panes, sourcePaneId: 99, 1, 1);

        // Assert
        await Assert.That(targets.Select(t => t.PaneId)).IsEquivalentTo(new[] { 2 });
    }

    [Test]
    public async Task ThePaneTheTextComesFromIsLeftOut()
    {
        // Arrange
        AgentCandidate[] panes = [Pane(1, "Claude"), Pane(2, "Codex")];

        // Act
        var targets = AgentSend.Targets(panes, sourcePaneId: 1, 1, 1);

        // Assert
        await Assert.That(targets.Select(t => t.PaneId)).IsEquivalentTo(new[] { 2 });
    }

    [Test]
    public async Task AnExitedAgentIsLeftOut()
    {
        // Arrange
        AgentCandidate[] panes = [Pane(1, "Claude", exited: true)];

        // Act
        var targets = AgentSend.Targets(panes, sourcePaneId: 99, 1, 1);

        // Assert
        await Assert.That(targets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TheCurrentTabComesFirstThenTheWindowThenOtherWindows()
    {
        // Arrange
        AgentCandidate[] panes =
        [
            Pane(1, "A", window: 2, tab: 9),
            Pane(2, "B", window: 1, tab: 2),
            Pane(3, "C", window: 1, tab: 1),
        ];

        // Act
        var targets = AgentSend.Targets(panes, sourcePaneId: 99, currentWindow: 1, currentTab: 1);

        // Assert
        await Assert.That(targets.Select(t => t.PaneId)).IsEquivalentTo(new[] { 3, 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnAgentsPayloadIsABracketedPaste()
    {
        // Act
        var (text, removed) = AgentSend.Payload("fix this", bracketed: true, sanitize: true, submit: false);

        // Assert
        await Assert.That(text).IsEqualTo("\e[200~fix this\e[201~");
        await Assert.That(removed).IsEqualTo(0);
    }

    [Test]
    public async Task SubmittingPressesEnterAfterThePaste()
    {
        // Act
        var (text, _) = AgentSend.Payload("go", bracketed: true, sanitize: true, submit: true);

        // Assert
        await Assert.That(text).IsEqualTo("\e[200~go\e[201~\r");
    }

    [Test]
    public async Task ThePayloadCantBreakOutOfThePaste()
    {
        // Act
        var (text, removed) = AgentSend.Payload("a\e[201~\u0003b", bracketed: true, sanitize: true, submit: false);

        // Assert
        await Assert.That(text).IsEqualTo("\e[200~ab\e[201~");
        await Assert.That(removed).IsGreaterThan(0);
    }

    [Test]
    public async Task NothingLeftMeansNothingIsSent()
    {
        // Act
        var (text, _) = AgentSend.Payload("\u0003", bracketed: true, sanitize: true, submit: true);

        // Assert
        await Assert.That(text).IsEqualTo("");
    }

    [Test]
    public async Task SendToAgentIsAnAction()
    {
        // Act
        bool ok = ActionSpec.TryParse("SendToAgent", out _, out _);

        // Assert
        await Assert.That(ok).IsTrue();
    }
}

public class BroadcastRoutingTests
{
    private sealed record P(string Name, bool Parked = false);

    private static readonly Func<P, bool> NotParked = p => !p.Parked;

    private static List<string> Targets(BroadcastMode mode, P[] tab, P focused, HashSet<P>? chosen = null)
    {
        var targets = new List<P>();
        BroadcastRouting.Targets(mode, tab, focused, NotParked, chosen, targets);
        return targets.ConvertAll(p => p.Name);
    }

    [Test]
    public async Task OffSendsNothingAnywhere()
    {
        // Arrange
        P a = new("a"), b = new("b");

        // Act
        var targets = Targets(BroadcastMode.Off, [a, b], a);

        // Assert
        await Assert.That(targets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AllPanesReachesEveryOtherPaneOfTheTab()
    {
        // Arrange
        P a = new("a"), b = new("b"), c = new("c");

        // Act
        var targets = Targets(BroadcastMode.AllPanes, [a, b, c], b);

        // Assert
        await Assert.That(targets).IsEquivalentTo(new[] { "a", "c" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AParkedPaneGetsNothing()
    {
        // Arrange
        P a = new("a"), parked = new("teammate", Parked: true), c = new("c");

        // Act
        var targets = Targets(BroadcastMode.AllPanes, [a, parked, c], a);

        // Assert
        await Assert.That(targets).IsEquivalentTo(new[] { "c" });
    }

    [Test]
    public async Task APaneOfAnotherTabIsNeverAmongTheTargets()
    {
        // Arrange
        P a = new("a"), b = new("b"), otherTab = new("other");
        var chosen = new HashSet<P> { a, b, otherTab }; // still in the set after moving away

        // Act
        var targets = Targets(BroadcastMode.Selected, [a, b], a, chosen);

        // Assert
        await Assert.That(targets).IsEquivalentTo(new[] { "b" });
    }

    [Test]
    public async Task SelectedReachesOnlyThePickedPanes()
    {
        // Arrange
        P a = new("a"), b = new("b"), c = new("c");

        // Act
        var targets = Targets(BroadcastMode.Selected, [a, b, c], a, [a, c]);

        // Assert
        await Assert.That(targets).IsEquivalentTo(new[] { "c" });
    }

    [Test]
    public async Task TypingOutsideThePickedGroupStaysLocal()
    {
        // Arrange
        P a = new("a"), b = new("b"), c = new("c");

        // Act
        var targets = Targets(BroadcastMode.Selected, [a, b, c], c, [a, b]);

        // Assert
        await Assert.That(targets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TheGroupCountsEligiblePanes()
    {
        // Arrange
        P a = new("a"), parked = new("t", Parked: true), c = new("c");

        // Act
        int size = BroadcastRouting.GroupSize(BroadcastMode.AllPanes, [a, parked, c], NotParked, null);

        // Assert
        await Assert.That(size).IsEqualTo(2);
    }

    [Test]
    public async Task TheGroupOfAPickCountsOnlyPickedPanes()
    {
        // Arrange
        P a = new("a"), b = new("b"), c = new("c");

        // Act
        int size = BroadcastRouting.GroupSize(BroadcastMode.Selected, [a, b, c], NotParked, new HashSet<P> { a, b });

        // Assert
        await Assert.That(size).IsEqualTo(2);
    }

    [Test]
    [Arguments("ToggleBroadcastInput")]
    [Arguments("ToggleBroadcastInput(Selected)")]
    public async Task ToggleBroadcastInputIsAnAction(string text)
    {
        // Act
        bool ok = ActionSpec.TryParse(text, out _, out _);

        // Assert
        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task ToggleBroadcastInputRefusesOtherArguments()
    {
        // Act
        bool ok = ActionSpec.TryParse("ToggleBroadcastInput(Everywhere)", out _, out _);

        // Assert
        await Assert.That(ok).IsFalse();
    }
}
