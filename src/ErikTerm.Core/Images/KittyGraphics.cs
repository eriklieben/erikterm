using System.IO.Compression;
using System.Text;

namespace ErikTerm.Core.Images;

/// <summary>The keys of one kitty graphics command (<c>ESC _ G keys ; payload ESC \</c>).</summary>
internal struct KittyCommand
{
    public char Action, Medium, Compression, Delete;
    public int Format, Quiet, More;
    public uint Id, Number, Placement;
    public int Width, Height, Size, Offset;
    public int X, Y, W, H, CellX, CellY, Columns, Rows, Cursor, Virtual, Z;
    public uint ParentId, ParentPlacement, Background; // P, Q; Y as a 32-bit RGBA color (a=f)
    public int ParentDx, ParentDy;                     // H, V

    public static KittyCommand Default => new() { Action = 't', Medium = 'd', Format = 32, Delete = 'a' };
}

/// <summary>
/// Kitty graphics protocol (https://sw.kovidgoyal.net/kitty/graphics-protocol/): APC <c>G keys ; payload</c> ST.
/// Supported: transmit (a=t / a=T; t=d direct, t=f file, t=t temporary file, t=s shared memory; f=24 / 32 raw,
/// f=100 PNG; o=z zlib; chunking with m=1), place (a=p: c/r, x/y/w/h, X/Y, z, p, C, U=1 virtual placements for
/// Unicode placeholders), delete (a=d with a/i/n/c/p/q/r/x/y/z selectors, upper case also frees the data), query
/// (a=q) and the quiet modes q=1 / q=2, relative placements (P/Q/H/V) and animation (a=f frames, a=a control,
/// a=c compose; <see cref="KittyAnimator"/>). Placing an image moves the terminal's cursor through <see cref="IImageGrid"/>.
/// </summary>
internal sealed class KittyGraphics(ImageStore images, IImageGrid grid, TerminalReplies replies)
{
    private readonly ImageStore _images = images;
    private readonly KittyAnimator _animator = new(images);

    private const int MaxKittyPayload = ImageLimits.MaxEncodedBytes / 3 * 4 + 16; // base64 of the largest file

    private KittyCommand _kittyChunked;
    private bool _kittyChunking;
    private MemoryStream? _kittyData;

    /// <summary>Upper id range for images sent with only I= (the terminal picks the id).</summary>
    private uint _kittyNextId = 0x7F00_0000;

    /// <summary>A transmission in chunks (m=1) stops: RIS, or a new program.</summary>
    public void CancelTransmission()
    {
        _kittyChunking = false;
        _kittyData = null;
    }

    /// <summary>An APC string that starts with G; <paramref name="data"/> is what follows the G.</summary>
    public void Dispatch(ReadOnlySpan<byte> data)
    {
        int semi = data.IndexOf((byte)';');
        var control = semi < 0 ? data : data[..semi];
        var payload = semi < 0 ? default : data[(semi + 1)..];

        if (_kittyChunking)
        {
            // A chunk of a transmission in progress: only m (and q) matter.
            var chunk = ParseKitty(control, KittyCommand.Default);
            if (chunk.Quiet != 0) _kittyChunked.Quiet = chunk.Quiet;
            AppendKittyData(payload);
            if (chunk.More == 1) return;
            _kittyChunking = false;
            var cmd = _kittyChunked;
            var buffer = _kittyData!;
            _kittyData = null;
            if (buffer.Length > MaxKittyPayload) { KittyReply(cmd, "EFBIG:image data too large"); return; }
            KittyExecute(cmd, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            return;
        }

        var c = ParseKitty(control, KittyCommand.Default);
        if (c.More == 1 && c.Action is 't' or 'T' or 'q' or 'f')
        {
            _kittyChunked = c;
            _kittyChunking = true;
            _kittyData = new MemoryStream();
            AppendKittyData(payload);
            return;
        }
        KittyExecute(c, payload);
    }

    private void AppendKittyData(ReadOnlySpan<byte> payload)
    {
        if (_kittyData is null || _kittyData.Length > MaxKittyPayload) return; // over the limit: drop, report at the end
        _kittyData.Write(payload);
    }

    private static KittyCommand ParseKitty(ReadOnlySpan<byte> control, KittyCommand c)
    {
        foreach (var range in control.Split((byte)','))
        {
            var kv = control[range];
            if (kv.Length < 2 || kv[1] != '=') continue;
            char key = (char)kv[0];
            var v = kv[2..];
            char ch = v.IsEmpty ? '\0' : (char)v[0];
            long num = ParseKittyNumber(v, out bool neg);
            int i32 = (int)Math.Clamp(neg ? -num : num, int.MinValue, int.MaxValue);
            if (!SetKittyTransmitKey(ref c, key, ch, num, i32)) SetKittyPlacementKey(ref c, key, num, i32);
        }
        return c;
    }

    /// <summary>The value's magnitude (saturated at uint.MaxValue), or 0 if it isn't a number.</summary>
    private static long ParseKittyNumber(ReadOnlySpan<byte> v, out bool negative)
    {
        negative = !v.IsEmpty && v[0] == '-';
        long num = 0;
        foreach (byte b in negative ? v[1..] : v)
        {
            if (b is < (byte)'0' or > (byte)'9') return 0;
            num = Math.Min(num * 10 + (b - '0'), uint.MaxValue);
        }
        return num;
    }

    /// <summary>The keys of the action, the transmission and the image's identity; false for any other key.</summary>
    private static bool SetKittyTransmitKey(ref KittyCommand c, char key, char ch, long num, int i32)
    {
        switch (key)
        {
            case 'a': c.Action = ch; break;
            case 't': c.Medium = ch; break;
            case 'o': c.Compression = ch; break;
            case 'd': c.Delete = ch; break;
            case 'f': c.Format = i32; break;
            case 'q': c.Quiet = i32; break;
            case 'm': c.More = i32; break;
            case 'i': c.Id = (uint)num; break;
            case 'I': c.Number = (uint)num; break;
            case 'p': c.Placement = (uint)num; break;
            case 's': c.Width = i32; break;
            case 'v': c.Height = i32; break;
            case 'S': c.Size = i32; break;
            case 'O': c.Offset = i32; break;
            default: return false;
        }
        return true;
    }

    private static void SetKittyPlacementKey(ref KittyCommand c, char key, long num, int i32)
    {
        switch (key)
        {
            case 'x': c.X = i32; break;
            case 'y': c.Y = i32; break;
            case 'w': c.W = i32; break;
            case 'h': c.H = i32; break;
            case 'X': c.CellX = i32; break;
            case 'Y': c.CellY = i32; c.Background = (uint)num; break;
            case 'P': c.ParentId = (uint)num; break;
            case 'Q': c.ParentPlacement = (uint)num; break;
            case 'H': c.ParentDx = i32; break;
            case 'V': c.ParentDy = i32; break;
            case 'c': c.Columns = i32; break;
            case 'r': c.Rows = i32; break;
            case 'C': c.Cursor = i32; break;
            case 'U': c.Virtual = i32; break;
            case 'z': c.Z = i32; break;
        }
    }

    private void KittyExecute(KittyCommand c, ReadOnlySpan<byte> payload)
    {
        if (c.Id != 0 && c.Number != 0) { KittyReply(c, "EINVAL:i and I are mutually exclusive"); return; }
        switch (c.Action)
        {
            case 't':
            case 'T':
            case 'q':
                KittyTransmit(c, payload);
                break;
            case 'p':
            {
                var image = KittyImageFor(c);
                if (image is null) { KittyReply(c, "ENOENT:no such image"); return; }
                c.Id = image.Id;
                string? err = KittyPlace(c, image);
                KittyReply(c, err ?? "OK");
                break;
            }
            case 'd':
                KittyDelete(c);
                break;
            case 'f':
            case 'a':
            case 'c':
                KittyAnimate(c, payload);
                break;
            default:
                KittyReply(c, "EINVAL:unsupported action");
                break;
        }
    }

    private void KittyTransmit(KittyCommand c, ReadOnlySpan<byte> payload)
    {
        DecodedImage decoded;
        try
        {
            decoded = KittyDecode(c, payload);
        }
        catch (Exception e) { KittyReply(c, KittyError(e)); return; }

        if (c.Action == 'q') { KittyReply(c, "OK"); return; } // a query only checks that the data would load

        var image = new TerminalImage(decoded) { OnAltScreen = grid.IsAlternateScreen, Number = c.Number };
        if (c.Id == 0 && c.Number != 0)
        {
            do _kittyNextId = _kittyNextId == uint.MaxValue ? 0x7F00_0000 : _kittyNextId + 1;
            while (_images.ById(_kittyNextId) is not null);
            c.Id = _kittyNextId;
        }
        image.Id = c.Id;
        if (c.Id != 0 && _images.ById(c.Id) is { } old) _images.Remove(old); // replacing an image deletes its placements
        _images.Add(image);
        if (image.IsEvicted) { KittyReply(c, "ENOSPC:image larger than the memory budget"); return; }

        string? err = c.Action == 'T' ? KittyPlace(c, image) : null;
        KittyReply(c, err ?? "OK");
    }

    /// <summary>
    /// A failed transmission's reply. Only fixed texts: nothing from the request, the file system or an exception
    /// message (which could name a file, or carry control characters into the program's input).
    /// </summary>
    private static string KittyError(Exception e) => e switch
    {
        KittyException k => k.Message,
        ImageDecodeException => "EBADPNG:could not decode the image",
        _ => "EINVAL:bad image data", // any other failure inside a decoder: it never escapes Feed
    };

    /// <summary>Thrown with one of the fixed reply texts in this file.</summary>
#pragma warning disable S3871 // never leaves this class: KittyTransmit catches it and replies with its text (KittyError)
    private sealed class KittyException(string message) : Exception(message);
#pragma warning restore S3871

    private const string FileError = "EBADF:could not read the file";

    internal DecodedImage KittyDecode(KittyCommand c, ReadOnlySpan<byte> payload)
    {
        // Everything the keys say is checked before any data is read or inflated.
        int bpp = c.Format switch { 24 => 3, 32 => 4, 100 => 0, _ => throw new KittyException("EINVAL:unsupported format") };
        if (c.Compression is not ('\0' or 'z')) throw new KittyException("EINVAL:unsupported compression");
        long need = bpp != 0 ? KittyRawSize(c, bpp) : 0;

        string? deleteAfter = null;
        ImageFiles.FileStamp stamp = default;
        byte[] data = c.Medium switch
        {
            'd' => ImageDecoder.DecodeBase64(payload) ?? throw new KittyException("EINVAL:bad base64 data"),
            'f' or 't' or 's' => KittyReadFile(c, payload, out deleteAfter, out stamp),
            _ => throw new KittyException("EINVAL:unsupported transmission medium"),
        };

        DecodedImage decoded;
        try
        {
            if (c.Compression == 'z') data = Inflate(data, bpp == 0 ? ImageLimits.MaxEncodedBytes : need);
            decoded = bpp == 0 ? ImageDecoder.Decode(data) : RawPixels(c, data, bpp, need);
        }
        // What a file held is nobody's business but its owner's: "too short", "not a PNG", "bad zlib data" would tell
        // a program which files exist and roughly what is in them, so a file that gave no image is just unreadable.
        catch (Exception) when (c.Medium != 'd') { throw new KittyException(FileError); }
        // t=t / t=s hand the file over: deleted once it gave an image (anything else stays where it was).
        if (deleteAfter is not null) ImageFiles.DeleteIfUnchanged(deleteAfter, stamp);
        return decoded;
    }

    /// <summary>Bytes of raw pixel data the keys announce.</summary>
    private static long KittyRawSize(KittyCommand c, int bpp)
    {
        if (c.Width <= 0 || c.Height <= 0) throw new KittyException("EINVAL:s and v are required for raw pixel data");
        if ((long)c.Width * c.Height > ImageLimits.MaxPixels || c.Width > ImageLimits.MaxDimension || c.Height > ImageLimits.MaxDimension)
            throw new KittyException("EFBIG:image too large");
        return (long)c.Width * c.Height * bpp;
    }

    private static DecodedImage RawPixels(KittyCommand c, byte[] data, int bpp, long need)
    {
        if (data.Length < need) throw new KittyException("ENODATA:insufficient image data");
        byte[] rgba;
        if (bpp == 4) rgba = data.Length == need ? data : data.AsSpan(0, (int)need).ToArray();
        else
        {
            rgba = new byte[(long)c.Width * c.Height * 4];
            for (long i = 0, o = 0; i < need; i += 3, o += 4)
            {
                rgba[o] = data[i];
                rgba[o + 1] = data[i + 1];
                rgba[o + 2] = data[i + 2];
                rgba[o + 3] = 255;
            }
        }
        return new DecodedImage(c.Width, c.Height, rgba);
    }

    /// <summary>zlib-inflates at most <paramref name="limit"/> bytes; more output than that is an error, found without producing it.</summary>
    private static byte[] Inflate(byte[] data, long limit)
    {
        var output = new MemoryStream();
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            var buf = new byte[81920];
            while (true)
            {
                // Never asks for more than one byte past the limit.
                int want = (int)Math.Min(buf.Length, limit - output.Length + 1);
                int n = z.Read(buf, 0, want);
                if (n <= 0) break;
                if (output.Length + n > limit) throw new KittyException("EFBIG:decompressed data too large");
                output.Write(buf, 0, n);
            }
        }
        catch (InvalidDataException) { throw new KittyException("EINVAL:bad zlib data"); }
        return output.ToArray();
    }

    /// <summary>
    /// t=f / t=t / t=s: the payload is a base64 path (or POSIX shared memory name). Off unless
    /// <see cref="Terminal.AllowImageFiles"/>. Only regular, non-empty files are read (see <see cref="ImageFiles"/>); t=t files
    /// must be in a temp directory and contain "tty-graphics-protocol" in their name. t=t files and t=s objects are
    /// deleted once they were read and decoded (not when that failed). Every failure to read gives the same reply, so this
    /// can't be used to find out which files exist, how big they are or whether they are readable.
    /// </summary>
    private byte[] KittyReadFile(KittyCommand c, ReadOnlySpan<byte> payload, out string? deleteAfter, out ImageFiles.FileStamp stamp)
    {
        deleteAfter = null;
        if (!grid.AllowImageFiles) throw new KittyException("EPERM:file transmission is disabled");
        string name = ImageFiles.DecodeName(payload) ?? throw new KittyException("EINVAL:bad file name");
        string path = c.Medium == 's' ? KittySharedMemoryPath(name) : KittyAbsolutePath(name);
        if (c.Medium == 't') CheckKittyTempFile(path);

        var data = ImageFiles.Read(path, Math.Max(0, c.Offset), Math.Max(0, c.Size), out stamp) ?? throw new KittyException(FileError);
        if (c.Medium is 't' or 's') deleteAfter = path;
        return data;
    }

    private static string KittySharedMemoryPath(string name)
    {
        if (!OperatingSystem.IsLinux()) throw new KittyException("EINVAL:shared memory is not supported here");
        return ImageFiles.SharedMemoryPath(name) ?? throw new KittyException("EINVAL:bad shared memory name");
    }

    private static string KittyAbsolutePath(string name)
    {
        if (!Path.IsPathRooted(name)) throw new KittyException("EINVAL:file path must be absolute");
        try { return Path.GetFullPath(name); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new KittyException("EINVAL:bad file name");
        }
    }

    /// <summary>A t=t file must be in a temp directory and have "tty-graphics-protocol" in its name.</summary>
    private static void CheckKittyTempFile(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        char separator = Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; // Windows paths ignore case
        string tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(separator);
        bool inTemp = dir.Equals(tmp, comparison) || dir.StartsWith(tmp + separator, comparison)
#pragma warning disable S5443 // only compares a GetFullPath-normalised path against the allowed temp directories; nothing is created here, and the read is O_NOFOLLOW
            || dir == "/tmp" || dir.StartsWith("/tmp/", StringComparison.Ordinal) || dir == "/dev/shm";
#pragma warning restore S5443
        if (!inTemp || !Path.GetFileName(path).Contains("tty-graphics-protocol", StringComparison.Ordinal))
            throw new KittyException("EPERM:temporary file is not in a temp directory or lacks tty-graphics-protocol in its name");
    }

    /// <summary>a=p / a=T: places an image at the cursor (or registers a virtual placement). Returns an error or null.</summary>
    private string? KittyPlace(KittyCommand c, TerminalImage image)
    {
        int sx = Math.Clamp(c.X, 0, image.Width), sy = Math.Clamp(c.Y, 0, image.Height);
        int sw = c.W > 0 ? Math.Min(c.W, image.Width - sx) : image.Width - sx;
        int sh = c.H > 0 ? Math.Min(c.H, image.Height - sy) : image.Height - sy;
        if (sw <= 0 || sh <= 0) return "EINVAL:empty source rectangle";
        var p = NewKittyPlacement(c, image, sx, sy, sw, sh);

        if (p.Virtual)
        {
            if (c.ParentId != 0) return "EINVAL:a virtual placement can't be relative";
            _images.AddPlacement(p, grid.IsAlternateScreen);
            return null;
        }
        if (c.ParentId != 0) return KittyPlaceRelative(c, p);

        if (c.Cursor != 1) grid.WrapPendingCursor();
        p.Line = grid.ScreenTopLine + grid.CursorY;
        p.Column = grid.CursorX;
        _images.AddPlacement(p, grid.IsAlternateScreen);

        if (c.Cursor != 1) grid.MoveCursorPastPlacement(p);
        grid.PruneImages();
        return null;
    }

    /// <summary>The placement <paramref name="c"/> describes, sized in cells; not positioned or registered yet.</summary>
    private KittyPlacement NewKittyPlacement(KittyCommand c, TerminalImage image, int sx, int sy, int sw, int sh)
    {
        int cols = Math.Clamp(c.Columns, 0, MaxPlacementCells), rows = Math.Clamp(c.Rows, 0, MaxPlacementCells);
        var p = new KittyPlacement
        {
            Image = image,
            PlacementId = c.Placement,
            RequestedColumns = cols,
            RequestedRows = rows,
            SourceX = sx, SourceY = sy, SourceWidth = sw, SourceHeight = sh,
            OffsetX = Math.Clamp(c.CellX, 0, grid.CellWidth - 1),
            OffsetY = Math.Clamp(c.CellY, 0, grid.CellHeight - 1),
            Z = c.Z,
            Virtual = c.Virtual == 1,
            ParentImageId = c.ParentId,
            ParentPlacementId = c.ParentPlacement,
            ParentDx = c.ParentDx,
            ParentDy = c.ParentDy,
        };
        int cellW = grid.CellWidth, cellH = grid.CellHeight;
        var (w, h) = p.DisplaySize(cellW, cellH);
        p.Columns = cols > 0 ? cols : CellsFor(p.OffsetX + w, cellW);
        p.Rows = rows > 0 ? rows : CellsFor(p.OffsetY + h, cellH);
        return p;
    }

    /// <summary>
    /// Most cells a placement covers in either direction. The cursor moves past a placement one line feed per row and
    /// to its right edge, so a size a program makes up (c=, r=, or one derived from an extreme aspect ratio) must stay
    /// small enough for both: far more than any screen, far less than int.MaxValue.
    /// </summary>
    private const int MaxPlacementCells = ImageLimits.MaxDimension;

    private static int CellsFor(float pixels, int cellPixels) => (int)Math.Clamp(MathF.Ceiling(pixels / cellPixels), 1, MaxPlacementCells);

    private void KittyDelete(KittyCommand c)
    {
        char sel = c.Delete == '\0' ? 'a' : c.Delete;
        bool free = char.IsUpper(sel);
        char lower = char.ToLowerInvariant(sel);
        if (KittyDeleteMatch(lower, c) is not { } match) return;
        bool alt = grid.IsAlternateScreen;
        _images.RemovePlacements(alt, match, free);
        if (alt && lower is 'i' or 'n' or 'r') _images.RemovePlacements(false, match, free);
        if (free) KittyFreeSelected(c, sel);
    }

    /// <summary>The placements selector <paramref name="sel"/> deletes, or null when it can't delete any.</summary>
    private Predicate<KittyPlacement>? KittyDeleteMatch(char sel, KittyCommand c)
    {
        long top = grid.ScreenTopLine;
        long cursorLine = top + grid.CursorY;
        int rows = grid.Rows, cursorX = grid.CursorX;
        return sel switch
        {
            'a' => p => IsGridPlacement(p) && p.Line + Math.Max(1, p.Rows) > top && p.Line < top + rows,
            'i' => p => p.Image.Id == c.Id && (c.Placement == 0 || p.PlacementId == c.Placement),
            'n' => _images.ByNumber(c.Number) is { } img ? p => p.Image == img && (c.Placement == 0 || p.PlacementId == c.Placement) : null,
            'c' => p => IsGridPlacement(p) && p.Intersects(cursorLine, cursorX),
            'p' => p => IsGridPlacement(p) && p.Intersects(top + c.Y - 1, c.X - 1),
            'q' => p => IsGridPlacement(p) && p.Intersects(top + c.Y - 1, c.X - 1) && p.Z == c.Z,
            'r' => p => p.Image.Id >= (uint)c.X && p.Image.Id <= (uint)c.Y,
            'x' => p => IsGridPlacement(p) && c.X - 1 >= p.Column && c.X - 1 < p.Column + p.Columns,
            'y' => p => IsGridPlacement(p) && top + c.Y - 1 >= p.Line && top + c.Y - 1 < p.Line + p.Rows,
            'z' => p => IsGridPlacement(p) && p.Z == c.Z,
            _ => null,
        };
    }

    private static bool IsGridPlacement(KittyPlacement p) => !p.Virtual && !p.RelativeToVirtual;

    /// <summary>Upper-case id selectors free the image even when it had no placements left.</summary>
    private void KittyFreeSelected(KittyCommand c, char sel)
    {
        if (sel == 'I' && c.Placement == 0 && _images.ById(c.Id) is { } byId) _images.Remove(byId);
        else if (sel == 'N' && c.Placement == 0 && _images.ByNumber(c.Number) is { } byNum) _images.Remove(byNum);
        else if (sel == 'R')
            foreach (var img in _images.Images.Where(i => i.Id >= (uint)c.X && i.Id <= (uint)c.Y).ToList()) _images.Remove(img);
    }

    /// <summary>Replies <c>ESC _ G i=…[,I=…][,p=…] ; message ESC \</c>, honouring q= and the "no id, no reply" rule.</summary>
    private void KittyReply(KittyCommand c, string message)
    {
        bool ok = message == "OK";
        if (c.Quiet >= 2 || (ok && c.Quiet == 1)) return;
        if (c.Id == 0 && c.Number == 0) return;
        var sb = new StringBuilder("\e_G");
        AppendKittyReplyIds(sb, c);
        sb.Append(';');
        // Replies only ever carry fixed texts; this keeps it that way should one ever include something else.
        foreach (char ch in message.Length > 256 ? message[..256] : message)
            sb.Append(ch is >= ' ' and < '\x7f' ? ch : '?');
        sb.Append("\e\\");
        replies.Send(sb.ToString());
    }

    private static void AppendKittyReplyIds(StringBuilder sb, KittyCommand c)
    {
        if (c.Id != 0) sb.Append("i=").Append(c.Id);
        if (c.Number != 0) sb.Append(c.Id != 0 ? "," : "").Append("I=").Append(c.Number);
        if (c.Placement != 0) sb.Append(",p=").Append(c.Placement);
    }

    /// <summary>Longest chain of relative placements (the protocol asks for at least 8).</summary>
    private const int MaxRelativeDepth = 8;

    private string? KittyPlaceRelative(KittyCommand c, KittyPlacement p)
    {
        bool alt = grid.IsAlternateScreen;
        var parent = _images.FindPlacement(c.ParentId, c.ParentPlacement, alt)
            ?? (_images.FindPlacement(c.ParentId, c.ParentPlacement, !alt) is { Virtual: true } v ? v : null);
        if (parent is null) return "ENOPARENT:no such parent placement";

        // The new placement replaces any with the same image and placement id: hanging it below itself is a cycle.
        int depth = 0;
        for (var q = parent; q is not null; q = _images.ParentOf(q, alt))
        {
            if (p.PlacementId != 0 && q.Image == p.Image && q.PlacementId == p.PlacementId) return "ECYCLE:placement would be its own parent";
            if (++depth > MaxRelativeDepth) return "ETOODEEP:chain of relative placements too long";
            if (!q.IsRelative) break;
        }

        p.RelativeToVirtual = parent.Virtual || parent.RelativeToVirtual;
        p.Line = (parent.Virtual ? 0 : parent.Line) + c.ParentDy;
        p.Column = (parent.Virtual ? 0 : parent.Column) + c.ParentDx;
        _images.AddPlacement(p, alt);
        grid.PruneImages();
        return null; // the cursor never moves for a relative placement
    }

    /// <summary>The image a command names by id or by number, or null.</summary>
    private TerminalImage? KittyImageFor(KittyCommand c)
    {
        if (c.Id != 0) return _images.ById(c.Id);
        return c.Number != 0 ? _images.ByNumber(c.Number) : null;
    }

    private void KittyAnimate(KittyCommand c, ReadOnlySpan<byte> payload)
    {
        var image = KittyImageFor(c);
        if (image is null) { KittyReply(c, "ENOENT:no such image"); return; }
        c.Id = image.Id;
        string? err;
        try
        {
            err = c.Action switch
            {
                'f' => _animator.Frame(c, KittyDecode(c, payload), image),
                'a' => _animator.Control(c, image),
                _ => _animator.Compose(c, image),
            };
        }
        catch (Exception e) { err = KittyError(e); }
        KittyReply(c, err ?? "OK");
    }
}
