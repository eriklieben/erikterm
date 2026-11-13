namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>erik (dark), erikterm's default: Atom One Dark's colours (https://github.com/atom/one-dark-syntax) on a neutral near-black.</summary>
    public static readonly ColorScheme Erik = BuiltIn(
        new("erik", 0xD4D7DE, 0x16181D, 0xF5C26B, 0x16181D, 0x3A4150,
            [0x23262E, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xC8CCD4],
            [0x5C6370, 0xEF7B85, 0xA9D48A, 0xF0CF8F, 0x7CC0F5, 0xD391EA, 0x6CCAD6, 0xFFFFFF],
            Split: 0x3A4150, Accent: 0x61AFEF),
        new SchemeCredit("Atom One Dark (one-dark-syntax)", "Copyright (c) 2016 GitHub Inc.", SchemeLicenses.Mit, "https://github.com/atom/one-dark-syntax")
        {
            Notes = "The normal red, green, yellow, blue, magenta and cyan are One Dark's syntax colours.",
        });
}
