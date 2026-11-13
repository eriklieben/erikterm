namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Onyx & Rose (dark), an erikterm original.</summary>
    public static readonly ColorScheme OnyxAndRose = BuiltIn(
        new("Onyx & Rose", 0xEBE3DA, 0x1F1D1D, 0xE8A5AB, 0x1F1D1D, 0x4A3A3C,
            [0x121010, 0xE8737F, 0xA5C48E, 0xE3C07E, 0x8FB0D8, 0xE8A5AB, 0x8CCAC4, 0xECE9E5],
            [0x726565, 0xEF8F99, 0xB8D1A4, 0xEBCF99, 0xA8C3E3, 0xF0C0C5, 0xA3D6D2, 0xFFFFFF],
            Split: 0x4A3A3C, Accent: 0xE8A5AB)
        {
            SelectionFg = 0xEBE3DA,
        });
}
