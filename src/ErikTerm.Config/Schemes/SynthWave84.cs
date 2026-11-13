namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>SynthWave '84 (dark), from SynthWave '84 (https://github.com/robb0wen/synthwave-vscode).</summary>
    public static readonly ColorScheme SynthWave84 = BuiltIn(
        new("SynthWave '84", 0xFFFFFF, 0x262335, 0xFFFFFF, 0x262335, 0x413F4E,
            [0x000000, 0xFE4450, 0x72F1B8, 0xF3E70F, 0x03EDF9, 0xFF7EDB, 0x03EDF9, 0xE5E5E5],
            [0x666666, 0xFE4450, 0x72F1B8, 0xFEDE5D, 0x03EDF9, 0xFF7EDB, 0x03EDF9, 0xE5E5E5],
            Split: 0x413F4E, Accent: 0xFF7EDB),
        new SchemeCredit("SynthWave '84", "Copyright (c) 2019 Robb Owen", SchemeLicenses.Mit, "https://github.com/robb0wen/synthwave-vscode")
        {
            Notes = "Black, white and bright black, which the theme leaves out, are VS Code's terminal defaults.",
        });
}
