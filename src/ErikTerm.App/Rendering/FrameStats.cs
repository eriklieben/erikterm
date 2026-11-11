using System.Diagnostics;

namespace ErikTerm.App.Rendering;

/// <summary>ERIKTERM_FRAME_STATS=1: per-interval frame build times (lock → overlays, before Present) and sizes, per window.</summary>
internal sealed class FrameStats
{
    private const int ReportMs = 3000;
    private readonly double[] _times = new double[8192], _sorted = new double[8192];
    private int _count;
    private long _quads, _allocated, _start, _startAlloc, _intervalStart = Environment.TickCount64, _maxQuads;

    public void BeginFrame()
    {
        _startAlloc = GC.GetAllocatedBytesForCurrentThread();
        _start = Stopwatch.GetTimestamp();
    }

    public void EndFrame(int quads, PaneRenderer renderer, uint window)
    {
        double ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        _allocated += GC.GetAllocatedBytesForCurrentThread() - _startAlloc;
        if (_count < _times.Length) _times[_count++] = ms;
        _quads += quads;
        _maxQuads = Math.Max(_maxQuads, quads);
        Report(renderer, Environment.TickCount64, window);
    }

    /// <summary>Also called from the event loop, so idle intervals (no frames) get reported.</summary>
    public void Report(PaneRenderer renderer, long now, uint window)
    {
        long elapsed = now - _intervalStart;
        if (elapsed < ReportMs) return;
        if (_count == 0)
        {
            Console.Error.WriteLine($"frame-stats: window {window}  {elapsed / 1000.0:F1}s  0 frames (idle)");
        }
        else
        {
            Array.Copy(_times, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            double sum = 0;
            for (int i = 0; i < _count; i++) sum += _sorted[i];
            double p95 = _sorted[Math.Min(_count - 1, (int)(_count * 0.95))];
            long rows = renderer.RowsBuilt + renderer.RowsReused;
            Console.Error.WriteLine(
                $"frame-stats: window {window}  {elapsed / 1000.0:F1}s  {_count} frames ({_count * 1000.0 / elapsed:F1}/s)  build avg {sum / _count:F3} ms  p95 {p95:F3} ms  " +
                $"max {_sorted[_count - 1]:F3} ms  quads avg {_quads / _count} max {_maxQuads}  rows built {renderer.RowsBuilt * 100.0 / Math.Max(1, rows):F1}% " +
                $"({renderer.RowsBuilt / _count}/frame)  alloc {_allocated / _count} B/frame");
        }
        _count = 0;
        _quads = _allocated = _maxQuads = 0;
        renderer.RowsBuilt = renderer.RowsReused = 0;
        _intervalStart = now;
    }
}
