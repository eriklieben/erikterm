namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Cream & Rose (light), an erikterm original.</summary>
    public static readonly ColorScheme CreamAndRose = BuiltIn(
        new("Cream & Rose", 0x2B2626, 0xF3ECE4, 0xC4707A, 0xF3ECE4, 0xEDCFCF,
            [0x090808, 0xC35A65, 0x61854B, 0xA07418, 0x5C7DAB, 0xB36282, 0x408683, 0xEEEDEC],
            [0x756767, 0xC86872, 0x699051, 0xAC7D1A, 0x6887B1, 0xB96E8C, 0x45918E, 0xEEEDEC],
            Split: 0xCBC4BE, Accent: 0xB0646E)
        {
            SelectionFg = 0x2B2626,
        });
}
