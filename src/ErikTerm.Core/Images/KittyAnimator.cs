namespace ErikTerm.Core.Images;

/// <summary>
/// Kitty graphics animation (for <see cref="KittyGraphics"/>) —
/// <c>a=f</c> frame data (new frames on a base frame <c>c=</c> or background color <c>Y=</c>, or edits of frame <c>r=</c>,
/// at <c>x=</c>/<c>y=</c>, alpha-blended or replacing with <c>X=1</c>, gap <c>z=</c>), <c>a=a</c> control (<c>s=</c>
/// 1 stop / 2 run and wait for frames / 3 run, <c>c=</c> current frame, <c>r=</c>+<c>z=</c> a frame's gap, <c>v=</c> loops)
/// and <c>a=c</c> composition (frame <c>r=</c> onto frame <c>c=</c>). Playback is in <see cref="ImageAnimation"/>.
/// </summary>
internal sealed class KittyAnimator(ImageStore images)
{
    private readonly ImageStore _images = images;

    /// <summary>a=f: frame data (decoded), composed onto a new frame (base frame c= or color Y=) or onto frame r=.</summary>
    public string? Frame(KittyCommand c, DecodedImage data, TerminalImage image)
    {
        var anim = image.Animate();
        int w = image.Width, h = image.Height;
        bool isNew = c.Rows <= 0;
        string? err = isNew ? KittyNewFrameTarget(c, anim, w, h, out var target) : KittyExistingFrame(c, anim, out target);
        if (err is not null) return err;
        Composite(data.Rgba, data.Width, 0, 0, target, w, h, c.X, c.Y, data.Width, data.Height, overwrite: c.CellX == 1);

        if (isNew)
        {
            anim.AddFrame(target, c.Z == 0 ? ImageAnimation.DefaultGapMs : c.Z);
            _images.FramesGrew(image, target.Length);
            if (image.IsEvicted) return "ENOSPC:animation larger than the memory budget";
        }
        else
        {
            if (c.Z != 0) anim.SetGap(c.Rows - 1, c.Z);
            anim.Touched();
            _images.Changed();
        }
        return null;
    }

    private static string? KittyExistingFrame(KittyCommand c, ImageAnimation anim, out byte[] target)
    {
        target = [];
        if (c.Rows > anim.FrameCount || anim.Frame(c.Rows - 1) is not { } existing) return "ENOENT:no such frame";
        target = existing;
        return null;
    }

    /// <summary>A new frame's canvas: a copy of base frame c=, or filled with color Y=, or transparent.</summary>
    private static string? KittyNewFrameTarget(KittyCommand c, ImageAnimation anim, int w, int h, out byte[] target)
    {
        target = [];
        if (anim.FrameCount >= ImageAnimation.MaxFrames) return "ENOSPC:too many frames";
        target = new byte[(long)w * h * 4];
        if (c.Columns > 0)
        {
            if (c.Columns > anim.FrameCount || anim.Frame(c.Columns - 1) is not { } baseFrame) return "ENOENT:no such base frame";
            baseFrame.AsSpan().CopyTo(target);
        }
        else if (c.Background != 0) FillBackground(target, c.Background);
        return null;
    }

    /// <summary>Fills an RGBA buffer with a color given as 0xRRGGBBAA.</summary>
    private static void FillBackground(byte[] target, uint color)
    {
        byte r = (byte)(color >> 24), g = (byte)(color >> 16), b = (byte)(color >> 8), a = (byte)color;
        for (int i = 0; i < target.Length; i += 4) { target[i] = r; target[i + 1] = g; target[i + 2] = b; target[i + 3] = a; }
    }

    /// <summary>a=a: s= state, c= current frame, r= + z= a frame's gap, v= loop count.</summary>
    public string? Control(KittyCommand c, TerminalImage image)
    {
        var anim = image.Animate();
        long now = _images.Clock();
        if (c.Rows > 0)
        {
            if (c.Rows > anim.FrameCount) return "ENOENT:no such frame";
            if (c.Z != 0) anim.SetGap(c.Rows - 1, c.Z);
        }
        if (c.Height > 0) anim.SetLoops(c.Height);
        if (c.Columns > 0)
        {
            if (c.Columns > anim.FrameCount) return "ENOENT:no such frame";
            anim.SetCurrent(c.Columns - 1, now);
        }
        if (c.Width is >= 1 and <= 3) anim.SetState((ImageAnimation.PlayState)c.Width, now);
        _images.Changed();
        return null;
    }

    /// <summary>a=c: copies a rectangle of frame r= (at X=, Y=) onto frame c= (at x=, y=), blended unless C=1.</summary>
    public string? Compose(KittyCommand c, TerminalImage image)
    {
        var anim = image.Animate();
        int w = image.Width, h = image.Height;
        if (c.Rows <= 0 || c.Rows > anim.FrameCount || c.Columns <= 0 || c.Columns > anim.FrameCount) return "ENOENT:no such frame";
        int rw = c.W > 0 ? c.W : w, rh = c.H > 0 ? c.H : h;
        int sx = c.CellX, sy = c.CellY, dx = c.X, dy = c.Y;
        if (sx < 0 || sy < 0 || dx < 0 || dy < 0 || (long)sx + rw > w || (long)sy + rh > h || (long)dx + rw > w || (long)dy + rh > h)
            return "EINVAL:rectangle out of bounds";
        if (c.Rows == c.Columns && sx < dx + rw && dx < sx + rw && sy < dy + rh && dy < sy + rh)
            return "EINVAL:source and destination rectangles overlap";
        var src = anim.Frame(c.Rows - 1);
        var dst = anim.Frame(c.Columns - 1);
        if (src is null || dst is null) return "ENOENT:no such frame";
        Composite(src, w, sx, sy, dst, w, h, dx, dy, rw, rh, overwrite: c.Cursor == 1);
        anim.Touched();
        _images.Changed();
        return null;
    }

    /// <summary>
    /// Draws the <paramref name="rw"/>×<paramref name="rh"/> rectangle at (<paramref name="sx"/>, <paramref name="sy"/>) of an
    /// RGBA buffer <paramref name="srcStride"/> pixels wide onto <paramref name="dst"/> at (<paramref name="dx"/>, <paramref name="dy"/>),
    /// clipped; straight-alpha "over" unless <paramref name="overwrite"/>.
    /// </summary>
    internal static void Composite(byte[] src, int srcStride, int sx, int sy, byte[] dst, int dw, int dh, int dx, int dy, int rw, int rh, bool overwrite)
    {
        if (dx < 0) { sx -= dx; rw += dx; dx = 0; }
        if (dy < 0) { sy -= dy; rh += dy; dy = 0; }
        rw = Math.Min(rw, dw - dx);
        rh = Math.Min(rh, dh - dy);
        if (rw <= 0 || rh <= 0) return;
        for (int y = 0; y < rh; y++)
        {
            int so = ((sy + y) * srcStride + sx) * 4, d = ((dy + y) * dw + dx) * 4;
            if (so < 0 || so + rw * 4 > src.Length) break;
            if (overwrite) Array.Copy(src, so, dst, d, rw * 4);
            else BlendRow(src, so, dst, d, rw);
        }
    }

    /// <summary>Straight-alpha "over" of <paramref name="width"/> pixels from <paramref name="so"/> onto <paramref name="d"/>.</summary>
    private static void BlendRow(byte[] src, int so, byte[] dst, int d, int width)
    {
        for (int x = 0; x < width; x++, so += 4, d += 4)
        {
            int sa = src[so + 3];
            if (sa == 255) { dst[d] = src[so]; dst[d + 1] = src[so + 1]; dst[d + 2] = src[so + 2]; dst[d + 3] = 255; continue; }
            if (sa == 0) continue;
            int da = dst[d + 3];
            int oa = sa * 255 + da * (255 - sa); // ×255
            for (int k = 0; k < 3; k++)
                dst[d + k] = (byte)((src[so + k] * sa * 255 + dst[d + k] * da * (255 - sa) + oa / 2) / oa);
            dst[d + 3] = (byte)((oa + 127) / 255);
        }
    }
}
