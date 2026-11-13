namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Cherry Blossom (dark), from Cherry Blossom (https://github.com/daitsuku/cherry-blossom-vscode-theme).</summary>
    public static readonly ColorScheme CherryBlossom = BuiltIn(
        new("Cherry Blossom", 0xF2F2F7, 0x1A1A1E, 0xF8B2CC, 0x1A1A1E, 0x6E5360,
            [0x1D1D22, 0xF8B2CC, 0x8ED9A9, 0xFFD87D, 0x7EB5E7, 0xD297D6, 0x7CE2E1, 0xE8E8EF],
            [0x5C5C66, 0xFAC2D8, 0xA5E6BD, 0xFFE49F, 0x9DC6ED, 0xE0B1E0, 0x9FEDED, 0xF2F2F7],
            Split: 0x6E5360, Accent: 0xF8B2CC),
        new SchemeCredit("Cherry Blossom", "Copyright (c) 2025 daitsuku", SchemeLicenses.Mit, "https://github.com/daitsuku/cherry-blossom-vscode-theme"));
}
