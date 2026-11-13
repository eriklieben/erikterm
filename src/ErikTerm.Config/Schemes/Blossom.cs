namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Blossom (light), an erikterm original.</summary>
    public static readonly ColorScheme Blossom = BuiltIn(
        new("Blossom", 0x4A2A3A, 0xFBEEF2, 0xD6337A, 0xFBEEF2, 0xF2C4D5,
            [0x130D10, 0xD5506A, 0x418D51, 0xA8740A, 0x4A80C3, 0xC650B0, 0x2B8B90, 0xF2F1F1],
            [0x896176, 0xD96078, 0x469857, 0xB57D0B, 0x598AC8, 0xCB60B7, 0x2E959B, 0xF2F1F1],
            Split: 0xF2C4D5, Accent: 0xD6337A)
        {
            SelectionFg = 0x4A2A3A,
        });
}
