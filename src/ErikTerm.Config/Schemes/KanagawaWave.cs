namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Kanagawa Wave (dark), from kanagawa.nvim (https://github.com/rebelot/kanagawa.nvim).</summary>
    public static readonly ColorScheme KanagawaWave = BuiltIn(
        new("Kanagawa Wave", 0xDCD7BA, 0x1F1F28, 0xC8C093, 0x1F1F28, 0x2D4F67,
            [0x090618, 0xC34043, 0x76946A, 0xC0A36E, 0x7E9CD8, 0x957FB8, 0x6A9589, 0xC8C093],
            [0x727169, 0xE82424, 0x98BB6C, 0xE6C384, 0x7FB4CA, 0x938AA9, 0x7AA89F, 0xDCD7BA],
            Split: 0x2D4F67, Accent: 0x7E9CD8)
        {
            SelectionFg = 0xC8C093,
        },
        new SchemeCredit("kanagawa.nvim", "Copyright (c) 2021 Tommaso Laurenzi", SchemeLicenses.Mit, "https://github.com/rebelot/kanagawa.nvim")
        {
            Notes = "The cursor text is the background colour (upstream reverses the cursor instead).",
        });
}
