namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Nightfox (dark), from nightfox.nvim (https://github.com/EdenEast/nightfox.nvim).</summary>
    public static readonly ColorScheme Nightfox = BuiltIn(
        new("Nightfox", 0xCDCECF, 0x192330, 0xCDCECF, 0x192330, 0x2B3B51,
            [0x393B44, 0xC94F6D, 0x81B29A, 0xDBC074, 0x719CD6, 0x9D79D6, 0x63CDCF, 0xDFDFE0],
            [0x575860, 0xD16983, 0x8EBAA4, 0xE0C989, 0x86ABDC, 0xBAA1E2, 0x7AD5D6, 0xE4E4E5],
            Split: 0x2B3B51, Accent: 0x719CD6)
        {
            SelectionFg = 0xCDCECF,
        },
        new SchemeCredit("nightfox.nvim", "Copyright (c) 2021 James Simpson", SchemeLicenses.Mit, "https://github.com/EdenEast/nightfox.nvim"));
}
