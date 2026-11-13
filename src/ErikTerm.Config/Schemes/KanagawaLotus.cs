namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Kanagawa Lotus (light), from kanagawa.nvim (https://github.com/rebelot/kanagawa.nvim).</summary>
    public static readonly ColorScheme KanagawaLotus = BuiltIn(
        new("Kanagawa Lotus", 0x545464, 0xF2ECBC, 0x545464, 0xF2ECBC, 0xC9CBD1,
            [0x1F1F28, 0xC84053, 0x6F894E, 0x77713F, 0x4D699B, 0xB35B79, 0x597B75, 0x545464],
            [0x8A8980, 0xD7474B, 0x6E915F, 0x836F4A, 0x6693BF, 0x624C83, 0x5E857A, 0x43436C],
            Split: 0xC9CBD1, Accent: 0x4D699B),
        new SchemeCredit("kanagawa.nvim", "Copyright (c) 2021 Tommaso Laurenzi", SchemeLicenses.Mit, "https://github.com/rebelot/kanagawa.nvim"));
}
