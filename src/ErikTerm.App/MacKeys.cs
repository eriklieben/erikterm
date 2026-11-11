using ErikTerm.App.Native;

namespace ErikTerm.App;

/// <summary>
/// macOS Option keys. erikterm treats only the left Alt as Alt/Meta everywhere (the right one is AltGr on many
/// layouts: KeyNames, KeyEncoder), which on a Mac means left Option = Alt and right Option types é, ø, … The setting
/// macos_option_as_alt picks another split; this rewrites a key event's modifiers to match before anything reads them.
/// </summary>
internal static class MacKeys
{
    private const ushort KMOD_RALT = 0x0200;

    /// <summary>The modifiers as erikterm should see them under <paramref name="optionAsAlt"/> ("left", "right", "both", "none").</summary>
    public static ushort MapOption(ushort mod, string optionAsAlt)
    {
        bool left = (mod & Sdl.KMOD_LALT) != 0, right = (mod & KMOD_RALT) != 0;
        (bool alt, bool compose) = optionAsAlt switch
        {
            "right" => (right, left),
            "both" => (left || right, false),
            "none" => (false, left || right),
            _ => (left, right),
        };
        mod &= unchecked((ushort)~(Sdl.KMOD_LALT | KMOD_RALT));
        if (alt) mod |= Sdl.KMOD_LALT;
        if (compose) mod |= KMOD_RALT;
        return mod;
    }
}
