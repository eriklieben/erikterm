using ErikTerm.App.Panes;

namespace ErikTerm.App.Rendering;

/// <summary>
/// What the layers over a pane's grid (images, the cursors) are drawn with this frame: the grid's top-left at
/// (<see cref="Ox"/>, <see cref="Oy"/>), the pane's inactive-pane dimming as the grid used it, its fade-in opacity
/// <see cref="Appear"/>, and the renderer's glyph drawing for text redrawn under a cursor. Built once per pane per frame
/// by <see cref="PaneRenderer"/> and passed by reference.
/// </summary>
internal readonly record struct PaneFrame(DrawList Dl, Pane Pane, float Ox, float Oy, ColorTransformCache Dim, GridText Text, float Appear, long Now);
