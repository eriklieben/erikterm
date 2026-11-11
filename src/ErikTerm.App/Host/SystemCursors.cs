using ErikTerm.App.Native;

namespace ErikTerm.App.Host;

/// <summary>The mouse cursors every window uses: SDL has one current cursor for the process.</summary>
internal sealed class SystemCursors
{
    private nint _current;

    public nint Text { get; private set; }
    public nint Default { get; private set; }
    public nint Ew { get; private set; }
    public nint Ns { get; private set; }
    public nint Pointer { get; private set; }

    /// <summary>Once SDL is up.</summary>
    public void Create()
    {
        Text = Sdl.CreateSystemCursor(Sdl.SYSTEM_CURSOR_TEXT);
        Default = Sdl.CreateSystemCursor(Sdl.SYSTEM_CURSOR_DEFAULT);
        Ew = Sdl.CreateSystemCursor(Sdl.SYSTEM_CURSOR_EW_RESIZE);
        Ns = Sdl.CreateSystemCursor(Sdl.SYSTEM_CURSOR_NS_RESIZE);
        Pointer = Sdl.CreateSystemCursor(Sdl.SYSTEM_CURSOR_POINTER);
    }

    public void Set(nint cursor)
    {
        if (cursor == _current || cursor == 0) return;
        _current = cursor;
        Sdl.SetCursor(cursor);
    }
}
