using ErikTerm.App.Panes;
using ErikTerm.App.Session;
using ErikTerm.App.Tmux;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Recognising Claude Code's teammate panes from what its tmux calls carry (Tmux/TmuxTeammates.cs).</summary>
public class TeammateDetectionTests
{
    /// <summary>The respawn-pane command Claude Code 2.1.319 sent in a real run (one shell string).</summary>
    private const string RespawnCommand =
        "cd /tmp/etpill-work && env CLAUDECODE=1 CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 CLAUDE_CODE_ATTRIBUTION_PROJECT='' " +
        "CLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS= /home/erik/.local/share/claude/versions/2.1.319 " +
        "--agent-id alpha@session-6556fe1b --agent-name alpha --team-name session-6556fe1b --agent-color blue " +
        "--parent-session-id 6556fe1b-6c23-4a21-9707-ae2cf8d7468d --agent-type general-purpose --permission-mode auto --model haiku";

    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        k => vars.FirstOrDefault(v => v.Key == k).Value;

    [Test]
    [Arguments("AI_AGENT", "claude-code_2-1-319_harness")]
    [Arguments("CLAUDE_CODE_ENTRYPOINT", "cli")]
    [Arguments("CLAUDECODE", "1")]
    public async Task CatSplitFromClaudeCodeIsATeammate(string key, string value)
    {
        // Act
        bool teammate = TmuxTeammates.IsTeammateSplit(["cat"], Env((key, value)));

        // Assert
        await Assert.That(teammate).IsTrue();
    }

    [Test]
    public async Task CatSplitFromAnotherProgramIsNotATeammate()
    {
        // Act
        bool teammate = TmuxTeammates.IsTeammateSplit(["cat"], Env(("SHELL", "/bin/zsh")));

        // Assert
        await Assert.That(teammate).IsFalse();
    }

    [Test]
    public async Task OtherSplitFromClaudeCodeIsNotATeammate()
    {
        // Act
        bool teammate = TmuxTeammates.IsTeammateSplit(["htop"], Env(("AI_AGENT", "claude-code_2-1-319_harness")));

        // Assert
        await Assert.That(teammate).IsFalse();
    }

    [Test]
    public async Task RespawnCommandNamesTheTeammate()
    {
        // Act
        var args = TmuxTeammates.Parse(["/bin/zsh", "-c", RespawnCommand]);

        // Assert
        await Assert.That(args).IsEqualTo(new TeammateArgs("alpha@session-6556fe1b", "alpha", "session-6556fe1b", "blue"));
    }

    [Test]
    public async Task QuotedNameIsUnquoted()
    {
        // Act
        var args = TmuxTeammates.Parse(["sh", "-c", "claude --agent-id 'qa lead@t' --agent-name 'qa lead' --team-name t"]);

        // Assert
        await Assert.That(args?.Name).IsEqualTo("qa lead");
    }

    [Test]
    public async Task AgentIdAloneGivesTheName()
    {
        // Act
        var args = TmuxTeammates.Parse(["claude", "--agent-id", "beta@team"]);

        // Assert
        await Assert.That(args?.Name).IsEqualTo("beta");
    }

    [Test]
    public async Task OrdinaryCommandIsNoTeammate()
    {
        // Act
        var args = TmuxTeammates.Parse(["sh", "-c", "npm run watch"]);

        // Assert
        await Assert.That(args).IsNull();
    }

    [Test]
    public async Task ControlCharactersAreDroppedFromNames()
    {
        // Act
        var args = TmuxTeammates.Parse(["claude", "--agent-name", "ev\u001b[31mil\u202e"]);

        // Assert
        await Assert.That(args?.Name).IsEqualTo("ev[31mil");
    }

    [Test]
    [Arguments("bg=default,fg=blue", "blue")]
    [Arguments("fg=colour208", "colour208")]
    [Arguments("bg=red", null)]
    [Arguments("fg=default", null)]
    public async Task StyleForegroundIsTheTeammateColor(string style, string? expected)
    {
        // Act
        string? color = TmuxTeammates.StyleForeground(style);

        // Assert
        await Assert.That(color).IsEqualTo(expected);
    }

    private static readonly uint[] Ansi = [0x000000, 0x110000, 0x001100, 0x111100, 0x000011, 0x110011, 0x001111, 0x111111,
        0x222222, 0x220000, 0x002200, 0x222200, 0x000022, 0x220022, 0x002222, 0xFFFFFF];

    [Test]
    [Arguments("blue", 0x000011u)]
    [Arguments("purple", 0x110011u)]
    [Arguments("magenta", 0x110011u)]
    [Arguments("brightred", 0x220000u)]
    [Arguments("colour208", 0xFF8700u)]
    [Arguments("orange", 0xFF8700u)]
    [Arguments("pink", 0xFF5FAFu)]
    [Arguments("colour244", 0x808080u)]
    [Arguments("#12ab34", 0x12AB34u)]
    public async Task ColorNamesResolve(string spec, uint expected)
    {
        // Act
        uint? rgb = TmuxTeammates.ColorValue(spec, Ansi);

        // Assert
        await Assert.That(rgb).IsEqualTo(expected);
    }

    [Test]
    [Arguments("color")]
    [Arguments("colour")]
    [Arguments("colorful")]
    public async Task AColorNameWithoutANumberIsNull(string spec)
    {
        // Act
        uint? rgb = TmuxTeammates.ColorValue(spec, Ansi);

        // Assert
        await Assert.That(rgb).IsNull();
    }

    [Test]
    public async Task UnknownColorIsNull()
    {
        // Act
        uint? rgb = TmuxTeammates.ColorValue("chartreuse", Ansi);

        // Assert
        await Assert.That(rgb).IsNull();
    }
}

/// <summary>Parked teammates in the layout tree: hidden leaves, where they would be, the lead's list (Panes/Teammate.cs).</summary>
public class TeammateLayoutTests
{
    private sealed class P(int id, Teammate? teammate = null) : ILayoutLeaf
    {
        public readonly int Id = id;
        public readonly Teammate? Teammate = teammate;
        public readonly List<P> Teammates = [];
        public bool LayoutHidden => Teammate is { Parked: true };
        public override string ToString() => Id.ToString();
    }

    private static readonly RectI Bounds = new(0, 0, 1000, 600);

    private static void Sync(LayoutTree<P> tree, bool pills = true) =>
        TeamRoster.Sync(tree.LeafList, p => p.Id, p => p.Teammate, p => p.Teammates, pills);

    /// <summary>Claude Code's layout: lead 1 at 30%, teammates 2 and 3 stacked to its right (both parked).</summary>
    private static (LayoutTree<P> Tree, P Lead, P A, P B) Team()
    {
        var lead = new P(1);
        var a = new P(2, new Teammate { LeadId = 1, Name = "alpha" });
        var b = new P(3, new Teammate { LeadId = 1, Name = "beta" });
        var tree = new LayoutTree<P>(lead);
        tree.Split(lead, a, SplitAxis.Horizontal, ratio: 0.3);
        tree.Split(a, b, SplitAxis.Vertical);
        Sync(tree);
        return (tree, lead, a, b);
    }

    private static List<(int Id, RectI Rect)> Shown(LayoutTree<P> tree, bool includeHidden = false)
    {
        var list = new List<(P, RectI)>();
        tree.Layout(Bounds, 10, list, includeHidden: includeHidden);
        return list.Select(x => (x.Item1.Id, x.Item2)).ToList();
    }

    [Test]
    public async Task ParkedTeammatesTakeNoRoom()
    {
        // Arrange
        var (tree, _, _, _) = Team();
        var dividers = new List<LayoutTree<P>.Divider>();

        // Act
        var shown = Shown(tree);
        tree.Layout(Bounds, 10, [], dividers);

        // Assert
        await Assert.That(shown).IsEquivalentTo([(1, Bounds)], CollectionOrdering.Matching);
        await Assert.That(dividers).IsEmpty();
    }

    [Test]
    public async Task ParkedTeammateKeepsTheGeometryOfItsSplit()
    {
        // Arrange
        var (tree, _, _, _) = Team();

        // Act
        var all = Shown(tree, includeHidden: true);

        // Assert
        await Assert.That(all).IsEquivalentTo([
            (1, new RectI(0, 0, 297, 600)),
            (2, new RectI(307, 0, 693, 295)),
            (3, new RectI(307, 305, 693, 295)),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OpeningATeammateShowsItInItsPlace()
    {
        // Arrange
        var (tree, _, a, _) = Team();

        // Act
        a.Teammate!.Parked = false;
        var shown = Shown(tree);

        // Assert
        await Assert.That(shown).IsEquivalentTo([(1, new RectI(0, 0, 297, 600)), (2, new RectI(307, 0, 693, 600))], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ParkingAgainGivesTheRoomBack()
    {
        // Arrange
        var (tree, _, a, _) = Team();
        a.Teammate!.Parked = false;

        // Act
        a.Teammate.Parked = true;
        var shown = Shown(tree);

        // Assert
        await Assert.That(shown).IsEquivalentTo([(1, Bounds)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task NeighborSkipsParkedTeammates()
    {
        // Arrange
        var (tree, lead, _, _) = Team();

        // Act
        var right = tree.Neighbor(lead, Direction.Right, Bounds, 10);

        // Assert
        await Assert.That(right).IsNull();
    }

    [Test]
    public async Task DividerBoundsIgnoreAHiddenSibling()
    {
        // Arrange: the lead split by the user, beside the parked teammates.
        var (tree, lead, _, _) = Team();
        var shell = new P(9);
        tree.Split(lead, shell, SplitAxis.Vertical);
        var split = tree.Find(lead)!.Parent!;

        // Act
        var bounds = tree.BoundsOf(split, Bounds, 10);

        // Assert
        await Assert.That(bounds).IsEqualTo(Bounds);
    }

    [Test]
    public async Task LeadListsItsTeammatesInLayoutOrder()
    {
        // Act
        var (_, lead, a, b) = Team();

        // Assert
        await Assert.That(lead.Teammates).IsEquivalentTo([a, b], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ExitedTeammateLeavesTheLeadsList()
    {
        // Arrange
        var (tree, lead, _, b) = Team();

        // Act: kill-pane / the teammate exiting removes its pane.
        tree.Remove(b);
        Sync(tree);

        // Assert
        await Assert.That(lead.Teammates.Select(p => p.Id)).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OrphanedTeammatesOpen()
    {
        // Arrange
        var (tree, lead, a, b) = Team();

        // Act: the lead's pane closes (or moves to another tab).
        tree.Remove(lead);
        Sync(tree);
        var shown = Shown(tree).Select(x => x.Id).ToList();

        // Assert
        await Assert.That(shown).IsEquivalentTo([2, 3], CollectionOrdering.Matching);
        await Assert.That((a.Teammate!.LeadId, b.Teammate!.LeadId)).IsEqualTo((0, 0));
    }

    [Test]
    public async Task SplitModeShowsEveryTeammate()
    {
        // Arrange
        var (tree, lead, _, _) = Team();

        // Act: tmux_agent_panes = "split".
        Sync(tree, pills: false);
        var shown = Shown(tree).Select(x => x.Id).ToList();

        // Assert
        await Assert.That(shown).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(lead.Teammates).IsEmpty();
    }

    [Test]
    public async Task PresetOnTheTeamSubtreeLeavesOtherPanesAlone()
    {
        // Arrange: a shell left of the lead; Claude Code's teammates split off the lead.
        var shell = new P(9);
        var lead = new P(1);
        var tree = new LayoutTree<P>(shell);
        tree.Split(shell, lead, SplitAxis.Horizontal, ratio: 0.4);
        var a = new P(2, new Teammate { LeadId = 1 });
        var b = new P(3, new Teammate { LeadId = 1 });
        tree.Split(lead, a, SplitAxis.Horizontal, ratio: 0.3);
        tree.Split(a, b, SplitAxis.Vertical);
        var team = tree.CommonAncestor([lead, a, b])!;

        // Act
        tree.Replace(team, LayoutPresets.Build(LayoutPreset.MainVertical, [lead, a, b]));
        var shown = Shown(tree);

        // Assert
        await Assert.That(shown).IsEquivalentTo([(9, new RectI(0, 0, 396, 600)), (1, new RectI(406, 0, 594, 600))], CollectionOrdering.Matching);
        await Assert.That(tree.Root.Ratio).IsEqualTo(0.4);
    }

    [Test]
    public async Task LabelIsRebuiltOnlyWhenTheStateChanges()
    {
        // Arrange
        var t = new Teammate { Name = "alpha" };
        t.RefreshLabel(ErikTerm.Ui.AgentStatus.AgentState.Working, null);
        string first = t.Label;

        // Act
        bool again = t.RefreshLabel(ErikTerm.Ui.AgentStatus.AgentState.Working, null);
        bool changed = t.RefreshLabel(ErikTerm.Ui.AgentStatus.AgentState.Idle, null);

        // Assert
        await Assert.That(first).IsEqualTo("alpha · working");
        await Assert.That(again).IsFalse();
        await Assert.That(changed).IsTrue();
        await Assert.That(t.Label).IsEqualTo("alpha · idle");
    }

    [Test]
    public async Task SessionLeavesTeammatesOut()
    {
        // Arrange
        var (tree, lead, a, _) = Team();
        a.Teammate!.Parked = false;

        // Act
        var node = SessionLayout.Capture(tree, p => new SessionPane { Cwd = "/" + p.Id }, p => p.Teammate is not null);
        int focused = SessionLayout.LeafIndex(tree, lead, p => p.Teammate is not null);
        int teammate = SessionLayout.LeafIndex(tree, a, p => p.Teammate is not null);

        // Assert
        await Assert.That(node.IsSplit).IsFalse();
        await Assert.That(node.Pane?.Cwd).IsEqualTo("/1");
        await Assert.That((focused, teammate)).IsEqualTo((0, -1));
    }
}

public class TeammateConfigTests
{
    [Test]
    [Arguments("", TmuxAgentPanes.Pills)]
    [Arguments("tmux_agent_panes = \"split\"", TmuxAgentPanes.Split)]
    [Arguments("tmux_agent_panes = \"Pills\"", TmuxAgentPanes.Pills)]
    public async Task TmuxAgentPanesSetting(string toml, TmuxAgentPanes expected)
    {
        // Act
        var r = SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(r.Settings.TmuxAgentPanes).IsEqualTo(expected);
        await Assert.That(r.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task UnknownTmuxAgentPanesValueIsReported()
    {
        // Act
        var r = SettingsLoader.Parse("tmux_agent_panes = \"tabs\"", "cfg.toml");

        // Assert
        await Assert.That(r.Settings.TmuxAgentPanes).IsEqualTo(TmuxAgentPanes.Pills);
        await Assert.That(r.Diagnostics.Single()).Contains("expected \"pills\" or \"split\"");
    }

    [Test]
    [Arguments("ToggleAgentPane(2)", "2")]
    [Arguments("ToggleAgentPane", null)]
    public async Task ToggleAgentPaneParses(string text, string? arg)
    {
        // Act
        bool ok = ActionSpec.TryParse(text, out var action, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That((action.Name, action.Arg)).IsEqualTo(("ToggleAgentPane", arg));
    }
}
