using ErikTerm.Ui;

namespace ErikTerm.App.Host;

/// <summary>What the process's parts need from <see cref="App"/>: its windows, opening one, and waking the loop.</summary>
internal interface IWindowOwner
{
    /// <summary>The open windows, which owns which tab, and focus order.</summary>
    WindowSet<TermWindow, Tab> Windows { get; }
    /// <summary>The window with that SDL id; null for 0 or a window that isn't ours.</summary>
    TermWindow? WindowById(uint id);
    /// <summary>Opens a window (throws when SDL or the first pane fails); <paramref name="prepared"/>: one made beforehand.</summary>
    TermWindow CreateWindow(TermWindow.OpenOptions options, TermWindow? prepared = null);
    /// <summary>The loop is about to end: draw nothing more.</summary>
    bool Quitting { get; }
    /// <summary>Wakes the event loop; any thread.</summary>
    void Wake();
}
