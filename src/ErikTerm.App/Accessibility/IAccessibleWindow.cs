using ErikTerm.App.Panes;

namespace ErikTerm.App.Accessibility;

/// <summary>
/// What <see cref="WindowAccessibility"/> needs from its window: TermWindow implements it. Read on the main thread
/// only; UI Automation's threads see a published copy (<see cref="WindowView"/>).
/// </summary>
internal interface IAccessibleWindow
{
    /// <summary>SDL's window, for its HWND.</summary>
    nint SdlWindow { get; }

    /// <summary>The panes on screen, in layout order.</summary>
    IReadOnlyList<Pane> VisiblePanes { get; }

    /// <summary>The pane keyboard input goes to.</summary>
    Pane FocusedPane { get; }

    bool IsWindowFocused { get; }

    /// <summary>The active tab's title (custom or automatic), or null before there are tabs.</summary>
    string? ActiveTabTitle { get; }

    /// <summary>One cell, in window pixels.</summary>
    (int Width, int Height) CellSize { get; }

    /// <summary>Window pixels per window point.</summary>
    float Density { get; }

    void FocusPane(Pane pane);

    /// <summary>Draws a frame: a selection or the scroll position changed.</summary>
    void Redraw();
}
