namespace ErikTerm.Core;

[Flags]
public enum CellAttributes : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strike = 1 << 7,
    /// <summary>First half of a double-width character.</summary>
    WideLead = 1 << 8,
    /// <summary>Second half of a double-width character (no glyph of its own).</summary>
    WideTrail = 1 << 9,
    /// <summary>
    /// The cell holds a grapheme cluster (base + combining marks / variation selectors / ZWJ sequence):
    /// <see cref="Cell.Rune"/> is the base, the full text is in the row's side table (<see cref="Row.TryGetCluster"/>).
    /// </summary>
    Cluster = 1 << 10,
    /// <summary>
    /// Bits 11–13: the underline style when <see cref="Underline"/> is set (see <see cref="UnderlineStyle"/>, read with
    /// <see cref="CellStyle.Underline"/>). 0 = single, so a plain <see cref="Underline"/> is a single underline.
    /// </summary>
    UnderlineStyleBit0 = 1 << 11,
    UnderlineStyleBit1 = 1 << 12,
    UnderlineStyleBit2 = 1 << 13,
    UnderlineStyleMask = UnderlineStyleBit0 | UnderlineStyleBit1 | UnderlineStyleBit2,
    /// <summary>SGR 53 (overline).</summary>
    Overline = 1 << 14,
    /// <summary>
    /// The cell shows a tile of an iTerm2 / sixel image instead of text: <see cref="Cell.Rune"/> is 0 and
    /// <see cref="Cell.Fg"/> holds the packed tile (see <see cref="Images.ImageStore.UnpackTile"/>). Writing or
    /// erasing the cell drops the flag, which erases that part of the image.
    /// </summary>
    Image = 1 << 15,

    SgrMask = Bold | Dim | Italic | Underline | UnderlineStyleMask | Blink | Inverse | Hidden | Strike | Overline,
}

/// <summary>SGR 4:n underline styles (kitty / VTE / wezterm). <see cref="None"/> is never stored: no underline flag.</summary>
public enum UnderlineStyle : byte
{
    None = 0,
    Single = 1,
    Double = 2,
    Curly = 3,
    Dotted = 4,
    Dashed = 5,
}

/// <summary>Packs and unpacks the underline style in <see cref="CellAttributes"/>.</summary>
public static class CellStyle
{
    private const int Shift = 11;

    /// <summary>The underline style of a cell's flags (<see cref="UnderlineStyle.None"/> without <see cref="CellAttributes.Underline"/>).</summary>
    public static UnderlineStyle Underline(CellAttributes flags) =>
        (flags & CellAttributes.Underline) == 0 ? UnderlineStyle.None
            : (UnderlineStyle)(((int)(flags & CellAttributes.UnderlineStyleMask) >> Shift) + 1);

    /// <summary>Flags with the underline replaced by <paramref name="style"/> (None removes it).</summary>
    public static CellAttributes WithUnderline(CellAttributes flags, UnderlineStyle style)
    {
        flags &= ~(CellAttributes.Underline | CellAttributes.UnderlineStyleMask);
        if (style is UnderlineStyle.None or > UnderlineStyle.Dashed) return flags;
        return flags | CellAttributes.Underline | (CellAttributes)(((int)style - 1) << Shift);
    }
}

/// <summary>One grid cell. 16 bytes, blittable; rows are flat arrays of these.</summary>
public struct Cell
{
    /// <summary>Unicode scalar; 0 = empty.</summary>
#pragma warning disable S1104 // written and read per cell on the print path, and StoreCell stores the fields as raw 64-bit words, so the layout is fixed
    public int Rune;
    public uint Fg;
    public uint Bg;
    public CellAttributes Flags;
#pragma warning restore S1104

    public static Cell Blank(uint bg) => new() { Bg = bg };
}

/// <summary>
/// Packed color: 0 = default, 0x01_0000ii = palette index, 0x02_rrggbb = true color.
/// </summary>
public static class TermColor
{
    public const uint Default = 0;
    private const uint IndexedTag = 0x0100_0000;
    private const uint RgbTag = 0x0200_0000;

    public static uint Indexed(int index) => IndexedTag | (uint)(index & 0xFF);

    public static uint Rgb(int r, int g, int b) =>
        RgbTag | (uint)(Math.Clamp(r, 0, 255) << 16 | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(b, 0, 255));

    public static bool IsDefault(uint c) => c == Default;
    public static bool IsIndexed(uint c) => (c & 0xFF00_0000) == IndexedTag;
    public static bool IsRgb(uint c) => (c & 0xFF00_0000) == RgbTag;
    public static int Index(uint c) => (int)(c & 0xFF);
}
