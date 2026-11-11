namespace ErikTerm.Ui;

/// <summary>
/// Where a pane's overlay scrollbar sits (in pixels), shared by the renderer and the mouse. The thumb never gets
/// smaller than a grabbable minimum, so marks are placed by the thumb's own travel: a tick lies under the thumb
/// exactly while its line is in view (<see cref="TickRows"/> pixel rows hold the whole history and screen).
/// </summary>
public readonly record struct ScrollbarGeometry(float X, float Width, float TrackY, float TrackH, float ThumbY, float ThumbH, int TickRows)
{
    public static ScrollbarGeometry Of(RectF pane, int scrollback, int rows, int viewOffset, float scale)
    {
        long total = (long)scrollback + rows;
        float trackH = MathF.Max(1, pane.H - 8 * scale);
        float thumbH = MathF.Min(trackH, MathF.Max(24 * scale, trackH * rows / total));
        float travel = trackH - thumbH;
        float pos = scrollback == 0 ? 1 : (float)(scrollback - viewOffset) / scrollback; // 1 = bottom
        float width = 5 * scale;
        float trackY = pane.Y + 4 * scale;
        int tickRows = scrollback == 0 ? 0 : (int)Math.Min(trackH, MathF.Round(travel * total / scrollback));
        return new ScrollbarGeometry(pane.Right - width - 3 * scale, width, trackY, trackH, trackY + travel * pos, thumbH, tickRows);
    }

    /// <summary>The view offset (lines scrolled back) that puts the thumb's top at <paramref name="thumbY"/>.</summary>
    public int OffsetForThumbAt(float thumbY, int scrollback)
    {
        float travel = TrackH - ThumbH;
        if (travel <= 0 || scrollback <= 0) return 0;
        float pos = Math.Clamp((thumbY - TrackY) / travel, 0, 1);
        return scrollback - (int)MathF.Round(pos * scrollback);
    }
}
