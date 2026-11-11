using ErikTerm.Core;

namespace ErikTerm.App.Panes;

/// <summary>
/// Reflow (<see cref="Terminal.Resize"/> with a new width) renumbers absolute lines, so per-pane UI state that holds
/// line/column positions points at other text afterwards. This drops it once per reflow: the selection, the hovered
/// link, the gutter's command block and the prompt flash. (Search re-runs by itself — TerminalSearch watches
/// <see cref="Terminal.ReflowCount"/> — and App clears the search bar's current match and quick select.)
/// </summary>
internal static class ReflowInvalidation
{
    /// <summary>True when the terminal reflowed since <paramref name="seenReflowCount"/>; the stale state is then cleared.</summary>
    public static bool Check(Terminal t, ref int seenReflowCount, ref Selection? selection, PaneLinks links)
    {
        int count = t.ReflowCount;
        if (count == seenReflowCount) return false;
        seenReflowCount = count;
        selection = null;
        links.Hovered.Clear();
        links.HoveredUri = null;
        links.HoveredExplicit = false;
        links.Armed = false;
        links.HoverFade.Snap(0);
        links.GutterStart = links.GutterEnd = links.GutterPrompt = -1;
        links.GutterFade.Snap(0);
        links.FlashLine = -1;
        links.Flash.Snap(0);
        return true;
    }
}
