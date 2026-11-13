namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Rosé Pine (dark), from Rosé Pine for Ghostty (https://github.com/rose-pine/ghostty).</summary>
    public static readonly ColorScheme RosePine = BuiltIn(
        new("Rosé Pine", 0xE0DEF4, 0x191724, 0xE0DEF4, 0x191724, 0x403D52,
            [0x26233A, 0xEB6F92, 0x31748F, 0xF6C177, 0x9CCFD8, 0xC4A7E7, 0xEBBCBA, 0xE0DEF4],
            [0x6E6A86, 0xEB6F92, 0x31748F, 0xF6C177, 0x9CCFD8, 0xC4A7E7, 0xEBBCBA, 0xE0DEF4],
            Split: 0x403D52, Accent: 0xC4A7E7)
        {
            SelectionFg = 0xE0DEF4,
        },
        new SchemeCredit("Rosé Pine for Ghostty", "Copyright (c) Rosé Pine", SchemeLicenses.Mit, "https://github.com/rose-pine/ghostty"));
}
