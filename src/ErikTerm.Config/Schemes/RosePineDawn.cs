namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Rosé Pine Dawn (light), from Rosé Pine for Ghostty (https://github.com/rose-pine/ghostty).</summary>
    public static readonly ColorScheme RosePineDawn = BuiltIn(
        new("Rosé Pine Dawn", 0x575279, 0xFAF4ED, 0x575279, 0xFAF4ED, 0xDFDAD9,
            [0xF2E9E1, 0xB4637A, 0x286983, 0xEA9D34, 0x56949F, 0x907AA9, 0xD7827E, 0x575279],
            [0x9893A5, 0xB4637A, 0x286983, 0xEA9D34, 0x56949F, 0x907AA9, 0xD7827E, 0x575279],
            Split: 0xD9D4D6, Accent: 0x907AA9)
        {
            SelectionFg = 0x575279,
        },
        new SchemeCredit("Rosé Pine for Ghostty", "Copyright (c) Rosé Pine", SchemeLicenses.Mit, "https://github.com/rose-pine/ghostty"));
}
