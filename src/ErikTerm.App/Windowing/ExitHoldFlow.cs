using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Pty;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// exit_behavior (wezterm's setting and names) and Restart Pane. When a pane's program exits, Close (the default) closes
/// the pane as before; Hold keeps it, its output intact, with an exit bar along its bottom — "Process exited with code 3
/// [Restart] [Close]", Enter restarts and Esc closes while it's the focused pane; CloseOnCleanExit closes on status 0
/// and holds otherwise. The bar takes rows from the grid (like the teammate strip) so it never covers the program's
/// last output. tmux emulation panes, teammates, erikterm's own tool splits and kill-pane always close
/// (<see cref="ExitHold"/>). Restart re-runs the pane's command (the directory its shell last reported, else where it
/// started) in the same pane: same id, place and output, with the old program's modes reset.
/// </summary>
internal sealed class ExitHoldFlow(IWindowHost window, Lock gate, CloseConfirmFlow closeConfirm)
{
    public const string MessageKey = "exit";
    private bool _overExitBar;

    private static long Now => Environment.TickCount64;

    // ---- Holding ------------------------------------------------------------------------------

    /// <summary>
    /// Hook from ReapExitedPanes / ReapBackgroundTabs for a pane whose program exited: true when it stays (held, now or
    /// already), false when it should close.
    /// </summary>
    public bool HoldIfWanted(Pane pane)
    {
        if (window.ContinueWorktreeLaunch(pane)) return true; // setup, agent, shell (Worktrees/WindowWorktrees.cs)
        if (window.ContinueRepoPlan(pane)) return true; // visible git steps (WindowFeatures/WindowRepoPlans.cs)
        if (pane.Held) return true;
        bool mustClose = pane.CloseOnExit || pane.CreatedByTmux || pane.Teammate is not null;
        if (!ExitHold.ShouldHold(window.Settings.ExitBehavior, mustClose, pane.ExitCode)) return false;
        pane.Held = true;
        lock (gate) pane.Term.Feed("\e[?25l"u8); // no cursor: nothing reads input there now (a restart shows it again)
        ShowBar(pane, Now);
        window.ApplyLayout(animate: true); // the grid gives the bar its rows (SizeTerminal); background tabs too
        return true;
    }

    /// <summary>Height of a held pane's exit bar (0 when not held, or when the pane is too small to give up rows).</summary>
    public int BarHeightFor(Pane pane, RectI r, int strip)
    {
        if (!pane.Held) return 0;
        var theme = window.Theme;
        int h = (int)MathF.Round(InfoBar.Height(theme) + theme.S(8));
        return h * 3 > r.H - strip ? 0 : h; // a tiny pane keeps its rows; the bar covers its bottom instead
    }

    /// <summary>Builds the bar's message, with buttons that act through this window (the pane may move between windows).</summary>
    private void ShowBar(Pane pane, long now)
    {
        pane.ExitBar ??= new InfoBar();
        pane.ExitBarOwner = this;
        int? code = pane.ExitCode;
        pane.ExitBar.Show(new InfoBarMessage
        {
            Key = MessageKey,
            Tone = code == 0 ? InfoBarTone.Info : InfoBarTone.Danger,
            Icon = code == 0 ? Icons.Info : Icons.Error,
            Text = ExitHold.Message(ExitHold.ProgramName(pane.LaunchArgv), code),
            Detail = "Enter to restart · Esc to close",
            Buttons =
            [
                new InfoBarButton("Restart", () => RestartPane(pane), primary: true),
                new InfoBarButton("Close", () => window.ClosePane(pane)),
            ],
            Dismissible = false,
        }, window.Theme, now);
    }

    /// <summary>A held pane that came from another window (a moved tab): its bar's buttons must act here.</summary>
    private void AdoptBar(Pane pane)
    {
        if (pane.Held && !ReferenceEquals(pane.ExitBarOwner, this)) ShowBar(pane, Now);
    }

    // ---- Restart ------------------------------------------------------------------------------

    /// <summary>RestartPane (palette, keys): asks first when something other than a shell still runs there.</summary>
    public void RequestRestartPane(Pane pane)
    {
        if (pane.Teammate is not null)
        {
            window.Toast(ToastKind.Info, "Teammates restart through Claude Code", "This pane belongs to an agent team");
            return;
        }
        if (pane.Held || pane.Exited) RestartPane(pane);
        else closeConfirm.ConfirmClose(CloseScope.Restart, [pane], () => RestartPane(pane)); // Windowing/CloseConfirmFlow.cs
    }

    /// <summary>Runs the pane's command again in the same pane (the old program, if still running, is hung up).</summary>
    private void RestartPane(Pane pane)
    {
        if (!window.Layout.Contains(pane) && window.FindPane(pane.Id) is null) return; // closed meanwhile
        pane.WorktreeLaunch = null; // the user's restart replaces what a worktree launch still had to start
        pane.RepoPlan = null;       // likewise a plan: Restart Pane is a shell in that directory
        var settings = window.Settings;
        string[] argv = pane.LaunchArgv.Length > 0 ? pane.LaunchArgv : settings.DefaultProg?.ToArray() ?? Pty.Pty.DefaultShellCommand();
        RespawnPane(pane, argv, ExitHold.RestartDirectory(pane.WorkingDirectory, pane.LaunchCwd, settings.DefaultCwd));
    }

    /// <summary>Runs <paramref name="argv"/> as the pane's program, keeping its place and output; false (and a toast) when it can't be started.</summary>
    public bool RespawnPane(Pane pane, string[] argv, string? cwd)
    {
        var env = window.PaneEnvironment(pane.Id, cwd); // the same ERIKTERM_PANE / TMUX_PANE as before
        // The default shell started again (Restart Pane, a worktree launch's shell step); agents and tools are started as they are.
        string[] spawnArgv = argv.AsSpan().SequenceEqual(window.Settings.DefaultProg?.ToArray() ?? Pty.Pty.DefaultShellCommand()) ? window.WithShellIntegration(argv, env) : argv;

        bool wasHeld = pane.Held;
        pane.Held = false;
        window.ApplyLayout(animate: false); // the grid takes back the exit bar's rows before the program sees its size
        int cols, rows;
        lock (gate) (cols, rows) = (pane.Term.Columns, pane.Term.Rows);
        Pty.Pty pty;
        try
        {
            pty = Pty.Pty.Spawn(new PtyOptions
            {
                Program = spawnArgv[0], Arguments = spawnArgv, Environment = env, WorkingDirectory = cwd, Columns = cols, Rows = rows,
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            if (wasHeld) { pane.Held = true; window.ApplyLayout(animate: false); }
            window.Toast(ToastKind.Error, "Couldn't restart the pane", ex.Message);
            return false;
        }
        pty.Resize(cols, rows, pane.Target.W, pane.Target.H - pane.BottomPx);
        lock (gate) pane.Term.TerminalName = env.TryGetValue("TERM", out var termName) ? termName : Core.TermCapabilities.BaseEntry;
        pane.Respawn(pty, gate, window.Wake, keepOutput: true); // Panes/Pane.cs
        pane.LaunchCwd = cwd;
        pane.ExitBar?.Remove(MessageKey, Now);
        pane.Agent.Pgid = -2; // identify the new program (Agents/WindowAgents.cs)
        pane.Cursor.Touch(Now);
        window.Redraw();
        return true;
    }

    // ---- Input --------------------------------------------------------------------------------

    /// <summary>Hook from OnKeyDown: on the focused held pane, Enter restarts and Esc closes (plain keys only).</summary>
    public bool KeyDown(uint key, ushort mod)
    {
        var focused = window.Focused;
        if (!focused.Held || (mod & (Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT | Sdl.KMOD_LALT | Sdl.KMOD_GUI)) != 0) return false;
        if (window.RepoPlanKeyDown(focused, key)) return true; // WindowFeatures/WindowRepoPlans.cs
        if (key is Sdl.K_RETURN or Sdl.K_KP_ENTER) { RestartPane(focused); return true; }
        if (key == Sdl.K_ESCAPE) { window.ClosePane(focused); return true; }
        return false;
    }

    /// <summary>Hook from OnMouseMove: hover on the exit bars' buttons.</summary>
    public bool MouseMove(float x, float y)
    {
        if (window.PointerBusy) return false;
        bool over = false, control = false, changed = false;
        foreach (var pane in window.VisiblePanes())
        {
            if (pane.ExitBar is not { } bar || !pane.Held) continue;
            if (bar.HandleMouseMove(x, y, out bool c)) { over = true; control |= bar.OverControl; }
            changed |= c;
        }
        if (over) window.SetCursor(control ? window.CursorPointer : window.CursorDefault);
        if (changed || over != _overExitBar) window.Redraw();
        _overExitBar = over;
        return over;
    }

    /// <summary>Hook from OnMouseDown: a click on an exit bar (its buttons, or the bar itself) doesn't reach the grid.</summary>
    public bool MouseDown(float x, float y)
    {
        foreach (var pane in window.VisiblePanes())
        {
            if (pane.ExitBar is not { } bar || !pane.Held) continue;
            AdoptBar(pane);
            if (!bar.HandleMouseDown(x, y)) continue;
            window.Focus(pane);
            return true;
        }
        return false;
    }

    // ---- Drawing ------------------------------------------------------------------------------

    /// <summary>Hook from DrawOverlays, after the teammate strips: the bars of held panes on screen.</summary>
    public void Draw(DrawList dl, Theme theme, List<Pane> visible, long now)
    {
        foreach (var pane in visible)
        {
            if (pane.ExitBar is not { } bar || !bar.IsVisible(now)) continue;
            AdoptBar(pane);
            var r = pane.Rect.Value(now);
            float h = InfoBar.Height(theme), inset = theme.S(4);
            float bottom = r.Bottom - pane.StripPx;
            bar.Draw(dl, theme, new RectF(r.X + inset, MathF.Round(bottom - h - inset), MathF.Max(0, r.W - 2 * inset), h), fromTop: false, now);
        }
    }

    /// <summary>Hook from IsAnimating.</summary>
    public static bool IsAnimating(LayoutTree<Pane> layout, long now)
    {
        foreach (var p in layout.LeafList)
            if (p.ExitBar is { } bar && bar.IsAnimating(now)) return true;
        return false;
    }

    // ---- Palette & debug ------------------------------------------------------------------------

    public void AddCommands(List<Command> list)
    {
        var focused = window.Focused;
        list.Add(new Command
        {
            Id = "RestartPane", Category = "Pane", Title = focused.Held ? "Restart Pane (its program exited)" : "Restart Pane",
            Icon = Icons.Refresh, Action = new ActionSpec("RestartPane", null), IsEnabled = () => window.Focused.Teammate is null,
            Shortcut = window.ShortcutFor(new ActionSpec("RestartPane", null)),
        });
    }

    /// <summary>Debug script step exit:N — the focused pane's program "exits" with status N (scenes show the exit bar).</summary>
    public bool RunScriptStep(string kind, string arg)
    {
        if (kind != "exit") return false;
        window.Focused.SimulateExit(int.TryParse(arg, out int code) ? code : 1);
        return true;
    }
}
