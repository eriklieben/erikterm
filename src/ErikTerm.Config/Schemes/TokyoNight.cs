namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Tokyo Night (dark), from tokyonight.nvim (https://github.com/folke/tokyonight.nvim).</summary>
    public static readonly ColorScheme TokyoNight = BuiltIn(
        new("Tokyo Night", 0xC0CAF5, 0x1A1B26, 0xC0CAF5, 0x1A1B26, 0x33467C,
            [0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6],
            [0x414868, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xC0CAF5],
            Split: 0x3B4261, Accent: 0x7AA2F7),
        new SchemeCredit("tokyonight.nvim", "By Folke Lemaitre and the tokyonight.nvim contributors", SchemeLicenses.Apache2, "https://github.com/folke/tokyonight.nvim"));
}
