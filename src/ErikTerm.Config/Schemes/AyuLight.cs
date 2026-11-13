namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Ayu Light (light), from ayu for VS Code (https://github.com/ayu-theme/vscode-ayu).</summary>
    public static readonly ColorScheme AyuLight = BuiltIn(
        new("Ayu Light", 0x5B6066, 0xF8F9FA, 0xF29718, 0xF8F9FA, 0xD3E1F5,
            [0x000000, 0xF06B6C, 0x6CBF43, 0xE7A100, 0x21A1E2, 0xA176CB, 0x4ABC96, 0xC7C7C7],
            [0x686868, 0xF07171, 0x86B300, 0xEBA400, 0x22A4E6, 0xA37ACC, 0x4CBF99, 0xD1D1D1],
            Split: 0xD9DADC, Accent: 0xC57B14),
        new SchemeCredit("ayu for VS Code", "Copyright (c) 2016 Ike Kurghinyan", SchemeLicenses.Mit, "https://github.com/ayu-theme/vscode-ayu")
        {
            Notes = "The translucent text and selection colours are composited over the background.",
        });
}
