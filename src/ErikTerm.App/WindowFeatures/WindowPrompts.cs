using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Shell integration's prompt marks (OSC 133): ScrollToPrompt jumps between prompts, SelectCommandOutput /
/// CopyCommandOutput select (and copy) a command's output, which the prompt gutter and the context menu offer too
/// (<see cref="WindowLinks"/>).
/// </summary>
internal sealed class WindowPrompts(IFeatureWindow window, IPaneInput input, Lock gate)
{
    private const string NoMarksDetail = "erikterm integrates zsh, bash and fish by itself (shell_integration); for other shells see the README";

    /// <summary>Command block whose prompt or output contains the line (not the previous command's).</summary>
    public static CommandBlock? BlockContaining(Terminal t, long line)
    {
        var block = t.CommandAt(line);
        return block is { } b && line >= b.PromptLine && line <= b.OutputEnd ? b : null;
    }

    public static string DescribeBlock(CommandBlock b)
    {
        string cmd = b.Command.Length > 0 ? b.Command : "Command";
        string lines = b.LineCount == 1 ? "1 line" : $"{b.LineCount} lines";
        string status = b.ExitCode switch { Row.NoExitCode => "", 0 => "", _ => $" · exit {b.ExitCode}" };
        return $"{cmd} · {lines}{status}";
    }

    public void ScrollToPrompt(int dir, uint key, ushort mod, long now)
    {
        var pane = window.Focused;
        long? target;
        bool usable;
        lock (gate)
        {
            var t = pane.Term;
            // While a command runs at the live view (fzf, a REPL) its keys are its own.
            usable = t.HasPromptMarks && !t.IsAlternateScreen && !(key != 0 && t.CommandRunning && t.ViewOffset == 0);
            target = usable ? t.FindPrompt(t.ViewTopLine, dir) : null;
            if (usable)
            {
                if (target is { } line) t.ScrollViewToLine(line);
                else if (dir > 0) t.ResetView();
            }
        }
        if (!usable)
        {
            // Without shell integration, in a full-screen app or while a command runs, the key belongs to the program.
            if (key != 0) input.PassThrough(key, mod);
            else window.Toast(ToastKind.Info, "No prompts marked yet", NoMarksDetail);
            return;
        }
        pane.ScrollActivityAt = now;
        if (target is { } l)
        {
            pane.Links.FlashLine = l;
            pane.Links.Flash.Snap(1);
            pane.Links.Flash.Set(0, now, 700);
        }
    }

    public void SelectLastCommand(bool copy)
    {
        var pane = window.Focused;
        CommandBlock? block;
        bool marks;
        lock (gate)
        {
            var t = pane.Term;
            marks = t.HasPromptMarks;
            // The command under the selection/view if scrolled back, else the last one that ran.
            long at;
            if (pane.Selection?.Range(t) is var (sl, _, _, _)) at = sl;
            else at = t.ViewOffset > 0 ? t.ViewTopLine + t.Rows / 2 : t.ScreenTopLine + t.CursorY;
            block = t.CommandAt(at);
        }
        if (block is { } b) SelectBlock(pane, b, copy);
        else if (!marks) window.Toast(ToastKind.Info, "No prompts marked yet", NoMarksDetail);
        else window.Toast(ToastKind.Info, "No command output", "The last command didn't print anything");
    }

    public void SelectBlock(Pane pane, CommandBlock b, bool copy)
    {
        string text;
        lock (gate)
        {
            foreach (var other in window.Layout.LeafList) other.Selection = null;
            pane.Selection = new Selection
            {
                AnchorLine = b.OutputStart, AnchorCol = 0, HeadLine = b.OutputEnd, HeadCol = pane.Term.Columns - 1, Mode = SelectionMode.Line,
            };
            text = pane.Selection.Text(pane.Term);
            // Bring the start into view when it's off screen.
            var t = pane.Term;
            if (b.OutputStart < t.ViewTopLine || b.OutputStart >= t.ViewTopLine + t.Rows) t.ScrollViewToLine(Math.Max(t.FirstLine, b.PromptLine));
        }
        if (text.Length > 0) Sdl.SetPrimarySelectionText(text);
        if (copy && text.Length > 0) Sdl.SetClipboardText(text);
        window.Toast(ToastKind.Success, copy ? "Copied command output" : "Selected command output", DescribeBlock(b));
    }

    /// <summary>Command-palette hook.</summary>
    public static void AddCommands(Action<string, string, int, string, Func<bool>?> add)
    {
        add("Edit", "Quick Select…", ShellIcons.Zap, "QuickSelect", null);
        add("Edit", "Select Command Output", ShellIcons.Output, "SelectCommandOutput", null);
        add("Edit", "Copy Command Output", ShellIcons.Output, "CopyCommandOutput", null);
        add("View", "Scroll to Previous Prompt", Icons.ArrowUp, "ScrollToPrompt(-1)", null);
        add("View", "Scroll to Next Prompt", Icons.ArrowDown, "ScrollToPrompt(1)", null);
    }
}
