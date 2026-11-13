namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Shado (dark), from shadotheme (https://github.com/Shadorain/shadotheme).</summary>
    public static readonly ColorScheme Shado = BuiltIn(
        new("Shado", 0xDFB7E8, 0x111119, 0x6161B3, 0xDFB7E8, 0x262440,
            [0x140A1D, 0xB52A5B, 0xFF4971, 0x8897F4, 0xBD93F9, 0xE9729D, 0xF18FB0, 0xF1C4E0],
            [0xA8899C, 0xB52A5B, 0xFF4971, 0x8897F4, 0xBD93F9, 0xE9729D, 0xF18FB0, 0xF1C4E0],
            Split: 0x3A3242, Accent: 0xE9729D),
        new SchemeCredit("shadotheme", "Copyright (c) 2021, Shadorain. All rights reserved.", SchemeLicenses.Bsd3, "https://github.com/Shadorain/shadotheme"));
}
