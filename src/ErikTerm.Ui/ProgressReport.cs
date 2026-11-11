namespace ErikTerm.Ui;

/// <summary>OSC 9 ; 4 progress states (ConEmu / Windows Terminal): 0 none, 1 normal, 2 error, 3 indeterminate, 4 paused.</summary>
public enum ProgressState : byte { None = 0, Normal = 1, Error = 2, Indeterminate = 3, Paused = 4 }

/// <summary>A pane's (or a tab's combined) progress report: a state and a percentage (meaningless when indeterminate).</summary>
public readonly record struct ProgressReport(ProgressState State, int Value)
{
    /// <summary>From the terminal's raw (state, value); anything out of range is no progress.</summary>
    public static ProgressReport From(int state, int value) =>
        state is >= 1 and <= 4 ? new((ProgressState)state, Math.Clamp(value, 0, 100)) : default;

    public bool IsActive => State != ProgressState.None;
}

/// <summary>
/// Combines the reports of a tab's panes into the one its tab shows: the most pressing state wins — error, then paused,
/// then normal, then indeterminate — and a determinate bar shows the mean of the determinate panes' percentages.
/// </summary>
public struct ProgressAggregate
{
    private int _sum, _determinate;
    private bool _error, _paused, _indeterminate;

    public void Add(ProgressReport r)
    {
        switch (r.State)
        {
            case ProgressState.None: return;
            case ProgressState.Indeterminate: _indeterminate = true; return;
            case ProgressState.Error: _error = true; break;
            case ProgressState.Paused: _paused = true; break;
        }
        _sum += r.Value;
        _determinate++;
    }

    public readonly ProgressReport Result
    {
        get
        {
            if (_determinate <= 0) return _indeterminate ? new(ProgressState.Indeterminate, 0) : default;
            var state = ProgressState.Normal;
            if (_error) state = ProgressState.Error;
            else if (_paused) state = ProgressState.Paused;
            return new(state, (int)MathF.Round(_sum / (float)_determinate));
        }
    }
}
