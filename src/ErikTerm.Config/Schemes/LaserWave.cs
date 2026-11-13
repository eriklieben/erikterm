namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>LaserWave (dark), from LaserWave (https://github.com/Jaredk3nt/laserwave).</summary>
    public static readonly ColorScheme LaserWave = BuiltIn(
        new("LaserWave", 0xFFFFFF, 0x27212E, 0xFFFFFF, 0x27212E, 0x452B43,
            [0x000000, 0xEB64B9, 0x74DFC4, 0xFFE261, 0x40B4C4, 0xB381C5, 0xB4DCE7, 0xE5E5E5],
            [0x666666, 0xEB64B9, 0x74DFC4, 0xFFE261, 0x40B4C4, 0xB381C5, 0xB4DCE7, 0xE5E5E5],
            Split: 0x524D58, Accent: 0xEB64B9),
        new SchemeCredit("LaserWave", "Copyright (c) 2019 Jared Jones", SchemeLicenses.Mit, "https://github.com/Jaredk3nt/laserwave")
        {
            Notes = "Black, white and bright black, which the theme leaves out, are VS Code's terminal defaults.",
        });
}
