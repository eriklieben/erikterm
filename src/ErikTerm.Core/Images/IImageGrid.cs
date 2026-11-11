namespace ErikTerm.Core.Images;

/// <summary>
/// What the image protocols (<see cref="KittyGraphics"/>, <see cref="CellImages"/>) need from the terminal: the cursor,
/// the screen and cell size, and the grid writes that move the cursor past a picture.
/// </summary>
internal interface IImageGrid
{
    int Columns { get; }
    int Rows { get; }
    int CursorX { get; }
    int CursorY { get; }

    /// <summary>The cursor is past the last column, waiting to wrap.</summary>
    bool PendingWrap { get; }

    bool IsAlternateScreen { get; }
    long ScreenTopLine { get; }

    /// <summary>Size of a cell in pixels (at least 1).</summary>
    int CellWidth { get; }
    int CellHeight { get; }

    /// <summary>Kitty t=f / t=t / t=s transmissions may read files (<see cref="Terminal.AllowImageFiles"/>).</summary>
    bool AllowImageFiles { get; }

    /// <summary>
    /// Writes an image into the grid at the cursor as a box of <see cref="InlineImage.Columns"/>×<see cref="InlineImage.Rows"/>
    /// cells (clipped at the right edge). The picture fills the content rectangle (cell units) inside the box.
    /// </summary>
    void PlaceCellImage(in InlineImage placement, ImageCursor cursor);

    /// <summary>A pending wrap happens now: an image placed at the cursor starts on the next line.</summary>
    void WrapPendingCursor();

    /// <summary>Cursor to the cell after a placement's right edge on its last row, scrolling as needed.</summary>
    void MoveCursorPastPlacement(KittyPlacement p);

    /// <summary>Drops placements whose lines have left the scrollback.</summary>
    void PruneImages();
}

/// <summary>Where the cursor goes after an image is written into the grid.</summary>
internal enum ImageCursor : byte
{
    /// <summary>Doesn't move, and the image never scrolls the screen (iTerm2 doNotMoveCursor=1, kitty C=1).</summary>
    Stay,
    /// <summary>The cell after the image's right edge, on its last row (iTerm2, kitty).</summary>
    AfterLastRow,
    /// <summary>The start column of the line below the image (sixel).</summary>
    NextLine,
}
