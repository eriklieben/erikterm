namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>GitHub Light (light), from GitHub VS Code theme (Primer) (https://github.com/primer/github-vscode-theme).</summary>
    public static readonly ColorScheme GitHubLight = BuiltIn(
        new("GitHub Light", 0x1F2328, 0xFFFFFF, 0x0969DA, 0xFFFFFF, 0xB5D2F4,
            [0x24292F, 0xCF222E, 0x116329, 0x4D2D00, 0x0969DA, 0x8250DF, 0x1B7C83, 0x6E7781],
            [0x57606A, 0xA40E26, 0x1A7F37, 0x633C01, 0x218BFF, 0xA475F9, 0x3192AA, 0x8C959F],
            Split: 0xB5D2F4, Accent: 0x0969DA),
        new SchemeCredit("GitHub VS Code theme (Primer)", "Copyright (c) 2020 Primer", SchemeLicenses.Mit, "https://github.com/primer/github-vscode-theme"));
}
