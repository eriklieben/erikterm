namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Nord (dark), from Nord for Alacritty (https://github.com/nordtheme/alacritty).</summary>
    public static readonly ColorScheme Nord = BuiltIn(
        new("Nord", 0xD8DEE9, 0x2E3440, 0xD8DEE9, 0x2E3440, 0x4C566A,
            [0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0],
            [0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4],
            Split: 0x4C566A, Accent: 0x88C0D0),
        new SchemeCredit("Nord for Alacritty", "Copyright (c) 2016-present Sven Greb", SchemeLicenses.Mit, "https://github.com/nordtheme/alacritty"));
}
