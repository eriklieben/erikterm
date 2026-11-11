using System.Runtime.CompilerServices;

namespace ErikTerm.Core;

/// <summary>
/// Column width (0, 1 or 2) and grapheme-relevant emoji properties of a code point, from tables generated
/// out of the Unicode Character Database by scripts/gen-unicode-tables.py (see UnicodeTables.g.cs).
/// Width 0 = combining mark / format character / variation selector: it joins the preceding cell.
/// </summary>
public static class CharWidth
{
    /// <summary>Unicode version the tables were generated from.</summary>
    public const string UnicodeVersion = UnicodeTables.UnicodeVersion;

    internal const byte WidthMask = 0b11;
    internal const byte Emoji = 1 << 2;
    internal const byte ExtendedPictographic = 1 << 3;
    internal const byte EmojiModifier = 1 << 4;
    internal const byte RegionalIndicator = 1 << 5;
    internal const byte EmojiModifierBase = 1 << 6;

    public const int ZeroWidthJoiner = 0x200D;
    public const int VariationSelector15 = 0xFE0E;
    public const int VariationSelector16 = 0xFE0F;

    /// <summary>The property byte for a code point (width in the low two bits, flags above).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Props(int cp)
    {
        if ((uint)cp >= 0x110000) return 1;
        int block = UnicodeTables.Index[cp >> UnicodeTables.Shift];
        return UnicodeTables.Blocks[(block << UnicodeTables.Shift) | (cp & UnicodeTables.Mask)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Of(int cp) => cp < 0x300 ? 1 : Props(cp) & WidthMask;

    public static bool IsEmoji(int cp) => (Props(cp) & Emoji) != 0;
    public static bool IsExtendedPictographic(int cp) => (Props(cp) & ExtendedPictographic) != 0;
    public static bool IsEmojiModifier(int cp) => (Props(cp) & EmojiModifier) != 0;
    public static bool IsRegionalIndicator(int cp) => (uint)(cp - 0x1F1E6) <= 0x1F1FF - 0x1F1E6;
}
