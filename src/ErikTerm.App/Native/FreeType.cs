using System.Runtime.InteropServices;

namespace ErikTerm.App.Native;

/// <summary>
/// Minimal FreeType bindings (FTL / GPLv2). FreeType's public structs are large, so instead of
/// mirroring them we read the few fields we need at fixed offsets. `long`-typed fields (FT_Long,
/// FT_Pos, FT_Fixed, FT_ULong) are 64-bit on LP64 (Linux/macOS) but 32-bit on LLP64 (Windows), so
/// there are two offset tables, and those fields are read as <see cref="CLong"/>, which has C `long`'s
/// width on every platform. Offsets were taken from offsetof() against FreeType 2.15 headers for
/// x86_64/aarch64 Linux and x86_64/aarch64 Windows; scripts/check-freetype-offsets.sh re-verifies them.
/// </summary>
#pragma warning disable S6640 // binding: FreeType's structs are read at native offsets through pointers
internal static unsafe partial class FreeType
#pragma warning restore S6640
{
    private const string Lib = NativeLibraries.FreeType;

    public const int LOAD_RENDER = 1 << 2;
    public const int LOAD_TARGET_LIGHT = 1 << 16; // FT_LOAD_TARGET_(FT_RENDER_MODE_LIGHT)
    public const int LOAD_COLOR = 1 << 20;

    public const long FACE_FLAG_SCALABLE = 1 << 0;
    public const long FACE_FLAG_FIXED_SIZES = 1 << 1;
    public const long FACE_FLAG_MULTIPLE_MASTERS = 1 << 8; // FT_HAS_MULTIPLE_MASTERS: variable (GX/OpenType) font
    public const long FACE_FLAG_COLOR = 1 << 14;

    public const byte PIXEL_MODE_MONO = 1;
    public const byte PIXEL_MODE_GRAY = 2;
    public const byte PIXEL_MODE_BGRA = 7; // premultiplied B, G, R, A (color emoji)

    /// <summary>Field offsets for one data model. The <c>long</c>-typed fields are noted.</summary>
    private readonly struct Layout
    {
        // FT_FaceRec
        public int FaceFlagsOffset { get; init; } // FT_Long
        public int FaceFamilyName { get; init; }
        public int FaceNumFixedSizes { get; init; } // FT_Int
        public int FaceAvailableSizes { get; init; } // FT_Bitmap_Size*
        public int FaceStyleName { get; init; }
        public int FaceGlyph { get; init; }
        public int FaceSize { get; init; }
        // FT_Bitmap_Size (one bitmap strike): FT_Short height, width; FT_Pos size, x_ppem, y_ppem
        public int BitmapSizeStride { get; init; }
        public int BitmapSizeYPpem { get; init; } // FT_Pos, 26.6
        // FT_SizeRec.metrics (FT_Size_Metrics) — FT_Pos fields, 26.6 fixed point
        public int SizeAscender { get; init; }
        public int SizeDescender { get; init; }
        public int SizeHeight { get; init; }
        // FT_GlyphSlotRec
        public int SlotAdvanceX { get; init; } // FT_Pos
        public int SlotBitmapRows { get; init; }
        public int SlotBitmapWidth { get; init; }
        public int SlotBitmapPitch { get; init; }
        public int SlotBitmapBuffer { get; init; }
        public int SlotBitmapPixelMode { get; init; }
        public int SlotBitmapLeft { get; init; }
        public int SlotBitmapTop { get; init; }
        // FT_MM_Var: FT_UInt num_axis, num_designs, num_namedstyles; FT_Var_Axis* axis; FT_Var_Named_Style* namedstyle
        public int MmNumAxis { get; init; }
        public int MmNumNamedStyles { get; init; }
        public int MmAxis { get; init; }
        public int MmNamedStyle { get; init; }
        // FT_Var_Axis: FT_String* name; FT_Fixed minimum, def, maximum; FT_ULong tag; FT_UInt strid
        public int VarAxisStride { get; init; }
        public int VarAxisTag { get; init; } // FT_ULong
        // FT_Var_Named_Style: FT_Fixed* coords; FT_UInt strid, psid
        public int NamedStyleStride { get; init; }
        public int NamedStyleCoords { get; init; }
        public int NamedStyleStrid { get; init; }
        // FT_SfntName: FT_UShort platform_id, encoding_id, language_id, name_id; FT_Byte* string; FT_UInt string_len
        public int SfntNameSize { get; init; }
    }

    /// <summary>LP64: Linux, macOS (x86_64 / arm64).</summary>
    private static readonly Layout Lp64 = new()
    {
        FaceFlagsOffset = 16,
        FaceFamilyName = 40,
        FaceStyleName = 48,
        FaceNumFixedSizes = 56,
        FaceAvailableSizes = 64,
        FaceGlyph = 152,
        FaceSize = 160,
        BitmapSizeStride = 32,
        BitmapSizeYPpem = 24,
        SizeAscender = 24 + 24,
        SizeDescender = 24 + 32,
        SizeHeight = 24 + 40,
        SlotAdvanceX = 128,
        SlotBitmapRows = 152,
        SlotBitmapWidth = 156,
        SlotBitmapPitch = 160,
        SlotBitmapBuffer = 168,
        SlotBitmapPixelMode = 178,
        SlotBitmapLeft = 192,
        SlotBitmapTop = 196,
        MmNumAxis = 0,
        MmNumNamedStyles = 8,
        MmAxis = 16,
        MmNamedStyle = 24,
        VarAxisStride = 48,
        VarAxisTag = 32,
        NamedStyleStride = 16,
        NamedStyleCoords = 0,
        NamedStyleStrid = 8,
        SfntNameSize = 24,
    };

    /// <summary>LLP64: 64-bit Windows (x64 / arm64), MSVC and MinGW builds alike.</summary>
    private static readonly Layout Llp64 = new()
    {
        FaceFlagsOffset = 8,
        FaceFamilyName = 24,
        FaceStyleName = 32,
        FaceNumFixedSizes = 40,
        FaceAvailableSizes = 48,
        FaceGlyph = 120,
        FaceSize = 128,
        BitmapSizeStride = 16,
        BitmapSizeYPpem = 12,
        SizeAscender = 24 + 12,
        SizeDescender = 24 + 16,
        SizeHeight = 24 + 20,
        SlotAdvanceX = 88,
        SlotBitmapRows = 104,
        SlotBitmapWidth = 108,
        SlotBitmapPitch = 112,
        SlotBitmapBuffer = 120,
        SlotBitmapPixelMode = 130,
        SlotBitmapLeft = 144,
        SlotBitmapTop = 148,
        MmNumAxis = 0,
        MmNumNamedStyles = 8,
        MmAxis = 16,
        MmNamedStyle = 24,
        VarAxisStride = 32,
        VarAxisTag = 20,
        NamedStyleStride = 16,
        NamedStyleCoords = 0,
        NamedStyleStrid = 8,
        SfntNameSize = 24,
    };

    // OperatingSystem.IsWindows() is a constant under Native AOT, so only one table survives.
    private static readonly Layout L = OperatingSystem.IsWindows() ? Llp64 : Lp64;

    [LibraryImport(Lib, EntryPoint = "FT_Init_FreeType")]
    public static partial int InitFreeType(nint* library);

    [LibraryImport(Lib, EntryPoint = "FT_Done_FreeType")]
    public static partial int DoneFreeType(nint library);

    // face_index is FT_Long and charcode FT_ULong: C `long`, hence CLong/CULong in the imports.
    [LibraryImport(Lib, EntryPoint = "FT_New_Face", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NewFace(nint library, string path, CLong faceIndex, nint* face);

    public static int NewFace(nint library, string path, nint faceIndex, nint* face) =>
        NewFace(library, path, new CLong(faceIndex), face);

    [LibraryImport(Lib, EntryPoint = "FT_Done_Face")]
    public static partial int DoneFace(nint face);

    [LibraryImport(Lib, EntryPoint = "FT_Set_Pixel_Sizes")]
    public static partial int SetPixelSizes(nint face, uint width, uint height);

    /// <summary>Selects a bitmap strike (index into available_sizes) on fonts without outlines.</summary>
    [LibraryImport(Lib, EntryPoint = "FT_Select_Size")]
    public static partial int SelectSize(nint face, int strikeIndex);

    [LibraryImport(Lib, EntryPoint = "FT_Get_Char_Index")]
    private static partial uint GetCharIndex(nint face, CULong charcode);

    public static uint GetCharIndex(nint face, nuint charcode) => GetCharIndex(face, new CULong(charcode));

    [LibraryImport(Lib, EntryPoint = "FT_Load_Glyph")]
    public static partial int LoadGlyph(nint face, uint glyphIndex, int loadFlags);

    // ---- Variable fonts (ftmm.h) and the name table (ftsnames.h) ----------------------------------

    [LibraryImport(Lib, EntryPoint = "FT_Get_MM_Var")]
    private static partial int GetMMVar(nint face, nint* master);

    [LibraryImport(Lib, EntryPoint = "FT_Done_MM_Var")]
    private static partial int DoneMMVar(nint library, nint master);

    /// <summary>Switches a variable face to named instance <paramref name="instanceIndex"/> (1-based; 0 = default).</summary>
    [LibraryImport(Lib, EntryPoint = "FT_Set_Named_Instance")]
    public static partial int SetNamedInstance(nint face, uint instanceIndex);

    [LibraryImport(Lib, EntryPoint = "FT_Get_Sfnt_Name_Count")]
    private static partial uint GetSfntNameCount(nint face);

    [LibraryImport(Lib, EntryPoint = "FT_Get_Sfnt_Name")]
    private static partial int GetSfntName(nint face, uint index, byte* name);

    private const uint TagWght = 0x77676874, TagItal = 0x6974616C, TagSlnt = 0x736C6E74;

    /// <summary>
    /// The named instances (named_style entries) of a variable face: 1-based index, name (from the name table via
    /// the instance's strid), and its wght / ital / slnt coordinates. Empty for static fonts.
    /// </summary>
    public static List<Rendering.VariableInstance> NamedInstances(nint library, nint face)
    {
        var result = new List<Rendering.VariableInstance>();
        if ((FaceFlags(face) & FACE_FLAG_MULTIPLE_MASTERS) == 0) return result;
        nint mm;
        if (GetMMVar(face, &mm) != 0 || mm == 0) return result;
        try
        {
            int numAxis = (int)Read<uint>(mm, L.MmNumAxis), numStyles = (int)Read<uint>(mm, L.MmNumNamedStyles);
            nint axes = Read<nint>(mm, L.MmAxis), styles = Read<nint>(mm, L.MmNamedStyle);
            int wght = -1, ital = -1, slnt = -1;
            for (int a = 0; a < numAxis; a++)
            {
                switch ((uint)Read<CULong>(axes + a * L.VarAxisStride, L.VarAxisTag).Value)
                {
                    case TagWght: wght = a; break;
                    case TagItal: ital = a; break;
                    case TagSlnt: slnt = a; break;
                }
            }
            var names = SfntNames(face);
            for (int i = 0; i < numStyles; i++)
            {
                nint style = styles + i * L.NamedStyleStride;
                nint coords = Read<nint>(style, L.NamedStyleCoords);
                double? Coord(int axis) => axis < 0 || coords == 0 ? null : Read<CLong>(coords, axis * sizeof(CLong)).Value / 65536.0;
                uint strid = Read<uint>(style, L.NamedStyleStrid);
                result.Add(new Rendering.VariableInstance(i + 1, names.GetValueOrDefault(strid), Coord(wght), Coord(ital), Coord(slnt)));
            }
        }
        finally
        {
            DoneMMVar(library, mm);
        }
        return result;
    }

    /// <summary>name_id → string from the name table, preferring Windows/Unicode English entries.</summary>
    private static Dictionary<uint, string> SfntNames(nint face)
    {
        var names = new Dictionary<uint, string>();
        var rank = new Dictionary<uint, int>();
        byte* rec = stackalloc byte[L.SfntNameSize + 8];
        uint count;
        try { count = GetSfntNameCount(face); }
        catch (EntryPointNotFoundException) { return names; } // FreeType built without FT_CONFIG_OPTION_SFNT_NAMES
        for (uint i = 0; i < count; i++)
        {
            if (GetSfntName(face, i, rec) != 0) continue;
            ushort platform = *(ushort*)rec, encoding = *(ushort*)(rec + 2), language = *(ushort*)(rec + 4), nameId = *(ushort*)(rec + 6);
            byte* str = *(byte**)(rec + 8);
            int len = (int)*(uint*)(rec + 16);
            if (str == null || len == 0) continue;
            // 3 = Windows (UTF-16BE; en-US best), 0 = Unicode (UTF-16BE), 1 = Macintosh Roman (ASCII-compatible).
            int r = NameRank(platform, encoding, language);
            if (r < 0 || (rank.TryGetValue(nameId, out int have) && have >= r)) continue;
            var bytes = new ReadOnlySpan<byte>(str, len);
            names[nameId] = platform == 1 ? System.Text.Encoding.Latin1.GetString(bytes) : System.Text.Encoding.BigEndianUnicode.GetString(bytes);
            rank[nameId] = r;
        }
        return names;
    }

    /// <summary>Preference of a name-table entry's platform and encoding; negative when it isn't usable.</summary>
    private static int NameRank(ushort platform, ushort encoding, ushort language) =>
        platform switch { 3 when language == 0x409 => 3, 3 when encoding is 1 or 10 => 2, 0 => 1, 1 when encoding == 0 => 0, _ => -1 };

    private static T Read<T>(nint p, int offset) where T : unmanaged => *(T*)(p + offset);

    /// <summary>Reads a C <c>long</c> (FT_Long / FT_Pos / FT_Fixed): 8 bytes on LP64, 4 on LLP64.</summary>
    private static long ReadLong(nint p, int offset) => Read<CLong>(p, offset).Value;

    /// <summary>FT_FaceRec.num_faces (the first field): how many faces the file holds (a .ttc has several).</summary>
    public static long NumFaces(nint face) => ReadLong(face, 0);

    [LibraryImport(Lib, EntryPoint = "FT_Get_First_Char")]
    private static partial CULong GetFirstChar(nint face, uint* glyphIndex);

    [LibraryImport(Lib, EntryPoint = "FT_Get_Next_Char")]
    private static partial CULong GetNextChar(nint face, CULong charCode, uint* glyphIndex);

    /// <summary>
    /// The code points the face's charmap (Unicode, when it has one) maps to a glyph, as inclusive (start, end) ranges in
    /// ascending order.
    /// </summary>
    public static List<(int Lo, int Hi)> CharRanges(nint face)
    {
        var ranges = new List<(int, int)>();
        uint glyph;
        ulong cp = GetFirstChar(face, &glyph).Value;
        int lo = -1, hi = -1;
        while (glyph != 0)
        {
            if (cp > 0x10FFFF) break;
            int c = (int)cp;
            if (lo >= 0 && c == hi + 1) hi = c;
            else
            {
                if (lo >= 0) ranges.Add((lo, hi));
                lo = hi = c;
            }
            cp = GetNextChar(face, new CULong((nuint)cp), &glyph).Value;
        }
        if (lo >= 0) ranges.Add((lo, hi));
        return ranges;
    }

    public static nint Glyph(nint face) => Read<nint>(face, L.FaceGlyph);
    public static long FaceFlags(nint face) => ReadLong(face, L.FaceFlagsOffset);
    public static int NumFixedSizes(nint face) => Read<int>(face, L.FaceNumFixedSizes);

    /// <summary>Pixel size (y_ppem) of bitmap strike <paramref name="index"/>.</summary>
    public static double StrikePixelSize(nint face, int index) =>
        ReadLong(Read<nint>(face, L.FaceAvailableSizes) + index * L.BitmapSizeStride, L.BitmapSizeYPpem) / 64.0;
    private static nint Size(nint face) => Read<nint>(face, L.FaceSize);

    public static double Ascender(nint face) => ReadLong(Size(face), L.SizeAscender) / 64.0;
    public static double Descender(nint face) => ReadLong(Size(face), L.SizeDescender) / 64.0;
    public static double Height(nint face) => ReadLong(Size(face), L.SizeHeight) / 64.0;

    /// <summary>
    /// Typographic family and subfamily (name IDs 16 / 17), null when the face doesn't carry them. A family
    /// shipped as one file per weight uses these to name the family it really belongs to, because
    /// <see cref="FamilyName"/> (name ID 1) has to stay a four-style group for old software: the file whose
    /// family_name is "JetBrainsMono NF Light" and style_name "Regular" is really "JetBrainsMono Nerd Font"
    /// Light, and the typographic family is the name fontconfig reports for it.
    /// </summary>
    public static (string? Family, string? Subfamily) TypographicNames(nint face)
    {
        var names = SfntNames(face);
        return (names.GetValueOrDefault(16u), names.GetValueOrDefault(17u));
    }

    /// <summary>FT_FaceRec.family_name, e.g. "Cascadia Code" (may be null for some bitmap fonts).</summary>
    public static string? FamilyName(nint face) => Marshal.PtrToStringUTF8(Read<nint>(face, L.FaceFamilyName));

    /// <summary>FT_FaceRec.style_name, e.g. "Bold Italic".</summary>
    public static string? StyleName(nint face) => Marshal.PtrToStringUTF8(Read<nint>(face, L.FaceStyleName));

    public readonly struct GlyphBitmap
    {
        public readonly int Width, Rows, Pitch, Left, Top;
        public readonly byte PixelMode;
        public readonly byte* Buffer;
        public readonly double AdvanceX;

        public GlyphBitmap(nint slot)
        {
            Width = (int)Read<uint>(slot, L.SlotBitmapWidth);
            Rows = (int)Read<uint>(slot, L.SlotBitmapRows);
            Pitch = Read<int>(slot, L.SlotBitmapPitch);
            Buffer = Read<nint>(slot, L.SlotBitmapBuffer) is var b ? (byte*)b : null;
            PixelMode = Read<byte>(slot, L.SlotBitmapPixelMode);
            Left = Read<int>(slot, L.SlotBitmapLeft);
            Top = Read<int>(slot, L.SlotBitmapTop);
            AdvanceX = ReadLong(slot, L.SlotAdvanceX) / 64.0;
        }
    }
}
