using ErikTerm.App.Panes;
using ErikTerm.Ui;

namespace ErikTerm.App.Rendering;

/// <summary>
/// When a window draws: frames held back while a pane floods output (<see cref="FloodFrames"/>), and
/// ERIKTERM_FRAME_STATS=1 (frame build time, quads and row-cache hits on stderr every few seconds).
/// </summary>
internal sealed class FramePacing
{
    private readonly FloodFrames _flood = new();

    /// <summary>ERIKTERM_FRAME_STATS=1; null otherwise.</summary>
    public FrameStats? Stats { get; } =
        Environment.GetEnvironmentVariable("ERIKTERM_FRAME_STATS") is "1" or "true" ? new FrameStats() : null;

    /// <summary>Hook from Render: read without the lock, because taking it is what a held frame is meant to avoid.</summary>
    public bool HoldForFlood(long now, bool redraw, LayoutTree<Pane> layout) =>
        _flood.Holding = !redraw && _flood.MayHold(now) && OnlyFloodsToDraw(layout);

    /// <summary>A pane printing at a readable pace in the same window still gets its frames.</summary>
    private static bool OnlyFloodsToDraw(LayoutTree<Pane> layout)
    {
        bool flood = false;
        foreach (var p in layout.LeafList)
        {
            if (p.LayoutHidden || !p.Term.Dirty) continue;
            if (!FloodFrames.IsFlood(p.Term.LinesScrolled - p.LinesScrolledAtFrame, p.Term.Rows)) return false;
            flood = true;
        }
        return flood;
    }

    /// <summary>Hook from Render, once a frame was built.</summary>
    public void Drawn(long now) => _flood.Drawn(now);

    /// <summary>Hook from the event loop: output needn't wake it chunk by chunk while the frame is held.</summary>
    public bool IsHolding(long now) => _flood.IsHolding(now);

    /// <summary>Hook from the event loop: the held frame is due even if the output stopped.</summary>
    public int WakeMs(long now) => _flood.WakeMs(now);

    /// <summary>Hook from App.EventLoop: idle intervals get reported too.</summary>
    public void ReportStats(PaneRenderer renderer, long now, uint window) => Stats?.Report(renderer, now, window);
}
