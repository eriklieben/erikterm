namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Fairy Floss (dark), from fairyfloss (https://github.com/sailorhg/fairyfloss).</summary>
    public static readonly ColorScheme FairyFloss = BuiltIn(
        new("Fairy Floss", 0xF8F8F2, 0x5A5475, 0xF8F8F0, 0x060709, 0x8077A8,
            [0x040303, 0xF92672, 0xC2FFDF, 0xE6C000, 0xC2FFDF, 0xFFB8D1, 0xC5A3FF, 0xF8F8F0],
            [0x6090CB, 0xFF857F, 0xC2FFDF, 0xFFEA00, 0xC2FFDF, 0xFFB8D1, 0xC5A3FF, 0xF8F8F0],
            Split: 0x8077A8, Accent: 0xFFB8D1)
        {
            SelectionFg = 0xF6E1CE,
        },
        new SchemeCredit("fairyfloss", "Copyright (c) Amy Wibowo (sailorhg)", SchemeLicenses.Mit, "https://github.com/sailorhg/fairyfloss")
        {
            Notes = "MIT as stated in its package.json. The terminal colour mapping is the iTerm2-Color-Schemes port (MIT).",
        });
}
