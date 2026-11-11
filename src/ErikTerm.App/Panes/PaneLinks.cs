using ErikTerm.Ui;

namespace ErikTerm.App.Panes;

/// <summary>Cells [StartCol, EndCol] (inclusive) of one line, in absolute line coordinates.</summary>
internal readonly record struct LineSegment(long Line, int StartCol, int EndCol);

/// <summary>Per-pane link and shell-integration UI state (main thread only).</summary>
internal sealed class PaneLinks
{
    /// <summary>Cells of the link under the pointer (empty when none).</summary>
    public readonly List<LineSegment> Hovered = [];
    public string? HoveredUri;
    /// <summary>The hovered link came from OSC 8 (its text may differ from its target).</summary>
    public bool HoveredExplicit;
    /// <summary>The hovered link is a file reference, commit or issue (Ctrl+click opens it in an editor / git / gh).</summary>
    public Integrations.SmartLink? HoveredSmart;
    /// <summary>Ctrl is held: a click opens the link.</summary>
    public bool Armed;
    public Tween HoverFade = new(0);

    /// <summary>Command block under the pointer in the gutter: output lines to outline.</summary>
    public long GutterStart = -1, GutterEnd = -1, GutterPrompt = -1;
    public Tween GutterFade = new(0);

    /// <summary>Prompt line just jumped to with ScrollToPrompt; flashes briefly.</summary>
    public long FlashLine = -1;
    public Tween Flash = new(0);

    public bool IsAnimating(long now) => HoverFade.IsAnimating(now) || GutterFade.IsAnimating(now) || Flash.IsAnimating(now);
}
