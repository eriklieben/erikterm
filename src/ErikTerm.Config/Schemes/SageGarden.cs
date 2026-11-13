namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Sage Garden (light), an erikterm original.</summary>
    public static readonly ColorScheme SageGarden = BuiltIn(
        new("Sage Garden", 0x2D3B37, 0xE9EFE9, 0x5F8A7F, 0xE9EFE9, 0xC9DBD2,
            [0x070A09, 0xBE5F5C, 0x558843, 0x9B7617, 0x4A81A9, 0x9C6AA1, 0x328881, 0xEBEEED],
            [0x557068, 0xC46D6A, 0x5C9348, 0xA77F19, 0x538BB4, 0xA475A8, 0x36938C, 0xEBEEED],
            Split: 0xC3CBC5, Accent: 0x5F8A7F)
        {
            SelectionFg = 0x2D3B37,
        });
}
