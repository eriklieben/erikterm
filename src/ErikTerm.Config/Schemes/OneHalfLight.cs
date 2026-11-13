namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>One Half Light (light), from One Half (https://github.com/sonph/onehalf).</summary>
    public static readonly ColorScheme OneHalfLight = BuiltIn(
        new("One Half Light", 0x383A42, 0xFAFAFA, 0xBFCEFF, 0x383A42, 0xBFCEFF,
            [0x383A42, 0xE45649, 0x50A14F, 0xC18401, 0x0184BC, 0xA626A4, 0x0997B3, 0xFAFAFA],
            [0x4F525E, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF],
            Split: 0xBFCEFF, Accent: 0x0184BC)
        {
            SelectionFg = 0x383A42,
        },
        new SchemeCredit("One Half", "Copyright (c) 2019 Son A. Pham", SchemeLicenses.Mit, "https://github.com/sonph/onehalf"));
}
