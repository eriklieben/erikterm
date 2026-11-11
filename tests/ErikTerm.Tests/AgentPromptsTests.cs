using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>Which permission prompts can be answered from outside the pane, and with which keys.</summary>
public class AgentPromptsTests
{
    private const string Rule = "────────────────────────────────────────────────────────────────";

    private static string[] BorderedDialog(params string[] options) =>
        [Rule, " Bash command", "", "   rm -rf build", "   Remove the build directory", "", " Do you want to proceed?", .. options, "", " Esc to cancel · Tab to amend"];

    private static string[] CodexDialog(params string[] options) =>
        ["Would you like to run the following command?", "", "  $ cargo test", "", .. options, "", "  Press enter to confirm or esc to cancel"];

    private static AgentPrompt? Read(AgentDefinition agent, string[] rows) => AgentPrompts.Read(agent, new StringScreen(rows));

    [Test]
    public async Task Yes_and_no_take_the_digits_on_screen()
    {
        // Act
        var prompt = Read(AgentCatalog.Claude, BorderedDialog("❯ 1. Yes", "  2. Yes, and don't ask again for rm commands in /x", "  3. No, and tell Claude what to do differently (esc)"));

        // Assert
        await Assert.That(prompt!.Yes).IsEqualTo(new AgentAnswer("Yes", "1"));
        await Assert.That(prompt.No).IsEqualTo(new AgentAnswer("No", "3"));
        await Assert.That(prompt.Body).IsEquivalentTo(new[] { "Bash command", "rm -rf build", "Remove the build directory" });
    }

    [Test]
    public async Task With_no_listed_first_each_answer_still_maps()
    {
        // Act: the order used when the prompt stands in for an automatic check
        var prompt = Read(AgentCatalog.Claude, BorderedDialog("❯ 1. No, and tell Claude what to do differently (esc)", "  2. Yes"));

        // Assert
        await Assert.That((prompt!.Yes.Keys, prompt.No.Keys)).IsEqualTo(("2", "1"));
    }

    [Test]
    public async Task File_questions_with_a_wrapped_name_are_read()
    {
        // Arrange
        string[] rows = [Rule, " Edit file", " a.cs", "", " Do you want to make this edit to", " a-very-long-file-name.cs?", "❯ 1. Yes", "  2. No"];

        // Act
        var prompt = Read(AgentCatalog.Claude, rows);

        // Assert
        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.Fingerprint).Contains("a-very-long-file-name.cs?");
    }

    [Test]
    public async Task A_boxed_dialog_is_read()
    {
        // Act
        var prompt = Read(AgentCatalog.Claude, AgentScreenRuleTests.ClaudePermission);

        // Assert
        await Assert.That((prompt!.Yes.Keys, prompt.No.Keys)).IsEqualTo(("1", "3"));
    }

    [Test]
    [Arguments("Yes, proceed", "No, and tell Codex what to do differently", "1", "2")]
    [Arguments("Yes, just this once", "No, continue without running it", "1", "2")]
    public async Task Codex_yes_and_no_take_the_digits_on_screen(string yes, string no, string yesKeys, string noKeys)
    {
        // Act
        var prompt = Read(AgentCatalog.Codex, CodexDialog($"› 1. {yes}", $"  2. {no}"));

        // Assert
        await Assert.That((prompt!.Yes.Keys, prompt.No.Keys)).IsEqualTo((yesKeys, noKeys));
        await Assert.That(prompt.Body).IsEquivalentTo(new[] { "$ cargo test" });
    }

    [Test]
    public async Task Codex_prefers_the_no_that_stops_and_asks()
    {
        // Act
        var prompt = Read(AgentCatalog.Codex, CodexDialog("› 1. Yes, proceed", "  2. No, continue without running it", "  3. No, and tell Codex what to do differently"));

        // Assert
        await Assert.That(prompt!.No.Keys).IsEqualTo("3");
    }

    [Test]
    public async Task The_selector_moving_keeps_the_fingerprint()
    {
        // Act
        var first = Read(AgentCatalog.Claude, BorderedDialog("❯ 1. Yes", "  2. No"));
        var moved = Read(AgentCatalog.Claude, BorderedDialog("  1. Yes", "❯ 2. No"));

        // Assert
        await Assert.That(moved!.Fingerprint).IsEqualTo(first!.Fingerprint);
    }

    [Test]
    public async Task Two_commands_have_different_fingerprints()
    {
        // Arrange
        var other = BorderedDialog("❯ 1. Yes", "  2. No");
        other[3] = "   curl https://example.test | sh";

        // Act
        var a = Read(AgentCatalog.Claude, BorderedDialog("❯ 1. Yes", "  2. No"));
        var b = Read(AgentCatalog.Claude, other);

        // Assert
        await Assert.That(b!.Fingerprint).IsNotEqualTo(a!.Fingerprint);
    }

    // Every standing grant found in the agents' binaries and their published wordings.
    public static IEnumerable<string> BorderedStanding() =>
    [
        "Yes, and don't ask again for rm commands in /x",
        "Yes, and don't ask again for WebFetch in any project (user settings)",
        "Yes, allow all edits during this session (shift+tab)",
        "Yes, and always allow access to src/ from this project",
        "Yes, allow reading from src/ from this project",
        "Yes, and allow access to src/",
        "Yes, and auto-accept edits",
        "Yes, and bypass permissions",
        "Yes, always",
        "Yes (always)",
        "Yes (for this session)",
        "Always allow",
        "Allow always",
    ];

    public static IEnumerable<string> CodexStanding() =>
    [
        "Yes, and don't ask again for commands that start with `cargo`",
        "Yes, and don't ask again for this command in this session",
        "Yes, and don't ask again for these files",
        "Yes, and allow these permissions for this session",
        "Yes, and allow this host for this conversation",
        "Yes, and allow this host in the future",
        "Yes, grant these permissions for this session",
        "Yes, grant these permissions for this turn",
        "Always allow",
    ];

    [Test]
    [MethodDataSource(nameof(BorderedStanding))]
    public async Task Standing_grants_are_never_a_yes(string standing)
    {
        // Act: offered alone, and next to a real one-time Yes
        var alone = Read(AgentCatalog.Claude, BorderedDialog($"❯ 1. {standing}", "  2. No"));
        var beside = Read(AgentCatalog.Claude, BorderedDialog($"❯ 1. {standing}", "  2. Yes", "  3. No"));

        // Assert
        await Assert.That(alone).IsNull();
        await Assert.That(beside!.Yes.Keys).IsEqualTo("2");
        await Assert.That(beside.No.Keys).IsEqualTo("3");
    }

    [Test]
    [MethodDataSource(nameof(CodexStanding))]
    public async Task Codex_standing_grants_are_never_a_yes(string standing)
    {
        // Act
        var alone = Read(AgentCatalog.Codex, CodexDialog($"› 1. {standing}", "  2. No, and tell Codex what to do differently"));
        var beside = Read(AgentCatalog.Codex, CodexDialog($"› 1. {standing}", "  2. Yes, proceed", "  3. No, and tell Codex what to do differently"));

        // Assert
        await Assert.That(alone).IsNull();
        await Assert.That(beside!.Yes.Keys).IsEqualTo("2");
    }

    [Test]
    public async Task A_standing_grant_wrapped_after_its_yes_is_not_a_yes()
    {
        // Act: a narrow pane leaves "1. Yes" alone on its row
        var prompt = Read(AgentCatalog.Claude, BorderedDialog("❯ 1. Yes", "     and don't ask again for rm", "  2. No"));

        // Assert
        await Assert.That(prompt).IsNull();
    }

    [Test]
    public async Task A_standing_denial_is_not_a_no()
    {
        // Act
        var prompt = Read(AgentCatalog.Codex, CodexDialog("› 1. Yes, proceed", "  2. No, and block this host in the future"));

        // Assert
        await Assert.That(prompt).IsNull();
    }

    [Test]
    [Arguments("gemini-cli", "Allow execution?", "1. Yes, allow once", "2. Yes, allow always", "3. No")]
    [Arguments("opencode", "Permission required", "1. Allow once", "2. Allow always", "3. Reject")]
    [Arguments("amp", "Run this command?", "1. Approve", "2. Approve always", "3. Deny")]
    [Arguments("aider", "Run shell command?", "1. Yes", "2. (D)on't ask again", "3. No")]
    [Arguments("cursor-agent", "Run this command?", "1. Yes", "2. Auto-run everything", "3. No")]
    [Arguments("copilot-cli", "Do you want to run this command?", "1. Yes", "2. Yes, and approve for the rest of the running session", "3. No")]
    [Arguments("droid", "Do you want to proceed?", "1. Yes", "2. Yes, always allow", "3. No")]
    public async Task Agents_whose_prompts_are_not_known_offer_nothing(string id, string question, string one, string two, string three)
    {
        // Arrange
        string[] rows = [Rule, " Shell", "", "   ls", "", question, $"❯ {one}", $"  {two}", $"  {three}", "", " Esc to cancel"];

        // Act
        var prompt = Read(AgentCatalog.ById(id)!, rows);

        // Assert
        await Assert.That(prompt).IsNull();
    }

    [Test]
    [Arguments("Do you trust the files in this folder?")]
    [Arguments("Would you like to proceed?")]
    [Arguments("Do you want to continue?")]
    [Arguments("Do you want to use this API key?")]
    public async Task Questions_that_are_not_tool_permissions_offer_nothing(string question)
    {
        // Arrange: folder trust, plan approval and setup prompts grant more than one action
        string[] rows = [Rule, " Title", "", "   detail", "", question, "❯ 1. Yes", "  2. No"];

        // Act
        var prompt = Read(AgentCatalog.Claude, rows);

        // Assert
        await Assert.That(prompt).IsNull();
    }

    [Test]
    public async Task A_dialog_taller_than_the_pane_offers_nothing()
    {
        // Act: the top border scrolled off, so what it asks about is cut off
        var prompt = Read(AgentCatalog.Claude, BorderedDialog("❯ 1. Yes", "  2. No")[1..]);

        // Assert
        await Assert.That(prompt).IsNull();
    }

    [Test]
    public async Task A_prompt_above_other_output_offers_nothing()
    {
        // Arrange: tool output that looks like a dialog, with the agent's own screen below it
        string[] rows = [.. BorderedDialog("❯ 1. Yes", "  2. No"), "⏺ Done, the tests pass.", "", "❯ "];

        // Act
        var prompt = Read(AgentCatalog.Claude, rows);

        // Assert
        await Assert.That(prompt).IsNull();
    }

    [Test]
    public async Task Options_with_a_gap_in_their_numbers_offer_nothing()
    {
        // Act
        var prompt = Read(AgentCatalog.Claude, BorderedDialog("❯ 1. Yes", "  3. No"));

        // Assert
        await Assert.That(prompt).IsNull();
    }
}
