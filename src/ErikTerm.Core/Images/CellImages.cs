using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ErikTerm.Core.Images;

/// <summary>
/// The image protocols whose pictures are written into the grid as cell tiles (<see cref="IImageGrid.PlaceCellImage"/>):
/// sixel (<c>DCS P1;P2;P3 q … ST</c>, decoded by <see cref="Sixel"/>) and iTerm2 inline images (https://iterm2.com/documentation-images.html):
/// <c>OSC 1337 ; File=[args] : base64 BEL</c>, and the 3.5 multipart form <c>MultipartFile=[args]</c>,
/// <c>FilePart=base64</c> (repeated), <c>FileEnd</c>. Args: name, size, width, height (N cells, Npx, N%, auto),
/// preserveAspectRatio (default 1), inline (only inline=1 is shown; downloads are ignored), doNotMoveCursor.
/// The cursor ends right of an iTerm2 image on its last row, or stays put with doNotMoveCursor=1 (then the image never
/// scrolls the screen), and below a sixel image. The same iTerm2 file sent again (programs repaint) is decoded once.
/// </summary>
internal sealed class CellImages(ImageStore images, IImageGrid grid)
{
    private readonly ImageStore _images = images;
    private string? _multipartArgs;
    private MemoryStream? _multipartData;

    /// <summary>OSC 1337 ; File= / MultipartFile= / FilePart= / FileEnd; false for any other OSC 1337 (SetUserVar).</summary>
    public bool Osc1337(ReadOnlySpan<byte> arg)
    {
        if (arg.StartsWith("File="u8))
        {
            var rest = arg[5..];
            int colon = rest.IndexOf((byte)':');
            if (colon < 0) return true;
            ITermImage(Encoding.UTF8.GetString(rest[..colon]), rest[(colon + 1)..]);
        }
        else if (arg.StartsWith("MultipartFile="u8))
        {
            _multipartArgs = Encoding.UTF8.GetString(arg[14..]);
            _multipartData = new MemoryStream();
        }
        else if (arg.StartsWith("FilePart="u8))
        {
            if (_multipartData is null) return true;
            if (_multipartData.Length + arg.Length > ImageLimits.MaxEncodedBytes / 3 * 4 + 16)
            {
                _multipartArgs = null;
                _multipartData = null;
                return true;
            }
            _multipartData.Write(arg[9..]);
        }
        else if (arg.SequenceEqual("FileEnd"u8))
        {
            if (_multipartArgs is null || _multipartData is null) return true;
            var args = _multipartArgs;
            var data = _multipartData;
            _multipartArgs = null;
            _multipartData = null;
            ITermImage(args, data.GetBuffer().AsSpan(0, (int)data.Length));
        }
        else return false;
        return true;
    }

    private void ITermImage(string args, ReadOnlySpan<byte> base64)
    {
        var options = ParseITermArgs(args);
        if (!options.Inline) return; // a download: not supported
        if (base64.Length > ImageLimits.MaxEncodedBytes / 3 * 4 + 16) return;
        if (ITermLoad(base64) is not { } image) return;

        int cw = grid.CellWidth, ch = grid.CellHeight;
        int available = Math.Max(1, grid.Columns - (grid.PendingWrap ? 0 : grid.CursorX));
        // Requested size in pixels (null = auto).
        float? reqW = ParseITermSize(options.Width, cw, grid.Columns), reqH = ParseITermSize(options.Height, ch, grid.Rows);
        float iw = image.Width, ih = image.Height;
        var (boxW, boxH) = ITermBox(reqW, reqH, iw, ih, options.Preserve, available, cw);
        int cols = Math.Max(1, (int)MathF.Ceiling(boxW / cw - 0.001f));
        int rows = Math.Max(1, (int)MathF.Ceiling(boxH / ch - 0.001f));

        // The picture inside the box: scaled to fit (centred) when preserving the aspect ratio, else stretched.
        float bw = boxW, bh = boxH;
        if (options.Preserve)
        {
            float scale = MathF.Min(boxW / iw, boxH / ih);
            bw = iw * scale;
            bh = ih * scale;
        }
        float cx = (cols * cw - bw) / 2 / cw, cy = (rows * ch - bh) / 2 / ch;
        if (reqW is null && reqH is null) cx = cy = 0; // native size: top-left
        grid.PlaceCellImage(new InlineImage(image, cols, rows, cx, cy, bw / cw, bh / ch), options.Stay ? ImageCursor.Stay : ImageCursor.AfterLastRow);
    }

    private readonly record struct Iterm2ImageOptions(string? Width, string? Height, bool Inline, bool Preserve, bool Stay);

    private static Iterm2ImageOptions ParseITermArgs(string args)
    {
        string? width = null, height = null;
        bool inline = false, preserve = true, stay = false;
        foreach (var part in args.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq], value = part[(eq + 1)..];
            switch (key)
            {
                case "width": width = value; break;
                case "height": height = value; break;
                case "inline": inline = value == "1"; break;
                case "preserveAspectRatio": preserve = value != "0"; break;
                case "doNotMoveCursor": stay = value == "1"; break;
            }
        }
        return new Iterm2ImageOptions(width, height, inline, preserve, stay);
    }

    /// <summary>The image for this payload, decoded and stored if it isn't already; null when there is nothing to show.</summary>
    private TerminalImage? ITermLoad(ReadOnlySpan<byte> base64)
    {
        byte[] hash = SHA256.HashData(base64);
        var image = _images.ByContent(hash);
        if (image is not null && !image.IsEvicted) return image;
        var file = ImageDecoder.DecodeBase64(base64);
        if (file is null || file.Length == 0) return null;
        DecodedImage decoded = ImageDecoder.DecodeOrPlaceholder(file);
        image = new TerminalImage(decoded) { ContentHash = hash, OnAltScreen = grid.IsAlternateScreen };
        _images.Add(image);
        return image.IsEvicted ? null : image;
    }

    /// <summary>The box the picture is laid out in, in pixels, from the requested sizes and the image's own.</summary>
    private static (float W, float H) ITermBox(float? reqW, float? reqH, float iw, float ih, bool preserve, int available, int cw)
    {
        if (reqW is null && reqH is null)
        {
            float boxW = iw, boxH = ih;
            if (boxW > available * cw) { boxH = boxH * available * cw / boxW; boxW = available * cw; } // shrink to fit the width
            return (boxW, boxH);
        }
        if (reqH is null) return (reqW!.Value, preserve ? reqW.Value * ih / iw : ih);
        if (reqW is null) return (preserve ? reqH.Value * iw / ih : iw, reqH.Value);
        return (reqW.Value, reqH.Value);
    }

    /// <summary>"N" cells, "Npx", "N%" of the terminal, "auto"/absent → pixels or null.</summary>
    private static float? ParseITermSize(string? value, int cellPx, int termCells)
    {
        if (string.IsNullOrEmpty(value) || value == "auto") return null;
        bool px = value.EndsWith("px", StringComparison.Ordinal), pct = value.EndsWith('%');
        string num = value;
        if (px) num = value[..^2];
        else if (pct) num = value[..^1];
        if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || n <= 0 || !float.IsFinite(n)) return null;
        float result = n * cellPx;
        if (px) result = n;
        else if (pct) result = n / 100 * termCells * cellPx;
        return Math.Min(result, ImageStore.MaxTileColumns * (float)cellPx);
    }

    /// <summary>A sixel image (the data after <c>DCS … q</c>), placed at the cursor; nothing for data that doesn't decode.</summary>
    public void PlaceSixel(ReadOnlySpan<byte> body)
    {
        DecodedImage decoded;
        try { decoded = Sixel.Decode(body); }
        catch (ImageDecodeException) { return; }
        var image = new TerminalImage(decoded) { OnAltScreen = grid.IsAlternateScreen };
        _images.Add(image);
        if (image.IsEvicted) return;
        int cw = grid.CellWidth, ch = grid.CellHeight;
        int cols = (decoded.Width + cw - 1) / cw, rows = (decoded.Height + ch - 1) / ch;
        grid.PlaceCellImage(new InlineImage(image, cols, rows, 0, 0, decoded.Width / (float)cw, decoded.Height / (float)ch), ImageCursor.NextLine);
    }
}
