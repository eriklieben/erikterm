namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Neon Grid (dark), an erikterm original.</summary>
    public static readonly ColorScheme NeonGrid = BuiltIn(
        new("Neon Grid", 0xE2DCF2, 0x1C1433, 0xFF3EA5, 0x1C1433, 0x3D2A63,
            [0x120C24, 0xFF4F7B, 0x3EE6A8, 0xFFD166, 0x5AA9FF, 0xFF3EA5, 0x22D3E6, 0xE6E3F1],
            [0x685E90, 0xFF7396, 0x5AEEB7, 0xFFDC8A, 0x7EBCFF, 0xFF62B6, 0x3EDDEE, 0xFFFFFF],
            Split: 0x3D2A63, Accent: 0xFF3EA5)
        {
            SelectionFg = 0xE2DCF2,
        });
}
