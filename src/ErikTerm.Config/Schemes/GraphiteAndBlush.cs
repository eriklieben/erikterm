namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Graphite & Blush (dark), an erikterm original.</summary>
    public static readonly ColorScheme GraphiteAndBlush = BuiltIn(
        new("Graphite & Blush", 0xD9DBE0, 0x2E3035, 0xF2779B, 0x2E3035, 0x4B4650,
            [0x1C1D21, 0xF27088, 0x9CCB8A, 0xE8C27A, 0x7FA6D6, 0xF2779B, 0x79C6C8, 0xE8E9EB],
            [0x646776, 0xF78EA2, 0xB0D7A1, 0xEFD196, 0x98B9E0, 0xF795B2, 0x91D2D4, 0xFFFFFF],
            Split: 0x4B4650, Accent: 0xF2779B)
        {
            SelectionFg = 0xD9DBE0,
        });
}
