namespace ErikTerm.Core;

/// <summary>Lets go of the rows a cleared scrollback left behind (<see cref="Terminal.HasSpareRows"/>) once a pane has had no output for a while.</summary>
public sealed class SpareRowRelease(Terminal terminal, Lock gate, int keptMs, Action released) : IDisposable
{
    private readonly AfterOutputStops _timer = new(keptMs, () => terminal.HasSpareRows, () =>
    {
        lock (gate) terminal.ReleaseSpareRows();
        released();
    });

    /// <summary>The program printed: the spare rows, if any, may be needed a little longer.</summary>
    public void NoteOutput() => _timer.NoteOutput();

    internal void Tick() => _timer.Tick();

    public void Dispose() => _timer.Dispose();
}
