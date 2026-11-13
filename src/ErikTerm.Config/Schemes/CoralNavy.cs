namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Coral Navy (dark), an erikterm original.</summary>
    public static readonly ColorScheme CoralNavy = BuiltIn(
        new("Coral Navy", 0xF5EFED, 0x26334A, 0xFF7A90, 0x26334A, 0x3C4D6B,
            [0x18202F, 0xFF6B7F, 0x8FD19E, 0xF2C879, 0x6FA8DC, 0xFF8FB1, 0x5FBFC4, 0xE4E8ED],
            [0x586787, 0xFF8F9E, 0xA7DDB3, 0xF7D697, 0x8AB9E5, 0xFFB3CA, 0x77CBD0, 0xFFFFFF],
            Split: 0x3C4D6B, Accent: 0xFF7A90)
        {
            SelectionFg = 0xF5EFED,
        });
}
