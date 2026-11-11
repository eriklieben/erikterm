using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Teammates;

/// <summary>What teammate handling reads and sets on a pane: <see cref="Pane"/> implements it, tests use a fake.</summary>
internal interface ITeammatePane<TPane> : IPaneView where TPane : class
{
    Teammate? Teammate { get; set; }
    /// <summary>As a lead: its teammates in this tab, in layout order (rebuilt by <see cref="WindowTeammates{TPane}.Sync"/>).</summary>
    List<TPane> Teammates { get; }
    /// <summary>Pixels at the bottom of the pane's rectangle given to the teammate strip.</summary>
    int StripPx { get; }
    AgentPane Agent { get; }
    RectI Target { get; set; }
    ref RectTween Rect { get; }
    ref Tween Focus { get; }
    ref Tween Appear { get; }
    /// <summary>Made through the tmux emulation, so respawn-pane may park it.</summary>
    bool CreatedByTmux { get; set; }
    string? TmuxTitle { get; }
    string? StartDirectory(string? defaultCwd);
}

/// <summary>What <see cref="WindowTeammates{TPane}"/> needs from its window: TermWindow implements it, tests use a fake.</summary>
internal interface ITeammateWindow<TPane> : IWindowPanes<TPane>, IWindowChrome where TPane : class, ITeammatePane<TPane>
{
    Settings Settings { get; }
    /// <summary>The scheme a teammate's tmux color resolves against.</summary>
    ColorScheme Scheme { get; }
    /// <summary>The animation duration, in ms.</summary>
    float Anim { get; }
    int LayoutGap { get; }
    /// <summary>A tab's focused pane.</summary>
    TPane TabFocused(int tabIndex);
    void SetTabFocused(int tabIndex, TPane pane);
    /// <summary>Starts a program for a tmux split; throws when it can't.</summary>
    TPane SpawnTeammate(string[]? argv, string? cwd, RectI area);
    /// <summary>Applies the change in the other tabs' layouts.</summary>
    void ResizeBackgroundTabs();
    /// <summary>Sizes a pane's terminal and PTY for the rectangle, leaving out the strip. True when it changed.</summary>
    bool SizeTerminal(TPane pane, RectI r, int strip);
    /// <summary>A parked pane's terminal was resized: the activity tracker (background tab) or reflow bookkeeping (active tab) hears of it.</summary>
    void ParkedResized(TPane pane, bool background);
    /// <summary>Clears the zoom without laying out; the caller does that next.</summary>
    void DropZoom(long now);
    void PointerCursor();
    void DefaultCursor();
    string[] ShortcutFor(ActionSpec action);
}
