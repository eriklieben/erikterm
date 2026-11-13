namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Rosé Pine Moon (dark), from Rosé Pine for Ghostty (https://github.com/rose-pine/ghostty).</summary>
    public static readonly ColorScheme RosePineMoon = BuiltIn(
        new("Rosé Pine Moon", 0xE0DEF4, 0x232136, 0xE0DEF4, 0x232136, 0x44415A,
            [0x393552, 0xEB6F92, 0x3E8FB0, 0xF6C177, 0x9CCFD8, 0xC4A7E7, 0xEA9A97, 0xE0DEF4],
            [0x6E6A86, 0xEB6F92, 0x3E8FB0, 0xF6C177, 0x9CCFD8, 0xC4A7E7, 0xEA9A97, 0xE0DEF4],
            Split: 0x44415A, Accent: 0xC4A7E7)
        {
            SelectionFg = 0xE0DEF4,
        },
        new SchemeCredit("Rosé Pine for Ghostty", "Copyright (c) Rosé Pine", SchemeLicenses.Mit, "https://github.com/rose-pine/ghostty"));
}
