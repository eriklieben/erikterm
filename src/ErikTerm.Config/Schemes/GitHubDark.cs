namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>GitHub Dark (dark), from GitHub VS Code theme (Primer) (https://github.com/primer/github-vscode-theme).</summary>
    public static readonly ColorScheme GitHubDark = BuiltIn(
        new("GitHub Dark", 0xE6EDF3, 0x0D1117, 0x58A6FF, 0x0D1117, 0x243E5D,
            [0x484F58, 0xFF7B72, 0x3FB950, 0xD29922, 0x58A6FF, 0xBC8CFF, 0x39C5CF, 0xB1BAC4],
            [0x6E7681, 0xFFA198, 0x56D364, 0xE3B341, 0x79C0FF, 0xD2A8FF, 0x56D4DD, 0xFFFFFF],
            Split: 0x243E5D, Accent: 0x58A6FF),
        new SchemeCredit("GitHub VS Code theme (Primer)", "Copyright (c) 2020 Primer", SchemeLicenses.Mit, "https://github.com/primer/github-vscode-theme")
        {
            Notes = "The selection is the background mixed with 30% of the accent.",
        });
}
