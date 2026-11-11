namespace ErikTerm.Ui.AgentStatus;

/// <summary>A one-time answer to a permission prompt: its label and the keys that pick it.</summary>
public sealed record AgentAnswer(string Label, string Keys);

/// <summary>
/// A yes/no permission prompt read off an agent's screen. <paramref name="Fingerprint"/> is the prompt's text (what it
/// asks about, the question, every option), so two prompts can be told apart; <paramref name="Body"/> is what it asks
/// about, for the user to read before answering.
/// </summary>
public sealed record AgentPrompt(string Fingerprint, string[] Body, AgentAnswer Yes, AgentAnswer No);

/// <summary>
/// Reads the permission prompts that can be answered from outside the pane, or null for anything else. Everything is an
/// allow list of exact wordings taken from the agents themselves, so a wording that isn't here, a standing grant
/// ("don't ask again", "for this session", …) or a prompt only partly on screen is not offered.
/// The other agents' prompts aren't known well enough to answer: their rules in <see cref="AgentCatalog"/> only say
/// that they wait. The number is read from the screen because the order varies, and picks the option at once: no Enter.
/// </summary>
public static class AgentPrompts
{
    /// <param name="QuestionFirst">The question heads the prompt, rather than sitting right above the options.</param>
    private sealed record Spec(bool QuestionFirst, string[] Questions, string[] Yes, string[] No);

    private static readonly Spec Claude = new(false,
        ["Do you want to proceed?", "Do you want to make this edit to ", "Do you want to create ", "Do you want to overwrite ", "Do you want to write to ",
            "Do you want to allow Claude to fetch this content?", "Do you want to allow this connection?"],
        ["Yes"],
        ["No, and tell Claude what to do differently", "No"]);

    private static readonly Spec Codex = new(true,
        ["Would you like to run the following command?", "Would you like to make the following edits?"],
        ["Yes, proceed", "Yes, just this once"],
        ["No, and tell Codex what to do differently", "No, continue without running it"]);

    private const int MaxQuestionRows = 3; // a long file name wraps the question
    private const int MinBorderChars = 8;

    public static AgentPrompt? Read(AgentDefinition agent, IScreenText screen)
    {
        var spec = agent.Id switch { "claude-code" => Claude, "codex" => Codex, _ => null };
        if (spec is null) return null;
        var rows = Rows(screen);
        int y = rows.Length - 1;
        while (y >= 0 && IsFooter(rows[y])) y--;
        if (ReadOptions(rows, ref y) is not { } options) return null;
        if ((spec.QuestionFirst ? QuestionOnTop(rows, y, spec) : QuestionAboveOptions(rows, y, spec)) is not { } block) return null;
        if (block.Body.Length == 0) return null;
        if (Find(options, spec.Yes) is not { } yes || Find(options, spec.No) is not { } no) return null;
        string fingerprint = string.Join('\n', [.. block.Body, block.Question, .. options.Select(o => o.Label)]);
        return new AgentPrompt(fingerprint, block.Body, new AgentAnswer("Yes", yes), new AgentAnswer("No", no));
    }

    private static string[] Rows(IScreenText screen)
    {
        var rows = new string[screen.RowCount];
        for (int y = 0; y < rows.Length; y++) rows[y] = screen.Row(y).ToString().Trim().Trim('│').Trim();
        return rows;
    }

    private static bool IsFooter(string row) => !TryOption(row, out _, out _)
        && (row.Length == 0 || IsBorder(row) || row.Contains("to cancel", StringComparison.OrdinalIgnoreCase) || row.Contains("to confirm", StringComparison.OrdinalIgnoreCase));

    private static bool IsBorder(string row)
    {
        var line = row.AsSpan().Trim("╭╮╰╯");
        return line.Length >= MinBorderChars && line.TrimStart('─').IsEmpty;
    }

    /// <summary>
    /// The numbered options ending at row <paramref name="y"/>, bottom up; <paramref name="y"/> ends above option 1. A row
    /// that isn't an option continues the one above it: a narrow pane wraps "Yes, and don't ask again" after its "Yes".
    /// </summary>
    private static List<(int Number, string Label)>? ReadOptions(string[] rows, ref int y)
    {
        var options = new List<(int Number, string Label)>();
        string rest = "";
        for (int row = y; row >= 0; row--)
        {
            if (rows[row].Length == 0) return null;
            if (!TryOption(rows[row], out int number, out string label)) { rest = Join(rows[row], rest); continue; }
            options.Insert(0, (number, Join(label, rest)));
            rest = "";
            if (number == 1) { y = row - 1; return Numbered(options) ? options : null; }
        }
        return null;
    }

    private static bool Numbered(List<(int Number, string Label)> options)
    {
        for (int i = 0; i < options.Count; i++) if (options[i].Number != i + 1) return false;
        return true;
    }

    private static bool TryOption(string row, out int number, out string label)
    {
        (number, label) = (0, "");
        var s = row.AsSpan().TrimStart().TrimStart("❯›>").TrimStart();
        if (s.Length < 4 || s[0] is < '1' or > '9' || s[1] != '.' || s[2] != ' ') return false;
        number = s[0] - '0';
        label = s[3..].Trim().ToString();
        return true;
    }

    private static string Join(string a, string b) => b.Length == 0 ? a : $"{a} {b}";

    /// <summary>The question sits right above the options; what it asks about runs up to the dialog's top border.</summary>
    private static (string Question, string[] Body)? QuestionAboveOptions(string[] rows, int y, Spec spec)
    {
        string question = "";
        for (int i = 0; i < MaxQuestionRows && y - i >= 0; i++)
        {
            if (rows[y - i].Length == 0 || IsBorder(rows[y - i])) return null;
            question = Join(rows[y - i], question);
            if (IsQuestion(question, spec)) return BodyUnderBorder(rows, y - i - 1) is { } body ? (question, body) : null;
        }
        return null;
    }

    /// <summary>Null when the top border isn't on screen: the dialog is taller than the pane, and what it asks about is cut off.</summary>
    private static string[]? BodyUnderBorder(string[] rows, int y)
    {
        var body = new List<string>();
        for (; y >= 0; y--)
        {
            if (IsBorder(rows[y])) { body.Reverse(); return [.. body]; }
            if (rows[y].Length > 0) body.Add(rows[y]);
        }
        return null;
    }

    /// <summary>The question heads the prompt, with the command or diff between it and the options.</summary>
    private static (string Question, string[] Body)? QuestionOnTop(string[] rows, int y, Spec spec)
    {
        for (int q = y; q >= 0 && !IsBorder(rows[q]); q--)
            if (IsQuestion(rows[q], spec))
                return (rows[q], [.. rows[(q + 1)..(y + 1)].Where(r => r.Length > 0)]);
        return null;
    }

    private static bool IsQuestion(string text, Spec spec) =>
        text.EndsWith('?') && spec.Questions.Any(q => text.StartsWith(q, StringComparison.Ordinal));

    /// <summary>The keys of the option with the first of <paramref name="labels"/> that is on offer; an option's "(esc)" hint doesn't count.</summary>
    private static string? Find(List<(int Number, string Label)> options, string[] labels)
    {
        foreach (var wanted in labels)
            foreach (var (number, label) in options)
                if (string.Equals(WithoutHint(label), wanted, StringComparison.Ordinal)) return number.ToString();
        return null;
    }

    /// <summary>Drops a key hint, "(esc)" or "(y)", and nothing else: "Yes (always)" must not read as "Yes".</summary>
    private static string WithoutHint(string label)
    {
        int open = label.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0 || label[^1] != ')') return label;
        var hint = label.AsSpan()[(open + 2)..^1];
        return hint.Length == 1 || hint.SequenceEqual("esc") ? label[..open] : label;
    }
}
