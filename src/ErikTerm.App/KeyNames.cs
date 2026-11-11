using ErikTerm.App.Native;
using ErikTerm.Config;

namespace ErikTerm.App;

/// <summary>Maps SDL key events onto config <see cref="KeyChord"/>s. SDL is set to report unmodified keycodes.</summary>
internal static class KeyNames
{
    public static KeyChord? Chord(uint key, ushort mod)
    {
        string? name = key switch
        {
            Sdl.K_RETURN or Sdl.K_KP_ENTER => "enter",
            Sdl.K_TAB => "tab",
            Sdl.K_ESCAPE => "escape",
            Sdl.K_SPACE => "space",
            Sdl.K_BACKSPACE => "backspace",
            Sdl.K_DELETE => "delete",
            Sdl.K_INSERT => "insert",
            Sdl.K_HOME => "home",
            Sdl.K_END => "end",
            Sdl.K_PAGEUP => "pageup",
            Sdl.K_PAGEDOWN => "pagedown",
            Sdl.K_UP => "up",
            Sdl.K_DOWN => "down",
            Sdl.K_LEFT => "left",
            Sdl.K_RIGHT => "right",
            >= Sdl.K_F1 and <= Sdl.K_F12 => "f" + (key - Sdl.K_F1 + 1),
            > 0x20 and < 0x7F => char.ToLowerInvariant((char)key).ToString(),
            _ => null,
        };
        return name is null ? null : new KeyChord(Mods(mod), name);
    }

    public static KeyMods Mods(ushort mod)
    {
        var m = KeyMods.None;
        if ((mod & Sdl.KMOD_CTRL) != 0) m |= KeyMods.Ctrl;
        if ((mod & Sdl.KMOD_SHIFT) != 0) m |= KeyMods.Shift;
        if ((mod & Sdl.KMOD_LALT) != 0) m |= KeyMods.Alt; // right Alt is AltGr on many layouts
        if ((mod & Sdl.KMOD_GUI) != 0) m |= KeyMods.Super;
        return m;
    }
}
