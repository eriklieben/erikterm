using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.App.Vim;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Extra ERIKTERM_DEBUG_SCRIPT steps: move:x,y · click:x,y · mclick:x,y · dclick:x,y (window pixels),
/// down:x,y · drag:x,y · up:x,y (left button held between them: tab drags, also outside the window),
/// hovertab:n · presstab:n (press on tab n, for drags) · menutab:n (right click on tab n) · skey:enter (Shift+key) · alt:c · feed:text
/// · ckey:tab (Ctrl+Tab, Ctrl stays held) · ctrl-up (Ctrl released) · ctrl-tap (a quick tap) · clickmore (the bar's "⌄ N more") · wheelbar:n (the wheel on the bar).
/// sleep:ms and the window steps are App's (App.RunScript); the window's own are TermWindow.RunScript.
/// </summary>
internal sealed class ScriptSteps(IWindowHost window, Lock gate, TabBar bar, WindowTabSwitcher<Tab> switcher, WindowVim<Pane> vim, ExitHoldFlow exitHold)
{
    public void Run(string kind, string arg, long now)
    {
        switch (kind)
        {
            case "move" or "drag": { var (x, y) = Point(arg); window.MouseMove(x, y, now); break; }
            case "click" or "mclick" or "dclick":
            {
                var (x, y) = Point(arg);
                byte button = kind == "mclick" ? Sdl.BUTTON_MIDDLE : Sdl.BUTTON_LEFT;
                window.MouseDown(button, (byte)(kind == "dclick" ? 2 : 1), x, y, now);
                window.MouseUp(button, x, y, now);
                break;
            }
            case "hovertab": HoverTab(arg, now); break;
            case "menutab": MenuTab(arg, now); break; // right click on tab n
            case "presstab": PressTab(arg, now); break;
            case "down": { var (x, y) = Point(arg); window.MouseMove(x, y, now); window.MouseDown(Sdl.BUTTON_LEFT, 1, x, y, now); break; }
            case "up": { var (x, y) = Point(arg); window.MouseUp(Sdl.BUTTON_LEFT, x, y, now); break; }
            case "skey": window.KeyDown(Key(arg), Sdl.KMOD_SHIFT, now); break;
            case "alt": window.KeyDown(Key(arg), Sdl.KMOD_LALT, now); break;
            case "ckey": window.KeyDown(Key(arg), Sdl.KMOD_CTRL, now); break; // Ctrl+key
            case "ctrl-tap": window.KeyDown(Sdl.K_TAB, Sdl.KMOD_CTRL, now); switcher.ModifierUp(0); break; // Ctrl+Tab pressed and released at once: a quick tap
            case "ctrl-double": DoubleCtrl(now); break; // Ctrl, Ctrl
            case "ctrl-up": switcher.ModifierUp(0); break; // releases a held Ctrl (the Ctrl+Tab list; see ckey:tab)
            case "clickmore": ClickMore(now); break; // the bar's "⌄ N more"
            case "wheelbar": bar.HandleWheel(ActionDispatcher.ParseInt(arg, 1), 0); break; // the wheel on the tab bar: scrolls an overflowing one
            case "cskey": window.KeyDown(Key(arg), (ushort)(Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT), now); break; // Ctrl+Shift+key
            case "vim": vim.Script(arg); break; // vim:KEYS in vim notation (Vim/WindowVim.cs)
            case "feed": lock (gate) window.Focused.Term.Feed(System.Text.Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Unescape(arg))); break;
            default:
                if (!window.RunDropScriptStep(kind, arg, now) && !window.RunTeammateScriptStep(kind, arg, now) && !exitHold.RunScriptStep(kind, arg))
                    window.RunShellScriptStep(kind, arg, now); // drag and drop (WindowFeatures/WindowDrop.cs), pills (Teammates/WindowTeammates.cs), exit:N (ExitHoldFlow), links/shell steps (WindowFeatures/WindowLinks.cs)
                break;
        }
    }

    private static (float X, float Y) Point(string arg)
    {
        var parts = arg.Split(',');
        return parts.Length == 2 && float.TryParse(parts[0], out float px) && float.TryParse(parts[1], out float py) ? (px, py) : (0, 0);
    }

    private void HoverTab(string arg, long now)
    {
        if (bar.CenterOf(ActionDispatcher.ParseInt(arg, 0)) is var (x, y)) window.MouseMove(x, y, now);
    }

    private void MenuTab(string arg, long now)
    {
        if (bar.CenterOf(ActionDispatcher.ParseInt(arg, 0)) is not var (x, y)) return;
        window.MouseMove(x, y, now);
        window.MouseDown(Sdl.BUTTON_RIGHT, 1, x, y, now);
        window.MouseUp(Sdl.BUTTON_RIGHT, x, y, now);
    }

    private void PressTab(string arg, long now)
    {
        if (bar.CenterOf(ActionDispatcher.ParseInt(arg, 0)) is not var (x, y)) return;
        window.MouseMove(x, y, now);
        window.MouseDown(Sdl.BUTTON_LEFT, 1, x, y, now);
    }

    private void DoubleCtrl(long now)
    {
        for (int t = 0; t < 2; t++)
        {
            window.KeyDown(0x400000E0, Sdl.KMOD_CTRL, now);
            switcher.DoubleCtrlKeyUp(0x400000E0, now);
        }
    }

    private void ClickMore(long now)
    {
        if (bar.MoreCenter is not var (x, y)) return;
        window.MouseMove(x, y, now);
        window.MouseDown(Sdl.BUTTON_LEFT, 1, x, y, now);
        window.MouseUp(Sdl.BUTTON_LEFT, x, y, now);
    }

    /// <summary>A key by its script name: down, up, enter, escape, tab, or a single character.</summary>
    public static uint Key(string name) => name switch
    {
        "down" => Sdl.K_DOWN, "up" => Sdl.K_UP, "enter" => Sdl.K_RETURN, "escape" => Sdl.K_ESCAPE, "tab" => Sdl.K_TAB,
        _ => name.Length == 1 ? name[0] : 0u,
    };
}
