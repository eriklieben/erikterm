using System.Text;
using ErikTerm.App.Panes;
using ErikTerm.Core;
using ErikTerm.Pty;

namespace ErikTerm.Tests;

/// <summary>
/// Real <see cref="Pane"/>s for the app's window-feature classes, without a window: a terminal with the given text and a
/// pty whose child just sleeps (disposing the pane hangs it up). Unix only; tests using it run on Linux and macOS.
/// </summary>
internal static class TestPanes
{
    public static Pane Spawn(int columns = 40, int rows = 6, string? text = null, int scrollback = 1000, string? workingDirectory = null)
    {
        var pty = Pty.Pty.Spawn(new PtyOptions
        {
            Program = "/bin/sh",
            Arguments = ["sh", "-c", "exec sleep 60"],
            Columns = columns,
            Rows = rows,
            WorkingDirectory = workingDirectory,
        });
        var term = new Terminal(columns, rows, scrollback);
        if (text is not null) term.Feed(Encoding.UTF8.GetBytes(text));
        return new Pane(term, pty);
    }
}
