namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Everforest Light (light), from Everforest (https://github.com/sainnhe/everforest).</summary>
    public static readonly ColorScheme EverforestLight = BuiltIn(
        new("Everforest Light", 0x5C6A72, 0xFDF6E3, 0x5C6A72, 0xFDF6E3, 0xEAEDC8,
            [0x5C6A72, 0xF85552, 0x8DA101, 0xDFA000, 0x3A94C5, 0xDF69BA, 0x35A77C, 0xE6E2CC],
            [0x5C6A72, 0xF85552, 0x8DA101, 0xDFA000, 0x3A94C5, 0xDF69BA, 0x35A77C, 0xE6E2CC],
            Split: 0xDDDACC, Accent: 0x809201),
        new SchemeCredit("Everforest", "Copyright (c) 2019 sainnhe", SchemeLicenses.Mit, "https://github.com/sainnhe/everforest")
        {
            Notes = "Colours from the vim palette (light, medium) with its terminal colour mapping.",
        });
}
