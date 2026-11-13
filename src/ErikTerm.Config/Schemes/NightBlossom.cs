namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Night Blossom (dark), an erikterm original.</summary>
    public static readonly ColorScheme NightBlossom = BuiltIn(
        new("Night Blossom", 0xF4C9EA, 0x1C1222, 0xFF6FD2, 0x1C1222, 0x4D2850,
            [0x0D0810, 0xFF5F7E, 0x86D69C, 0xF3C77A, 0xA3A6FF, 0xFF6FD2, 0x7FD3E3, 0xF1DFED],
            [0x7C568F, 0xFF839B, 0x9FE1B1, 0xF8D598, 0xC7C9FF, 0xFF93DD, 0x9ADEEB, 0xFFFFFF],
            Split: 0x4D2850, Accent: 0xFF6FD2)
        {
            SelectionFg = 0xF4C9EA,
        });
}
