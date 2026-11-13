namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Abyss & Brass (dark), an erikterm original.</summary>
    public static readonly ColorScheme AbyssAndBrass = BuiltIn(
        new("Abyss & Brass", 0xD6E2EC, 0x10213A, 0xEBB723, 0x10213A, 0x24405F,
            [0x0A1526, 0xEF6B6B, 0x7CC79A, 0xEBB723, 0x5FA8E0, 0xC49BE0, 0x4FC3C3, 0xE4EAEF],
            [0x566984, 0xF58989, 0x93D3AD, 0xF2C43F, 0x7AB9E8, 0xD4B5EA, 0x67CFCF, 0xFFFFFF],
            Split: 0x24405F, Accent: 0xEBB723)
        {
            SelectionFg = 0xD6E2EC,
        });
}
