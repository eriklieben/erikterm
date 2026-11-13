namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>One Half Dark (dark), from One Half (https://github.com/sonph/onehalf).</summary>
    public static readonly ColorScheme OneHalfDark = BuiltIn(
        new("One Half Dark", 0xDCDFE4, 0x282C34, 0xA3B3CC, 0xDCDFE4, 0x474E5D,
            [0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4],
            [0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4],
            Split: 0x474E5D, Accent: 0x61AFEF)
        {
            SelectionFg = 0xDCDFE4,
        },
        new SchemeCredit("One Half", "Copyright (c) 2019 Son A. Pham", SchemeLicenses.Mit, "https://github.com/sonph/onehalf"));
}
