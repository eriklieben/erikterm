namespace ErikTerm.Ui;

/// <summary>
/// How much a window's overlays (palette, worktree list, Finish dialog, Ctrl+Tab list ...) grow with its font zoom.
/// They follow the zoom but stop where the largest of them no longer fits the window, and don't shrink past legibility.
/// </summary>
public static class OverlayZoom
{
    /// <summary>The smallest overlay text the zoom may reach, in points (a smaller command_palette_font_size stays as set).</summary>
    public const float MinPoints = 9;
    /// <summary>Widest minimum among the overlays (the Finish dialog) with its margins, in points.</summary>
    public const float FitWidthPoints = 508;
    /// <summary>Room the overlays need in lines of their text: footers across, the tallest dialog (Finish, about 33 lines at its fullest) down.</summary>
    public const float FitWidthLines = 36, FitHeightLines = 34;
    /// <summary>The window-size limit moves in these steps, so a live resize doesn't rasterize the UI at every pixel.</summary>
    private const float Step = 0.05f;

    /// <summary>The factor for <paramref name="uiPx"/> (the overlays' text size at zoom 1, device pixels; <paramref name="scale"/> device pixels per point).</summary>
    public static float For(float zoom, float windowW, float windowH, float uiPx, float scale)
    {
        float points = uiPx / (scale * 96f / 72f);
        float floor = MathF.Min(1, MinPoints / points);
        float fit = MathF.Min(windowW / (scale * FitWidthPoints), MathF.Min(windowW / (uiPx * FitWidthLines), windowH / (uiPx * FitHeightLines)));
        float ceiling = MathF.Max(1, MathF.Floor(fit / Step) * Step); // too small a window keeps today's size: the overlays clip their own lists
        return MathF.Min(MathF.Max(zoom, floor), ceiling);
    }
}
