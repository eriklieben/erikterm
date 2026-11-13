namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Carbonfox (dark), from nightfox.nvim (https://github.com/EdenEast/nightfox.nvim).</summary>
    public static readonly ColorScheme Carbonfox = BuiltIn(
        new("Carbonfox", 0xF2F4F8, 0x161616, 0xF2F4F8, 0x161616, 0x2A2A2A,
            [0x282828, 0xEE5396, 0x25BE6A, 0x08BDBA, 0x78A9FF, 0xBE95FF, 0x33B1FF, 0xDFDFE0],
            [0x484848, 0xF16DA6, 0x46C880, 0x2DC7C4, 0x8CB6FF, 0xC8A5FF, 0x52BDFF, 0xE4E4E5],
            Split: 0x424243, Accent: 0x78A9FF)
        {
            SelectionFg = 0xF2F4F8,
        },
        new SchemeCredit("nightfox.nvim", "Copyright (c) 2021 James Simpson", SchemeLicenses.Mit, "https://github.com/EdenEast/nightfox.nvim"));
}
