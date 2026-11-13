namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Everforest Dark (dark), from Everforest (https://github.com/sainnhe/everforest).</summary>
    public static readonly ColorScheme EverforestDark = BuiltIn(
        new("Everforest Dark", 0xD3C6AA, 0x2D353B, 0xD3C6AA, 0x2D353B, 0x543A48,
            [0x475258, 0xE67E80, 0xA7C080, 0xDBBC7F, 0x7FBBB3, 0xD699B6, 0x83C092, 0xD3C6AA],
            [0x475258, 0xE67E80, 0xA7C080, 0xDBBC7F, 0x7FBBB3, 0xD699B6, 0x83C092, 0xD3C6AA],
            Split: 0x4E5251, Accent: 0xA7C080),
        new SchemeCredit("Everforest", "Copyright (c) 2019 sainnhe", SchemeLicenses.Mit, "https://github.com/sainnhe/everforest")
        {
            Notes = "Colours from the vim palette (medium) with its terminal colour mapping.",
        });
}
