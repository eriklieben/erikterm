using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Pty;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Close confirmation (window_close_confirmation, skip_close_confirmation_for_processes_named): closing a pane, tab or
/// window in which something other than a shell runs — Neovim, a build, a coding agent even when idle — asks first on
/// the info bar: "Close this pane? nvim is still running [Close Pane] [Cancel] ✕". Enter (or y) closes, Esc (or n)
/// cancels; while it is up the keyboard answers it. The window's close button / the compositor's close asks once for
/// every tab of the window ("Quit erikterm?" for the last window). Scripted closes (`erikterm cli kill-pane`, the
/// tmux emulation's kill-pane) and panes whose program exited never ask. Texts: Panes/CloseConfirmation.cs.
/// </summary>
internal sealed class CloseConfirmFlow(IWindowHost window, TabList<Tab> tabs)
{
    /// <summary>What the shown prompt does when confirmed; null when no prompt is up.</summary>
    private Action? _closeConfirmed;

    private static long Now => Environment.TickCount64;

    public bool IsPromptShown => _closeConfirmed is not null;

    /// <summary>Close Pane (Ctrl+Shift+W, palette, menu, vim mode).</summary>
    public void RequestClosePane(Pane pane) => ConfirmClose(CloseScope.Pane, [pane], () => window.ClosePane(pane));

    /// <summary>Close Tab (palette, tab ✕, tab menu, vim mode).</summary>
    public void RequestCloseTab(Tab tab) =>
        ConfirmClose(CloseScope.Tab, PanesOf(tab), () => { if (tabs.IndexOf(tab) >= 0) window.CloseTab(tab); });

    /// <summary>
    /// The window's close button or the compositor's close. True when the window may close now; otherwise a prompt is up
    /// and a confirmation closes it later (the session is saved as on any close).
    /// </summary>
    public bool RequestCloseWindow()
    {
        var panes = new List<Pane>();
        foreach (var tab in tabs.Items) panes.AddRange(PanesOf(tab));
        var scope = window.WindowCount <= 1 ? CloseScope.Quit : CloseScope.Window;
        return ConfirmClose(scope, panes, window.RequestClose);
    }

    /// <summary>Runs <paramref name="close"/> now (returns true) or asks first (returns false).</summary>
    public bool ConfirmClose(CloseScope scope, IReadOnlyList<Pane> panes, Action close)
    {
        var names = RunningPrograms(panes);
        if (names.Count == 0)
        {
            DismissPrompt();
            close();
            return true;
        }
        long now = Now;
        _closeConfirmed = close;
        window.InfoBar.Show(new InfoBarMessage
        {
            Key = CloseConfirmation.Key,
            Priority = CloseConfirmation.Priority,
            Tone = InfoBarTone.Danger,
            Icon = Icons.Close,
            Text = CloseConfirmation.Text(scope, names),
            Detail = scope == CloseScope.Restart ? "Enter restarts · Esc cancels" : "Enter closes · Esc cancels",
            Buttons =
            [
                new InfoBarButton(CloseConfirmation.ConfirmLabel(scope), Answer(true), primary: true),
                new InfoBarButton("Cancel", Answer(false)),
            ],
            Dismissed = Answer(false),
        }, window.Theme, now);
        window.Redraw();
        return false;
    }

    private Action Answer(bool close) => () =>
    {
        var action = _closeConfirmed;
        DismissPrompt();
        if (close) action?.Invoke();
    };

    private void DismissPrompt()
    {
        if (_closeConfirmed is null) return;
        _closeConfirmed = null;
        window.InfoBar.Remove(CloseConfirmation.Key, Now);
        window.Redraw();
    }

    /// <summary>Hook from OnKeyDown while the prompt is up: Enter / y close, Esc / n cancel, other keys are swallowed.</summary>
    public bool PromptKey(uint key)
    {
        if (_closeConfirmed is null) return false;
        if (key is Sdl.K_RETURN or Sdl.K_KP_ENTER or 'y') Answer(true)();
        else if (key is Sdl.K_ESCAPE or 'n') Answer(false)();
        return true;
    }

    /// <summary>What runs in these panes that a close would end (one name per pane; empty: nothing, close at once).</summary>
    private List<string> RunningPrograms(IReadOnlyList<Pane> panes)
    {
        var names = new List<string>();
        var settings = window.Settings;
        if (settings.WindowCloseConfirmation == Config.WindowCloseConfirmation.NeverPrompt) return names;
        foreach (var p in panes)
        {
            if (p.Exited) continue;
            string? fg = p.Pty.ForegroundProcessName;
            if (fg is not null && CloseConfirmation.IsSkipped(fg, settings.SkipCloseConfirmationForProcessesNamed)) continue; // skips the /proc argv read
            // agent_status off: Definition stays null, so identify the agent here.
            string? agent = (p.Agent.Definition ?? AgentCatalog.Identify(fg, p.Pty.ForegroundArguments()))?.Name;
            if (CloseConfirmation.RunningProgram(settings.WindowCloseConfirmation, fg, agent, settings.SkipCloseConfirmationForProcessesNamed) is { } name)
                names.Add(name);
        }
        return names;
    }

    /// <summary>A tab's panes (the active tab's live layout).</summary>
    private IReadOnlyList<Pane> PanesOf(Tab tab) => (ReferenceEquals(tab, tabs.Active) ? window.Layout : tab.Layout).LeafList;
}
