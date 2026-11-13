namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Tokyo Night Storm (dark), from tokyonight.nvim (https://github.com/folke/tokyonight.nvim).</summary>
    public static readonly ColorScheme TokyoNightStorm = BuiltIn(
        new("Tokyo Night Storm", 0xC0CAF5, 0x24283B, 0xC0CAF5, 0x24283B, 0x2E3C64,
            [0x1D202F, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6],
            [0x414868, 0xFF899D, 0x9FE044, 0xFABA4A, 0x8DB0FF, 0xC7A9FF, 0xA4DAFF, 0xC0CAF5],
            Split: 0x7AA2F7, Accent: 0x7AA2F7)
        {
            SelectionFg = 0xC0CAF5,
        },
        new SchemeCredit("tokyonight.nvim", "By Folke Lemaitre and the tokyonight.nvim contributors", SchemeLicenses.Apache2, "https://github.com/folke/tokyonight.nvim"));
}
