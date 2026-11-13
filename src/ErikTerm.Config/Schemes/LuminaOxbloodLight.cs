namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Lumina Oxblood Light (light), from Lumina themes (https://github.com/RazikSF/lumina-themes).</summary>
    public static readonly ColorScheme LuminaOxbloodLight = BuiltIn(
        new("Lumina Oxblood Light", 0x42222F, 0xFEF2F4, 0x42222F, 0xFEF2F4, 0xE6B8CB,
            [0x42222F, 0x9B2827, 0x237742, 0xA1600A, 0x3166A6, 0x82469D, 0x087380, 0x8A6472],
            [0x7D5D68, 0x732926, 0x0F6131, 0x874E00, 0x1C5290, 0x683A7D, 0x005D68, 0xAB8392],
            Split: 0xDECED1, Accent: 0xA62D66)
        {
            SelectionFg = 0x42222F,
        },
        new SchemeCredit("Lumina themes", "Copyright (c) 2026 Razik Ikhlef", SchemeLicenses.Mit, "https://github.com/RazikSF/lumina-themes"));
}
