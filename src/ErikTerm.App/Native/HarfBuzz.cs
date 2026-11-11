using System.Runtime.InteropServices;

namespace ErikTerm.App.Native;

/// <summary>
/// Minimal HarfBuzz bindings (MIT), used for ligatures / contextual alternates on the grid. Fonts come
/// from FreeType faces via hb-ft. HarfBuzz is optional: <see cref="Available"/> is false when the
/// library can't be loaded, and the renderer then maps codepoints to glyphs directly.
/// </summary>
#pragma warning disable S6640 // binding: HarfBuzz returns glyph arrays as pointers
internal static unsafe partial class HarfBuzz
#pragma warning restore S6640
{
    private const string Lib = NativeLibraries.HarfBuzz;

    public const int DIRECTION_LTR = 4;

    /// <summary>hb_feature_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Feature
    {
        public uint Tag;
        public uint Value;
        public uint Start;
        public uint End;
    }

    /// <summary>hb_glyph_info_t (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphInfo
    {
        public uint Codepoint; // glyph index after shaping
        public uint Mask;
        public uint Cluster;
        private uint _var1, _var2;
    }

    /// <summary>hb_glyph_position_t (20 bytes), in the font's scale (26.6 pixels for hb-ft fonts).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphPosition
    {
        public int XAdvance, YAdvance, XOffset, YOffset;
        private uint _var;
    }

    private static int _available = -1;

    /// <summary>True when a HarfBuzz we can actually shape with could be loaded (checked once).</summary>
    public static bool Available
    {
        get
        {
            if (_available < 0)
            {
                try
                {
                    nint b = BufferCreate();
                    BufferDestroy(b);
                    _available = HasFreeTypeSupport() ? 1 : 0;
                    if (_available == 0)
                        Console.Error.WriteLine("erikterm: HarfBuzz was built without FreeType support (no hb_ft_*): " +
                            "ligatures and combining marks disabled. A HarfBuzz built against the same FreeType brings them back.");
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    Console.Error.WriteLine("erikterm: HarfBuzz not available, ligatures disabled (" + e.Message + ")");
                    _available = 0;
                }
            }
            return _available == 1;
        }
    }

    /// <summary>
    /// Whether this HarfBuzz has the hb-ft entry points. HarfBuzz builds its FreeType integration optionally and
    /// the official Windows binaries leave it out, which the rest of the library gives no hint of:
    /// <c>hb_buffer_create</c> and <c>hb_shape</c> are all there, and only <c>hb_ft_font_create_referenced</c> is
    /// missing. Our fonts are FreeType faces and there is no other way to hand one to HarfBuzz, so such a build is
    /// no use to us — and finding that out by calling it would throw <see cref="EntryPointNotFoundException"/> out
    /// of every frame, which drops the frame: a terminal that draws no text at all. Checked by looking the symbol
    /// up instead, so the answer is "HarfBuzz is not available" and the renderer falls back to mapping code points
    /// to glyphs directly.
    /// </summary>
    private static bool HasFreeTypeSupport()
    {
        // The handle goes through NativeLibraries' resolver and belongs to the runtime's cache: it is not freed.
        return NativeLibrary.TryLoad(Lib, typeof(HarfBuzz).Assembly, null, out nint handle)
            && NativeLibrary.TryGetExport(handle, "hb_ft_font_create_referenced", out _);
    }

    [LibraryImport(Lib, EntryPoint = "hb_ft_font_create_referenced")]
    public static partial nint FtFontCreateReferenced(nint ftFace);

    /// <summary>Must be called after the FreeType face's size changed.</summary>
    [LibraryImport(Lib, EntryPoint = "hb_ft_font_changed")]
    public static partial void FtFontChanged(nint font);

    [LibraryImport(Lib, EntryPoint = "hb_ft_font_set_load_flags")]
    public static partial void FtFontSetLoadFlags(nint font, int loadFlags);

    [LibraryImport(Lib, EntryPoint = "hb_font_destroy")]
    public static partial void FontDestroy(nint font);

    [LibraryImport(Lib, EntryPoint = "hb_buffer_create")]
    public static partial nint BufferCreate();

    [LibraryImport(Lib, EntryPoint = "hb_buffer_destroy")]
    public static partial void BufferDestroy(nint buffer);

    [LibraryImport(Lib, EntryPoint = "hb_buffer_clear_contents")]
    public static partial void BufferClearContents(nint buffer);

    [LibraryImport(Lib, EntryPoint = "hb_buffer_add_codepoints")]
    public static partial void BufferAddCodepoints(nint buffer, uint* text, int textLength, uint itemOffset, int itemLength);

    [LibraryImport(Lib, EntryPoint = "hb_buffer_set_direction")]
    public static partial void BufferSetDirection(nint buffer, int direction);

    [LibraryImport(Lib, EntryPoint = "hb_buffer_guess_segment_properties")]
    public static partial void BufferGuessSegmentProperties(nint buffer);

    [LibraryImport(Lib, EntryPoint = "hb_shape")]
    public static partial void Shape(nint font, nint buffer, Feature* features, uint numFeatures);

    [LibraryImport(Lib, EntryPoint = "hb_buffer_get_glyph_infos")]
    public static partial GlyphInfo* BufferGetGlyphInfos(nint buffer, uint* length);

    [LibraryImport(Lib, EntryPoint = "hb_buffer_get_glyph_positions")]
    public static partial GlyphPosition* BufferGetGlyphPositions(nint buffer, uint* length);
}
