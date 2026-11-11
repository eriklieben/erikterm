using ErikTerm.App.Native;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Primary font (4 styles) plus a fallback chain, rasterised with FreeType.
/// Fonts are located by <see cref="FontLocator"/> (fc-match on Linux, the registry on Windows, fontconfig or a
/// FreeType scan on macOS); a path may name a collection's face as <c>file.ttc#N</c> (<see cref="FontFiles"/>).
/// Bitmap-only color fonts (CBDT, e.g. Noto Color Emoji) use their nearest strike and are scaled by
/// the atlas, as do macOS's sbix ones (Apple Color Emoji), whose bitmap strikes are preferred to their outlines.
/// Every face gets a small integer id so shaped text can key glyphs by (face, glyph index),
/// and a HarfBuzz font on demand (<see cref="ShapingFont"/>).
/// </summary>
#pragma warning disable S6640 // FreeType and HarfBuzz return faces and glyph data through pointers
internal sealed unsafe partial class FontSet : IDisposable
#pragma warning restore S6640
{
    private sealed class Face(string path, int id, int instance = 0)
    {
        public readonly string Path = path;
        public readonly int Id = id;
        public readonly int Instance = instance; // variable font named instance (1-based), 0 = default
        public nint Handle;
        public bool Failed;
        public uint CurrentPx; // FreeType size state is per face; switch only when needed
        public bool Bitmap;    // no outlines: a fixed strike is selected and the atlas scales it
        public bool Color;     // FT_FACE_FLAG_COLOR: loaded with FT_LOAD_COLOR into BGRA bitmaps
        public nint Hb;        // hb_font_t, created on first use
        public uint HbPx;      // the FreeType size the HarfBuzz font was last synced to
    }

    public readonly record struct Metrics(float Ascender, float Descender, float Advance)
    {
        public float Height => Ascender - Descender;
    }

    private readonly Dictionary<uint, Metrics> _metrics = [];

    private readonly nint _library;
    private readonly Face?[] _styles = new Face?[4];
    private readonly List<Face> _fallbacks = [];
    private readonly List<Face> _faces = []; // every face, indexed by id
    private readonly HashSet<string> _knownPaths = [];
    private readonly Dictionary<int, Face?> _fallbackCache = [];
    private readonly string _requestedFamily;
    private readonly string[] _requestedFallback;
    private readonly List<string> _warnings = [];
    private readonly FallbackResolver _resolver;
    private int _seenResolverVersion;
    private bool _awaitingFallback, _pendingSeen;

    /// <summary>What went wrong finding the fonts (a family that isn't installed), for Show Config Problems.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Bumped when a fallback font arrived for a code point that was drawn without one (<see cref="PollFallbacks"/>):
    /// glyphs and shaped runs cached before may be .notdef boxes that can now be drawn.
    /// </summary>
    public int Generation { get; private set; }

    public float PixelSize { get; private set; }
    public int CellWidth { get; private set; }
    public int CellHeight { get; private set; }
    public int Baseline { get; private set; }
    public int UnderlinePosition => Math.Min(CellHeight - 1, Baseline + Math.Max(1, CellHeight / 12));
    public int LineThickness => Math.Max(1, (int)MathF.Round(PixelSize / 14f));

    public double LineHeight { get; private set; } = 1.0;

    /// <summary>The grid pixel size as FreeType sees it.</summary>
    public uint GridPx => (uint)Math.Clamp(MathF.Round(PixelSize), 4, 400);

    /// <param name="locator">Tests: a fake platform (default <see cref="FontLocator.Current"/>).</param>
    /// <param name="resolver">Tests: their own fallback lookups (default <see cref="FallbackResolver.Shared"/>).</param>
    public FontSet(string family, IReadOnlyList<string> fallbackFamilies, float pixelSize, double lineHeight = 1.0,
        IFontLocator? locator = null, FallbackResolver? resolver = null)
    {
        _resolver = resolver ?? FallbackResolver.Shared;
        _requestedFamily = family;
        _requestedFallback = [.. fallbackFamilies];
        nint lib;
        if (FreeType.InitFreeType(&lib) != 0) throw new InvalidOperationException("FT_Init_FreeType failed");
        _library = lib;

        var fonts = locator ?? FontLocator.Current;
        string? regular = fonts.Find(family, FontStyle.Regular);
        if (regular is null && family.Trim().Length > 0)
        {
            regular = fonts.DefaultMonospace() ?? throw new InvalidOperationException("no monospace font found");
            Warn($"font: no installed font matches \"{family}\"; using {System.IO.Path.GetFileName(regular)}" + (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "" : " (`fc-list : family` lists the installed ones)"));
        }
        regular ??= fonts.DefaultMonospace() ?? throw new InvalidOperationException("no monospace font found");
        _styles[0] = NewFaceEntry(regular, VariableInstanceFor(regular, FontStyle.Regular));
        _knownPaths.Add(regular);
        string?[] variants = [fonts.Find(family, FontStyle.Bold), fonts.Find(family, FontStyle.Italic), fonts.Find(family, FontStyle.BoldItalic)];
        for (int i = 0; i < 3; i++)
        {
            // A variable font has no separate Bold/Italic files: use its named instance (FontSet.Variable.cs).
            string path = variants[i] ?? regular;
            int instance = VariableInstanceFor(path, (FontStyle)(i + 1));
            if (path != regular || instance != 0)
            {
                _styles[i + 1] = NewFaceEntry(path, instance);
                _knownPaths.Add(path);
            }
        }
        _instances.Clear();

        // User-configured fallbacks, then symbols (powerline / nerd font icons), then the platform's order.
        foreach (var pattern in fallbackFamilies)
            AddFallback(fonts.Find(pattern, FontStyle.Regular) ?? Warn($"font_fallback: no font matches \"{pattern}\""));
        foreach (var pattern in fonts.SymbolFallbackFamilies)
            AddFallback(fonts.Find(pattern, FontStyle.Regular));
        foreach (var path in fonts.FallbackFiles(family).Take(24)) AddFallback(path);

        LineHeight = lineHeight;
        SetPixelSize(pixelSize);
    }

    private Face NewFaceEntry(string path, int instance = 0)
    {
        var face = new Face(path, _faces.Count, instance);
        _faces.Add(face);
        return face;
    }

    private string? Warn(string message)
    {
        Console.Error.WriteLine("erikterm: " + message);
        _warnings.Add(message);
        return null;
    }

    /// <summary>
    /// Called once per loop iteration (TermWindow.Tick): true when a fallback lookup this set was waiting for has been
    /// answered, so the window clears its glyph atlas and redraws (<see cref="Generation"/> changes too).
    /// </summary>
    public bool PollFallbacks()
    {
        if (!_awaitingFallback) return false;
        int version = _resolver.Version;
        if (version == _seenResolverVersion) return false;
        _seenResolverVersion = version;
        _awaitingFallback = false; // set again by any code point that is still pending when it's redrawn
        Generation++;
        return true;
    }

    /// <summary>A lookup is queued: don't cache "no glyph" for it, and redraw when it is answered.</summary>
    private void MarkPending()
    {
        _pendingSeen = true;
        _awaitingFallback = true;
    }

    public bool Matches(string family, IReadOnlyList<string> fallback) =>
        family == _requestedFamily && fallback.SequenceEqual(_requestedFallback);

    /// <summary>The primary (regular) font file.</summary>
    public string PrimaryPath => _styles[0]!.Path;

    /// <summary>Fallback faces in the chain (configured, symbol and platform fallbacks).</summary>
    public int FallbackCount => _fallbacks.Count;

    public string Description => $"{System.IO.Path.GetFileName(_styles[0]!.Path)} @ {PixelSize:F0}px, cell {CellWidth}x{CellHeight}";

    public void SetPixelSize(float pixelSize, double? lineHeight = null)
    {
        if (lineHeight is { } lh) LineHeight = lh;
        PixelSize = Math.Clamp(pixelSize, 6, 200);
        if (Open(_styles[0]!) == 0) throw new InvalidOperationException($"cannot open {_styles[0]!.Path}");

        var metrics = MetricsFor(PixelSize);
        double ascender = metrics.Ascender, advance = metrics.Advance;

        // line_height scales the cell, with the extra space split evenly above and below the text.
        double natural = metrics.Height;
        CellWidth = Math.Max(1, (int)Math.Round(advance));
        CellHeight = Math.Max(1, (int)Math.Ceiling(natural * LineHeight));
        Baseline = (int)Math.Ceiling(ascender + (CellHeight - natural) / 2);
    }

    /// <summary>Ascender/descender/advance of the primary font at any pixel size (UI text uses other sizes than the grid).</summary>
    public Metrics MetricsFor(float pixelSize)
    {
        uint px = (uint)Math.Clamp(MathF.Round(pixelSize), 4, 400);
        if (_metrics.TryGetValue(px, out var m)) return m;
        var primary = _styles[0]!;
        nint h = Open(primary);
        EnsureSize(primary, px);
        float advance = px * 0.6f;
        uint glyph = FreeType.GetCharIndex(h, 'M');
        if (glyph != 0 && FreeType.LoadGlyph(h, glyph, FreeType.LOAD_TARGET_LIGHT) == 0)
            advance = (float)new FreeType.GlyphBitmap(FreeType.Glyph(h)).AdvanceX;
        m = new Metrics((float)FreeType.Ascender(h), (float)FreeType.Descender(h), advance);
        _metrics[px] = m;
        return m;
    }

    /// <summary>Rasterises a codepoint at the grid size into FreeType's glyph slot; valid until the next call.</summary>
    public bool Rasterize(int codepoint, FontStyle style, out FreeType.GlyphBitmap bitmap) =>
        Rasterize(codepoint, style, PixelSize, out bitmap);

    /// <summary>
    /// Rasterises a codepoint into FreeType's glyph slot. Color faces yield BGRA bitmaps at their strike
    /// size (PIXEL_MODE_BGRA; the caller scales them).
    /// </summary>
    public bool Rasterize(int codepoint, FontStyle style, float pixelSize, out FreeType.GlyphBitmap bitmap)
    {
        bitmap = default;
        var (face, index) = Resolve(codepoint, style);
        if (face is null) return false;
        return Load(face, index, pixelSize, out bitmap);
    }

    /// <summary>Rasterises glyph <paramref name="glyph"/> of face <paramref name="faceId"/> (from <see cref="ResolveFace"/> or shaping).</summary>
    public bool RasterizeGlyph(int faceId, uint glyph, float pixelSize, out FreeType.GlyphBitmap bitmap)
    {
        bitmap = default;
        if ((uint)faceId >= (uint)_faces.Count) return false;
        var face = _faces[faceId];
        if (Open(face) == 0) return false;
        return Load(face, glyph, pixelSize, out bitmap);
    }

    private static bool Load(Face face, uint index, float pixelSize, out FreeType.GlyphBitmap bitmap)
    {
        bitmap = default;
        EnsureSize(face, (uint)Math.Clamp(MathF.Round(pixelSize), 4, 400));
        int flags = FreeType.LOAD_RENDER | FreeType.LOAD_TARGET_LIGHT | (face.Color ? FreeType.LOAD_COLOR : 0);
        if (FreeType.LoadGlyph(face.Handle, index, flags) != 0) return false;
        bitmap = new FreeType.GlyphBitmap(FreeType.Glyph(face.Handle));
        return bitmap.PixelMode is FreeType.PIXEL_MODE_GRAY or FreeType.PIXEL_MODE_MONO or FreeType.PIXEL_MODE_BGRA;
    }

    /// <summary>The face (by id) and glyph index used for a codepoint; glyph 0 is the primary font's .notdef.</summary>
    public int ResolveFace(int codepoint, FontStyle style, out uint glyph)
    {
        var (face, index) = Resolve(codepoint, style);
        glyph = index;
        return (face ?? _styles[0]!).Id;
    }

    // ---- Grapheme clusters --------------------------------------------------------------------

    private readonly Dictionary<string, int>[] _clusterFaces = [new(), new(), new(), new()];
    private readonly Dictionary<int, Face?> _colorFaceCache = [];

    /// <summary>
    /// The face to shape a multi-code-point cluster with (e.g. "é", "👍🏽", "🇳🇱", "👨‍👩‍👧", "❤️").
    /// Emoji sequences (VS16, skin tones, ZWJ, flags, emoji-presentation bases) go to a color font so its
    /// ligatures produce the single emoji glyph; other clusters use the first face that covers every code
    /// point (so a combining mark comes from the same font as its base), else the base's face. Cached per text.
    /// </summary>
    public int ResolveClusterFace(ReadOnlySpan<char> text, FontStyle style)
    {
        var cache = _clusterFaces[(int)style & 3];
        var lookup = cache.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out int id)) return id;
        _pendingSeen = false;
        id = ResolveClusterFaceUncached(text, style);
        if (_pendingSeen) return id; // a fallback is still being looked up: decide again once it's there
        if (cache.Count > 4096) cache.Clear();
        lookup[text] = id;
        return id;
    }

    private int ResolveClusterFaceUncached(ReadOnlySpan<char> text, FontStyle style)
    {
        if (text.IsEmpty) return _styles[0]!.Id;
        System.Text.Rune.DecodeFromUtf16(text, out var first, out _);
        int baseCp = first.Value;
        if (WantsColorPresentation(text, baseCp) && ColorFaceFor(baseCp) is { } color) return color.Id;

        var (baseFace, _) = Resolve(baseCp, style);
        baseFace ??= _styles[0]!;
        if (Covers(baseFace, text)) return baseFace.Id;
        foreach (var candidate in Candidates(style))
            if (candidate != baseFace && Open(candidate) != 0 && FreeType.GetCharIndex(candidate.Handle, (nuint)baseCp) != 0 && Covers(candidate, text))
                return candidate.Id;
        return baseFace.Id;
    }

    private IEnumerable<Face> Candidates(FontStyle style)
    {
        if (_styles[(int)style] is { } styled) yield return styled;
        yield return _styles[0]!;
        foreach (var f in _fallbacks) yield return f;
    }

    /// <summary>Every code point that needs a glyph (not ZWJ / variation selectors) is in the face.</summary>
    private bool Covers(Face face, ReadOnlySpan<char> text)
    {
        nint h = Open(face);
        if (h == 0) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp is 0x200D or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF)) continue;
            if (FreeType.GetCharIndex(h, (nuint)cp) == 0) return false;
        }
        return true;
    }

    /// <summary>VS16, a skin tone, ZWJ between pictographs, a flag, or an emoji-presentation base without VS15.</summary>
    internal static bool WantsColorPresentation(ReadOnlySpan<char> text, int baseCp)
    {
        bool textStyle = false, emojiStyle = IsEmojiPresentation(baseCp) || baseCp is >= 0x1F1E6 and <= 0x1F1FF;
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 0xFE0E: textStyle = true; break;
                case 0xFE0F or 0x200D or (>= 0x1F3FB and <= 0x1F3FF): emojiStyle = true; break; // a keycap needs VS16 (else it stays text, one cell)
            }
        }
        return emojiStyle && !textStyle;
    }

    private Face? ColorFaceFor(int cp)
    {
        if (_colorFaceCache.TryGetValue(cp, out var face)) return face;
        face = null;
        foreach (var f in _fallbacks)
        {
            nint h = Open(f);
            if (h != 0 && IsEmojiFace(f) && FreeType.GetCharIndex(h, (nuint)cp) != 0) { face = f; break; }
        }
        // Keycap bases (0-9 # *) and other emoji below U+2000 (©, ®) are in every text font, so asking the system for
        // the code point finds a text font: ask for the font that has an emoji (😀) instead.
        if (face is null)
        {
            if (!_resolver.TryGet(cp >= 0x2000 ? cp : 0x1F600, FallbackKind.Color, out string? path))
            {
                MarkPending(); // asked off-thread (FallbackResolver); drawn as text until it arrives
                return null;
            }
            if (path is not null && (AddFallback(path) ?? FaceForPath(path)) is { } added
                && Open(added) != 0 && IsEmojiFace(added) && FreeType.GetCharIndex(added.Handle, (nuint)cp) != 0)
                face = added;
        }
        _colorFaceCache[cp] = face;
        return face;
    }

    /// <summary>
    /// A color font that is an emoji font (has 😀): some text fonts carry a COLR table too, and would otherwise
    /// win for keycap bases like "1" that every font has.
    /// </summary>
    private bool IsEmojiFace(Face face) => face.Color && Open(face) is var h && h != 0 && FreeType.GetCharIndex(h, 0x1F600) != 0;

    /// <summary>Glyph index of a code point in a face (0 if missing).</summary>
    public uint GlyphIndex(int faceId, int codepoint)
    {
        if ((uint)faceId >= (uint)_faces.Count) return 0;
        nint h = Open(_faces[faceId]);
        return h == 0 ? 0 : FreeType.GetCharIndex(h, (nuint)codepoint);
    }

    /// <summary>True for color (emoji) faces: their glyphs are BGRA bitmaps, drawn untinted.</summary>
    public bool IsColorFace(int faceId) => (uint)faceId < (uint)_faces.Count && _faces[faceId].Color;

    /// <summary>
    /// HarfBuzz font for a face at the grid size (0 if HarfBuzz isn't available). The FreeType face is
    /// shared with rasterisation, so this re-selects the grid size and resyncs HarfBuzz when it changed.
    /// </summary>
    public nint ShapingFont(int faceId)
    {
        if ((uint)faceId >= (uint)_faces.Count || !HarfBuzz.Available) return 0;
        var face = _faces[faceId];
        nint h = Open(face);
        if (h == 0) return 0;
        uint px = GridPx;
        EnsureSize(face, px);
        if (face.Hb == 0)
        {
            face.Hb = HarfBuzz.FtFontCreateReferenced(h);
            if (face.Hb == 0) return 0;
            HarfBuzz.FtFontSetLoadFlags(face.Hb, FreeType.LOAD_TARGET_LIGHT | (face.Color ? FreeType.LOAD_COLOR : 0));
            face.HbPx = px;
        }
        else if (face.HbPx != px)
        {
            HarfBuzz.FtFontChanged(face.Hb);
            face.HbPx = px;
        }
        return face.Hb;
    }

    private static void EnsureSize(Face face, uint px)
    {
        if (face.Bitmap) return; // one fixed strike; scaled when copied into the atlas
        if (face.CurrentPx == px || face.Handle == 0) return;
        FreeType.SetPixelSizes(face.Handle, 0, px);
        face.CurrentPx = px;
    }

    private (Face? face, uint index) Resolve(int cp, FontStyle style)
    {
        var styled = _styles[(int)style] ?? _styles[0]!;
        nint h = Open(styled);
        uint idx = h != 0 ? FreeType.GetCharIndex(h, (nuint)cp) : 0;
        if (idx != 0) return (styled, idx);

        var regularFace = _styles[0]!;
        nint regular = Open(regularFace);
        idx = FreeType.GetCharIndex(regular, (nuint)cp);
        if (idx != 0) return (regularFace, idx);

        if (!_fallbackCache.TryGetValue(cp, out var fb))
        {
            fb = FindFallback(cp, out bool pending);
            if (pending) MarkPending(); // not cached: the answer is on its way (FallbackResolver)
            else _fallbackCache[cp] = fb;
        }
        if (fb is not null)
        {
            nint fh = Open(fb);
            return (fb, FreeType.GetCharIndex(fh, (nuint)cp));
        }
        return (regularFace, 0); // .notdef box
    }

    private Face? FindFallback(int cp, out bool pending)
    {
        pending = false;
        // Emoji-presentation codepoints (🦀 ✨ ⭐ …) prefer a color font, text-presentation ones (★ ✔ © ↔ …)
        // a monochrome one, whatever order the chain has them in; otherwise the first match wins.
        bool emoji = IsEmojiPresentation(cp);
        Face? other = null;
        foreach (var face in _fallbacks)
        {
            nint h = Open(face);
            if (h == 0 || FreeType.GetCharIndex(h, (nuint)cp) == 0) continue;
            if (face.Color == emoji) return face;
            other ??= face;
        }
        if (other is not null) return other;
        // Last resort: any installed font covering this codepoint — found off the UI thread (fc-match is a process per
        // code point); until the answer arrives the cell shows .notdef.
        if (!_resolver.TryGet(cp, FallbackKind.Text, out string? path))
        {
            pending = true;
            return null;
        }
        if (path is not null && (AddFallback(path) ?? FaceForPath(path)) is { } added)
        {
            nint h = Open(added);
            if (h != 0 && FreeType.GetCharIndex(h, (nuint)cp) != 0) return added;
        }
        return null;
    }

    /// <summary>The fallback face already loaded from <paramref name="path"/> (another code point found it first).</summary>
    private Face? FaceForPath(string path)
    {
        foreach (var f in _fallbacks)
            if (f.Path == path) return f;
        return null;
    }

    /// <summary>Rough Emoji_Presentation=Yes test: the pictographic blocks plus the BMP symbols that default to emoji.</summary>
    internal static bool IsEmojiPresentation(int cp) => cp >= 0x10000 ? IsSupplementaryEmoji(cp) : IsBmpEmoji(cp);

    private static bool IsSupplementaryEmoji(int cp) => cp switch
    {
        (>= 0x1F300 and <= 0x1F64F) or (>= 0x1F680 and <= 0x1F6FF) or (>= 0x1F900 and <= 0x1F9FF) or (>= 0x1FA70 and <= 0x1FAFF) => true,
        0x1F004 or 0x1F0CF or 0x1F18E or (>= 0x1F191 and <= 0x1F19A) or (>= 0x1F7E0 and <= 0x1F7EB) => true,
        _ => false,
    };

    private static bool IsBmpEmoji(int cp) => cp switch
    {
        0x231A or 0x231B or (>= 0x23E9 and <= 0x23EC) or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE or 0x2614 or 0x2615 => true,
        (>= 0x2648 and <= 0x2653) or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE or 0x26C4 or 0x26C5 => true,
        0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or 0x2728 => true,
        0x274C or 0x274E or (>= 0x2753 and <= 0x2755) or 0x2757 or (>= 0x2795 and <= 0x2797) or 0x27B0 or 0x27BF => true,
        0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,
        _ => false,
    };

    private Face? AddFallback(string? path)
    {
        if (path is null || !_knownPaths.Add(path)) return null;
        var face = NewFaceEntry(path);
        _fallbacks.Add(face);
        return face;
    }

    private nint Open(Face face)
    {
        if (face.Handle != 0 || face.Failed) return face.Handle;
        nint h;
        var (file, faceIndex) = FontFiles.Split(face.Path);
        if (FreeType.NewFace(_library, file, faceIndex, &h) != 0)
        {
            face.Failed = true;
            return 0;
        }
        if (face.Instance != 0) FreeType.SetNamedInstance(h, (uint)face.Instance);
        long flags = FreeType.FaceFlags(h);
        face.Color = (flags & FreeType.FACE_FLAG_COLOR) != 0;
        uint px = (uint)MathF.Round(PixelSize);
        bool ok = SelectSize(h, flags, px, face);
        if (!ok)
        {
            FreeType.DoneFace(h);
            face.Failed = true;
            return 0;
        }
        face.Handle = h;
        face.CurrentPx = px;
        return h;
    }

    private static bool SelectSize(nint h, long flags, uint px, Face face)
    {
        if ((flags & FreeType.FACE_FLAG_SCALABLE) != 0 && !PreferStrikes(flags)) return FreeType.SetPixelSizes(h, 0, px) == 0;
        if ((flags & FreeType.FACE_FLAG_FIXED_SIZES) == 0 || FreeType.NumFixedSizes(h) <= 0) return false;
        face.Bitmap = true;
        return FreeType.SelectSize(h, BestStrike(h, px)) == 0;
    }

    /// <summary>
    /// Bitmap strikes (CBDT color emoji has one, ~109 ppem): the smallest that is at least twice the grid size (emoji are
    /// drawn up to two cells wide), else the largest; the atlas scales.
    /// </summary>
    private static int BestStrike(nint h, uint px)
    {
        int count = FreeType.NumFixedSizes(h), best = -1, largest = 0;
        double bestPx = double.MaxValue, largestPx = 0;
        for (int i = 0; i < count; i++)
        {
            double s = FreeType.StrikePixelSize(h, i);
            if (s > largestPx) { largestPx = s; largest = i; }
            if (s >= 2 * px && s < bestPx) { bestPx = s; best = i; }
        }
        return best >= 0 ? best : largest;
    }

    /// <summary>
    /// macOS: a color font with bitmap strikes (sbix: Apple Color Emoji) is drawn from its strikes even if FreeType calls
    /// it scalable — its outlines are empty, and FT_Set_Pixel_Sizes only picks a strike at an exact size match.
    /// </summary>
    private static bool PreferStrikes(long flags) =>
        OperatingSystem.IsMacOS() && (flags & FreeType.FACE_FLAG_COLOR) != 0 && (flags & FreeType.FACE_FLAG_FIXED_SIZES) != 0;

    public void Dispose()
    {
        // HarfBuzz fonts hold a reference on their FreeType face: release them first.
        foreach (nint font in _faces.Select(f => f.Hb).Where(h => h != 0)) HarfBuzz.FontDestroy(font);
        foreach (nint face in _faces.Select(f => f.Handle).Where(h => h != 0)) FreeType.DoneFace(face);
        FreeType.DoneFreeType(_library);
    }
}
