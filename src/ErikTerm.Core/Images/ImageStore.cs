namespace ErikTerm.Core.Images;

/// <summary>
/// Decoded pixels of one image a program sent (kitty graphics, iTerm2 inline image or sixel). Several placements
/// may show it. <see cref="Rgba"/> becomes null when the image is evicted from the memory budget.
/// </summary>
public sealed class TerminalImage
{
    private static long _serials;

    /// <summary>Unique for the process: renderers key their texture caches by it.</summary>
    public long Serial { get; } = Interlocked.Increment(ref _serials);

    /// <summary>Kitty image id (<c>i=</c>), 0 for anonymous / iTerm2 / sixel images.</summary>
    public uint Id { get; internal set; }

    /// <summary>Kitty image number (<c>I=</c>), 0 if none.</summary>
    public uint Number { get; internal set; }

    public int Width { get; }
    public int Height { get; }
    public byte[]? Rgba { get; private set; }

    /// <summary>Transmitted while the alternate screen was active (dropped when it is left).</summary>
    public bool OnAltScreen { get; internal set; }

    /// <summary>SHA-256 of the encoded file, for iTerm2 images (programs re-send the same picture on every redraw).</summary>
    internal byte[]? ContentHash;

    internal long LastUsed;

    public TerminalImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public TerminalImage(DecodedImage d) : this(d.Width, d.Height, d.Rgba) { }

    /// <summary>Kitty animation frames and playback (null for a still image).</summary>
    public ImageAnimation? Animation { get; private set; }

    internal ImageAnimation Animate() => Animation ??= new ImageAnimation(this);

    /// <summary>Pixels of the frame to show (the root frame for still images).</summary>
    public byte[]? DisplayRgba => Animation is { } a ? a.Frame(a.Current) ?? Rgba : Rgba;

    /// <summary>Changes when <see cref="DisplayRgba"/> shows different pixels (frame changes and frame edits).</summary>
    public long DisplayVersion => Animation is { } a ? (long)a.PixelsVersion << 16 | (uint)a.Current : 0;

    public long Bytes => (long)Width * Height * 4 + (Animation?.ExtraBytes ?? 0);
    public bool IsEvicted => Rgba is null;

    internal void Release()
    {
        Rgba = null;
        Animation?.Release();
    }
}

/// <summary>A kitty graphics placement: an image drawn over (or under, z &lt; 0) a rectangle of cells.</summary>
public sealed class KittyPlacement
{
    public required TerminalImage Image { get; init; }
    public uint PlacementId { get; init; }

    /// <summary>Absolute line (see <see cref="Terminal.FirstLine"/>) and column of the top-left cell.</summary>
    public long Line { get; internal set; }
    public int Column { get; internal set; }

    /// <summary>Cells covered (at the cell size in effect when it was placed).</summary>
    public int Columns { get; internal set; }
    public int Rows { get; internal set; }

    /// <summary><c>c=</c> / <c>r=</c> as given (0 = not given: derived from the image's pixel size).</summary>
    public int RequestedColumns { get; init; }
    public int RequestedRows { get; init; }

    /// <summary>Source rectangle in image pixels.</summary>
    public int SourceX { get; init; }
    public int SourceY { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }

    /// <summary>Pixel offset inside the first cell (<c>X=</c>, <c>Y=</c>).</summary>
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }

    public int Z { get; init; }

    /// <summary>Virtual placement (<c>U=1</c>): shown only where Unicode placeholders (U+10EEEE) are printed.</summary>
    public bool Virtual { get; init; }

    /// <summary>Pixel size the placement is drawn at, for a cell size.</summary>
    public (float W, float H) DisplaySize(int cellW, int cellH)
    {
        float sw = Math.Max(1, SourceWidth), sh = Math.Max(1, SourceHeight);
        if (RequestedColumns > 0 && RequestedRows > 0) return (RequestedColumns * cellW, RequestedRows * cellH);
        if (RequestedColumns > 0) { float w = RequestedColumns * cellW; return (w, w * sh / sw); }
        if (RequestedRows > 0) { float h = RequestedRows * cellH; return (h * sw / sh, h); }
        return (sw, sh);
    }

    internal bool Intersects(long line, int col) =>
        line >= Line && line < Line + Math.Max(1, Rows) && col >= Column && col < Column + Math.Max(1, Columns);

    // ---- Relative placements (P= / Q= / H= / V=) ----------------------------------------------------

    /// <summary>Parent image id (<c>P=</c>), 0 when the placement isn't relative.</summary>
    public uint ParentImageId { get; init; }
    /// <summary>Parent placement id (<c>Q=</c>).</summary>
    public uint ParentPlacementId { get; init; }
    /// <summary>Offset in cells from the parent's top-left cell (<c>H=</c>, <c>V=</c>).</summary>
    public int ParentDx { get; init; }
    public int ParentDy { get; init; }

    public bool IsRelative => ParentImageId != 0;

    /// <summary>
    /// The chain ends in a virtual placement: the position follows wherever its placeholders are drawn
    /// (renderers resolve it), so <see cref="Line"/>/<see cref="Column"/> are only offsets from that origin.
    /// </summary>
    public bool RelativeToVirtual { get; internal set; }

    internal bool IsChildOf(KittyPlacement parent) =>
        ParentImageId == parent.Image.Id && ParentPlacementId == parent.PlacementId;
}

/// <summary>
/// What a cell-attached image tile shows (iTerm2 inline images and sixel graphics are stored in the grid itself:
/// see <see cref="CellAttributes.Image"/>). The image occupies a box of <see cref="Columns"/>×<see cref="Rows"/> cells;
/// the picture fills the content rectangle inside it (in cell units, so it scales with the font).
/// </summary>
public readonly record struct InlineImage(TerminalImage Image, int Columns, int Rows, float ContentX, float ContentY, float ContentW, float ContentH);

/// <summary>
/// Images and placements of one terminal, with a memory budget (least recently used images are evicted first).
/// Cell tiles refer to <see cref="InlineImage"/> slots through a packed value in <see cref="Cell.Fg"/>:
/// slot (14 bits) · row in the image (9 bits) · column (9 bits).
/// </summary>
public sealed class ImageStore
{
    public const int SlotBits = 14, RowBits = 9, ColBits = 9;
    public const int MaxSlots = 1 << SlotBits;
    public const int MaxTileRows = 1 << RowBits, MaxTileColumns = 1 << ColBits;

    /// <summary>Default budget for decoded image data per terminal (kitty uses the same).</summary>
    public const long DefaultMemoryLimit = 320L * 1024 * 1024;

    /// <summary>
    /// Most images kept (tiny ones fit the memory budget by the million); the least recently used go first.
    /// Placements per screen and per image are capped too, the oldest making way: work per command then stays
    /// bounded however many a program sends.
    /// </summary>
    public const int MaxImages = 4096, MaxPlacements = 4096, MaxPlacementsPerImage = 1024;

    private readonly List<TerminalImage> _images = [];
    private readonly Dictionary<uint, TerminalImage> _byId = [];
    private readonly List<KittyPlacement> _main = [], _alt = [];
    private InlineImage?[] _slots = new InlineImage?[64];
    private readonly Dictionary<InlineImage, int> _slotOf = [];
    private int _nextSlot = 1;
    private long _tick;

    public long MemoryLimit { get; set; } = DefaultMemoryLimit;
    public long MemoryUsed { get; private set; }

    /// <summary>Incremented whenever images or placements change.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<TerminalImage> Images => _images;

    /// <summary>True once any cell tile was written (renderers scan the grid for tiles only then).</summary>
    public bool HasInlineImages => _slotOf.Count > 0;

    /// <summary>True when there is a virtual placement (renderers look for U+10EEEE placeholders only then).</summary>
    public bool HasVirtualPlacements { get; private set; }

    public bool HasPlacements => _main.Count > 0 || _alt.Count > 0;

    public List<KittyPlacement> Placements(bool alt) => alt ? _alt : _main;

    /// <summary>Milliseconds clock for animations (tests replace it).</summary>
    public Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    /// <summary>Animation frames were added to <paramref name="image"/>: count them against the budget.</summary>
    internal void FramesGrew(TerminalImage image, long bytes)
    {
        MemoryUsed += bytes;
        Touch(image);
        EnforceBudget(image);
        Version++;
    }

    internal void Changed() => Version++;

    /// <summary>A placement by image id and placement id on one screen (the newest if several).</summary>
    public KittyPlacement? FindPlacement(uint imageId, uint placementId, bool alt)
    {
        if (CurrentIndex(alt) is { } index) return index.ByKey.TryGetValue((imageId, placementId), out var p) ? p : null;
        var list = alt ? _alt : _main;
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].Image.Id == imageId && list[i].PlacementId == placementId) return list[i];
        return null;
    }

    /// <summary>
    /// Lookups by (image id, placement id) and of the newest virtual placement per image, for each screen, so renderers
    /// walking parent chains every frame don't scan the lists. After a change (<see cref="Version"/>) a few lookups
    /// scan instead (commands change the lists between every couple of lookups); the index is rebuilt once lookups
    /// outnumber that, keeping the cost per lookup amortized at most linear.
    /// </summary>
    private sealed class PlacementIndex
    {
        public readonly Dictionary<(uint, uint), KittyPlacement> ByKey = [];
        public readonly Dictionary<uint, KittyPlacement> NewestVirtual = [];

        public void Rebuild(List<KittyPlacement> list)
        {
            ByKey.Clear();
            NewestVirtual.Clear();
            foreach (var p in list) // in order: the newest wins
            {
                ByKey[(p.Image.Id, p.PlacementId)] = p;
                if (p.Virtual && !p.Image.IsEvicted) NewestVirtual[p.Image.Id] = p;
            }
        }
    }

    private readonly PlacementIndex _mainIndex = new(), _altIndex = new();
    private int _indexVersion = -1, _staleLookups;
    private const int ScansBeforeIndexing = 16;

    /// <summary>The index if it is current (or worth rebuilding now), else null: scan.</summary>
    private PlacementIndex? CurrentIndex(bool alt)
    {
        if (_indexVersion != Version)
        {
            if (++_staleLookups < ScansBeforeIndexing) return null;
            _mainIndex.Rebuild(_main);
            _altIndex.Rebuild(_alt);
            _indexVersion = Version;
            _staleLookups = 0;
        }
        return alt ? _altIndex : _mainIndex;
    }

    /// <summary>The placement a relative one hangs off (also a virtual one on the other screen), or null.</summary>
    public KittyPlacement? ParentOf(KittyPlacement p, bool alt)
    {
        if (!p.IsRelative) return null;
        return FindPlacement(p.ParentImageId, p.ParentPlacementId, alt)
            ?? (FindPlacement(p.ParentImageId, p.ParentPlacementId, !alt) is { Virtual: true } v ? v : null);
    }

    private static (uint, uint) Key(KittyPlacement p) => (p.Image.Id, p.PlacementId);
    private static (uint, uint) ParentKey(KittyPlacement p) => (p.ParentImageId, p.ParentPlacementId);

    /// <summary>Re-positions the relative placements below <paramref name="parent"/> (it was placed again elsewhere).</summary>
    private static void UpdateChildren(List<KittyPlacement> list, KittyPlacement parent)
    {
        if (!HasChild(list, parent)) return;
        var children = ChildrenByParent(list, parent);
        if (children is null) return;
        var queue = new Queue<(KittyPlacement Parent, int Depth)>();
        queue.Enqueue((parent, 0));
        var seen = new HashSet<KittyPlacement> { parent };
        while (queue.Count > 0)
        {
            var (p, depth) = queue.Dequeue();
            if (depth > 16 || !children.TryGetValue(Key(p), out var kids)) continue;
            foreach (var c in kids)
            {
                if (!seen.Add(c)) continue;
                FollowParent(c, p);
                queue.Enqueue((c, depth + 1));
            }
        }
    }

    private static void FollowParent(KittyPlacement child, KittyPlacement parent)
    {
        child.RelativeToVirtual = parent.Virtual || parent.RelativeToVirtual;
        child.Line = (parent.Virtual ? 0 : parent.Line) + child.ParentDy;
        child.Column = (parent.Virtual ? 0 : parent.Column) + child.ParentDx;
    }

    private static bool HasChild(List<KittyPlacement> list, KittyPlacement parent)
    {
        foreach (var c in list)
            if (c.IsRelative && c.ParentImageId == parent.Image.Id && c.ParentPlacementId == parent.PlacementId && c != parent) return true;
        return false;
    }

    /// <summary>Children by parent key, built once: the walk is linear in the number of placements.</summary>
    private static Dictionary<(uint, uint), List<KittyPlacement>>? ChildrenByParent(List<KittyPlacement> list, KittyPlacement parent)
    {
        Dictionary<(uint, uint), List<KittyPlacement>>? children = null;
        foreach (var c in list)
        {
            if (!c.IsRelative || c == parent) continue;
            children ??= [];
            if (!children.TryGetValue(ParentKey(c), out var l)) children[ParentKey(c)] = l = [];
            l.Add(c);
        }
        return children;
    }

    /// <summary>
    /// A relative placement lives as long as its parent: drops the ones whose parent is gone. Each pass is linear
    /// (parents looked up in a set); there is one pass per level of the deepest chain that lost its root.
    /// </summary>
    private int RemoveOrphans()
    {
        if (!_main.Exists(static p => p.IsRelative) && !_alt.Exists(static p => p.IsRelative)) return 0;
        int removed = 0;
        while (true)
        {
            var main = KeysOf(_main, virtualOnly: false);
            var alt = KeysOf(_alt, virtualOnly: false);
            var mainVirtual = KeysOf(_main, virtualOnly: true);
            var altVirtual = KeysOf(_alt, virtualOnly: true);
            // A parent is on the same screen, or a virtual placement on the other one (see ParentOf).
            int n = _main.RemoveAll(p => p.IsRelative && !main.Contains(ParentKey(p)) && !altVirtual.Contains(ParentKey(p)));
            n += _alt.RemoveAll(p => p.IsRelative && !alt.Contains(ParentKey(p)) && !mainVirtual.Contains(ParentKey(p)));
            if (n == 0) return removed;
            removed += n;
        }

        static HashSet<(uint, uint)> KeysOf(List<KittyPlacement> list, bool virtualOnly)
        {
            var set = new HashSet<(uint, uint)>();
            foreach (var p in list)
                if (!virtualOnly || p.Virtual) set.Add(Key(p));
            return set;
        }
    }

    public static uint PackTile(int slot, int row, int col) => (uint)slot << (RowBits + ColBits) | (uint)row << ColBits | (uint)col;

    public static (int Slot, int Row, int Col) UnpackTile(uint value) =>
        ((int)(value >> (RowBits + ColBits)), (int)(value >> ColBits) & (MaxTileRows - 1), (int)value & (MaxTileColumns - 1));

    public InlineImage? GetSlot(int slot) => (uint)slot < (uint)_slots.Length ? _slots[slot] : null;

    /// <summary>Registers a new image, evicting least recently used ones if over budget.</summary>
    public void Add(TerminalImage image)
    {
        image.LastUsed = ++_tick;
        _images.Add(image);
        MemoryUsed += image.Bytes;
        if (image.Id != 0)
        {
            if (_byId.TryGetValue(image.Id, out var old) && old != image) Remove(old);
            _byId[image.Id] = image;
        }
        while (_images.Count > MaxImages && LeastRecentlyUsed(image) is { } victim) Remove(victim);
        EnforceBudget(image);
        Version++;
    }

    private TerminalImage? LeastRecentlyUsed(TerminalImage keep)
    {
        TerminalImage? victim = null;
        foreach (var img in _images)
        {
            if (img == keep) continue;
            if (victim is null || img.LastUsed < victim.LastUsed) victim = img;
        }
        return victim;
    }

    public void Touch(TerminalImage image) => image.LastUsed = ++_tick;

    public TerminalImage? ById(uint id) => _byId.TryGetValue(id, out var img) && !img.IsEvicted ? img : null;

    /// <summary>The newest image transmitted with kitty image number <paramref name="number"/>.</summary>
    public TerminalImage? ByNumber(uint number)
    {
        for (int i = _images.Count - 1; i >= 0; i--)
            if (_images[i].Number == number && _images[i].Id != 0) return _images[i];
        return null;
    }

    /// <summary>An iTerm2 image already decoded from the same file contents.</summary>
    public TerminalImage? ByContent(byte[] hash)
    {
        for (int i = _images.Count - 1; i >= 0; i--)
            if (_images[i].ContentHash is { } h && h.AsSpan().SequenceEqual(hash)) return _images[i];
        return null;
    }

    /// <summary>Slot for a cell-tile image box (reused when the same image is shown the same way again).</summary>
    public int AllocateSlot(InlineImage inline)
    {
        Touch(inline.Image);
        if (_slotOf.TryGetValue(inline, out int existing)) return existing;
        int slot = -1;
        // Prefer an unused slot; grow the table; else recycle round-robin (stale cells then show the newer image,
        // which only happens after 16k other images).
        for (int i = 1; i < _slots.Length; i++)
        {
            int s = (_nextSlot + i - 1) % (_slots.Length - 1) + 1;
            if (_slots[s] is null || _slots[s]!.Value.Image.IsEvicted) { slot = s; break; }
        }
        if (slot < 0 && _slots.Length < MaxSlots)
        {
            slot = _slots.Length;
            Array.Resize(ref _slots, Math.Min(MaxSlots, _slots.Length * 2));
        }
        if (slot < 0) slot = _nextSlot;
        if (_slots[slot] is { } old) _slotOf.Remove(old);
        _slots[slot] = inline;
        _slotOf[inline] = slot;
        _nextSlot = slot % (MaxSlots - 1) + 1;
        Version++;
        return slot;
    }

    public void AddPlacement(KittyPlacement p, bool alt)
    {
        var list = alt ? _alt : _main;
        // Placement ids are unique per image and screen: at most one is replaced. Plain loops: this runs per command.
        int replaced = 0, ofImage = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var o = list[i];
            if (o.Image != p.Image) continue;
            if (p.PlacementId != 0 && o.PlacementId == p.PlacementId && replaced == 0) { list.RemoveAt(i); replaced++; }
            else ofImage++;
        }
        // Over a cap the oldest placements go (of this image, else of the screen), an eighth of the cap at once so the
        // clean-up (their relative children go with them) is rare; then each command costs a few linear passes at most.
        bool dropped = false;
        if (ofImage >= MaxPlacementsPerImage)
        {
            int drop = MaxPlacementsPerImage / 8;
            list.RemoveAll(o => o.Image == p.Image && drop-- > 0);
            dropped = true;
        }
        if (list.Count >= MaxPlacements) { list.RemoveRange(0, list.Count - MaxPlacements + MaxPlacements / 8); dropped = true; }
        list.Add(p);
        if (p.Virtual) HasVirtualPlacements = true;
        // Only a placement that replaced one can have children (a relative placement needs its parent to exist), and
        // they now hang off the new one, which has the same key: only dropped placements leave orphans.
        if (replaced > 0) UpdateChildren(list, p); // re-placed: its relative placements follow
        if (dropped)
        {
            RemoveOrphans();
            UpdateVirtualFlag();
        }
        Touch(p.Image);
        Version++;
    }

    /// <summary>The virtual placement placeholders for image <paramref name="id"/> refer to (newest, or by placement id).</summary>
    public KittyPlacement? FindVirtual(uint id, uint placementId, bool alt)
    {
        // Virtual placements are not tied to a screen: placeholders printed on the other one still resolve.
        return FindVirtualOn(id, placementId, alt) ?? FindVirtualOn(id, placementId, !alt);
    }

    private KittyPlacement? FindVirtualOn(uint id, uint placementId, bool onAlt)
    {
        if (CurrentIndex(onAlt) is { } index)
        {
            if (placementId == 0) return index.NewestVirtual.TryGetValue(id, out var v) ? v : null;
            return index.ByKey.TryGetValue((id, placementId), out var p) && p.Virtual && !p.Image.IsEvicted ? p : null;
        }
        return ScanForVirtual(onAlt ? _alt : _main, id, placementId);
    }

    private static KittyPlacement? ScanForVirtual(List<KittyPlacement> list, uint id, uint placementId)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var p = list[i];
            if (p.Virtual && p.Image.Id == id && !p.Image.IsEvicted && (placementId == 0 || p.PlacementId == placementId)) return p;
        }
        return null;
    }

    /// <summary>Removes placements matching <paramref name="match"/>; with <paramref name="freeImages"/>, also images left without placements.</summary>
    public int RemovePlacements(bool alt, Predicate<KittyPlacement> match, bool freeImages)
    {
        var list = alt ? _alt : _main;
        HashSet<TerminalImage>? touched = freeImages ? [] : null;
        int removed = list.RemoveAll(p =>
        {
            if (!match(p)) return false;
            touched?.Add(p.Image);
            return true;
        });
        if (touched is { Count: > 0 })
        {
            var placed = PlacedImages();
            foreach (var img in touched.Where(t => !placed.Contains(t)))
                Remove(img);
        }
        if (removed > 0) removed += RemoveOrphans();
        if (removed > 0) { UpdateVirtualFlag(); Version++; }
        return removed;
    }

    /// <summary>Deletes an image and all its placements (cell tiles referring to it stop drawing).</summary>
    public void Remove(TerminalImage image)
    {
        if (!_images.Remove(image)) return;
        MemoryUsed -= image.Bytes;
        if (image.Id != 0 && _byId.TryGetValue(image.Id, out var cur) && cur == image) _byId.Remove(image.Id);
        _main.RemoveAll(p => p.Image == image);
        _alt.RemoveAll(p => p.Image == image);
        for (int s = 1; s < _slots.Length; s++)
            if (_slots[s] is { } inline && inline.Image == image)
            {
                _slotOf.Remove(inline);
                _slots[s] = null;
            }
        image.Release();
        RemoveOrphans();
        UpdateVirtualFlag();
        Version++;
    }

    /// <summary>Drops placements on lines that left the scrollback.</summary>
    public void PruneBefore(long firstLine)
    {
        if (!AnyBefore(_main, firstLine) && !AnyBefore(_alt, firstLine)) return; // the usual case, without delegates
        int n = _main.RemoveAll(p => !p.Virtual && !p.RelativeToVirtual && p.Line + Math.Max(1, p.Rows) <= firstLine);
        n += _alt.RemoveAll(p => !p.Virtual && !p.RelativeToVirtual && p.Line + Math.Max(1, p.Rows) <= firstLine);
        if (n > 0) n += RemoveOrphans();
        if (n > 0) { UpdateVirtualFlag(); Version++; }
    }

    private static bool AnyBefore(List<KittyPlacement> list, long firstLine)
    {
        foreach (var p in list)
            if (!p.Virtual && !p.RelativeToVirtual && p.Line + Math.Max(1, p.Rows) <= firstLine) return true;
        return false;
    }

    /// <summary>Leaving the alternate screen: its placements and the images sent there go away.</summary>
    public void ClearAlt()
    {
        // Called on every alternate screen exit: allocation-free when there are no images.
        if (_alt.Count == 0 && _images.Count == 0) return;
        _alt.Clear();
        RemoveOrphans();
        var placed = PlacedImages();
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            if (i >= _images.Count) continue; // Remove may have shrunk the list
            var img = _images[i];
            if (img.OnAltScreen && !placed.Contains(img)) Remove(img);
        }
        UpdateVirtualFlag();
        Version++;
    }

    public void Clear()
    {
        foreach (var img in _images) img.Release();
        _images.Clear();
        _byId.Clear();
        _main.Clear();
        _alt.Clear();
        Array.Clear(_slots);
        _slotOf.Clear();
        MemoryUsed = 0;
        HasVirtualPlacements = false;
        Version++;
    }

    private HashSet<TerminalImage> PlacedImages()
    {
        var set = new HashSet<TerminalImage>();
        foreach (var p in _main) set.Add(p.Image);
        foreach (var p in _alt) set.Add(p.Image);
        return set;
    }

    private void UpdateVirtualFlag() => HasVirtualPlacements = _main.Exists(p => p.Virtual) || _alt.Exists(p => p.Virtual);

    private void EnforceBudget(TerminalImage keep)
    {
        while (MemoryUsed > MemoryLimit && _images.Count > 1)
        {
            // Least recently used first.
            if (LeastRecentlyUsed(keep) is not { } victim) break;
            Remove(victim);
        }
        if (MemoryUsed > MemoryLimit && _images.Count == 1 && _images[0] == keep && keep.Bytes > MemoryLimit) Remove(keep);
    }
}
