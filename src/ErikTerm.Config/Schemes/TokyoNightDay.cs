namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Tokyo Night Day (light), from tokyonight.nvim (https://github.com/folke/tokyonight.nvim).</summary>
    public static readonly ColorScheme TokyoNightDay = BuiltIn(
        new("Tokyo Night Day", 0x3760BF, 0xE1E2E7, 0x3760BF, 0xE1E2E7, 0xB7C1E3,
            [0xB4B5B9, 0xF52A65, 0x587539, 0x8C6C3E, 0x2E7DE9, 0x9854F1, 0x007197, 0x6172B0],
            [0xA1A6C5, 0xFF4774, 0x5C8524, 0xA27629, 0x358AFF, 0xA463FF, 0x007EA8, 0x3760BF],
            Split: 0x2E7DE9, Accent: 0x2E7DE9)
        {
            SelectionFg = 0x3760BF,
        },
        new SchemeCredit("tokyonight.nvim", "By Folke Lemaitre and the tokyonight.nvim contributors", SchemeLicenses.Apache2, "https://github.com/folke/tokyonight.nvim"));
}
