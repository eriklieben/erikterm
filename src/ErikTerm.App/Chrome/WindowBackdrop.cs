using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.App.Windowing;
using ErikTerm.Config;

namespace ErikTerm.App.Chrome;

/// <summary>
/// The blur behind a see-through window, and Toggle Transparency. On Linux and macOS the blur belongs to the
/// compositor and an application has no say in it, which is why <c>window_background_opacity</c> has always
/// documented blur as "up to the compositor". Windows is the exception: DWM will blur behind a window, but only if it
/// is asked, so on Windows erikterm has to ask (Native/WindowsDwm.cs).
/// <para>
/// Only the pixels we leave translucent show it, so the backdrop does nothing at full opacity — which is also
/// why <see cref="WindowBlurMode.Auto"/> turns it off there rather than paying for a blur nobody can see.
/// </para>
/// </summary>
internal sealed class WindowBackdrop(IWindowSurface window)
{
    private bool? _opaqueOverride;

    /// <summary>Toggle Transparency made this window opaque (the frame then presents at full opacity).</summary>
    public bool Opaque => _opaqueOverride == true;

    /// <summary>Toggle Transparency: flips the window opaque or back; true when it is opaque now.</summary>
    public bool ToggleOpaque()
    {
        _opaqueOverride = _opaqueOverride == true ? null : true;
        Apply(); // an opaque window has nothing to blur
        return Opaque;
    }

    /// <summary>Applied at startup, on config reload and when Toggle Transparency flips the window opaque.</summary>
    public void Apply()
    {
        if (Sdl.Hwnd(window.SdlWindow) is var hwnd && hwnd == 0) return;
        // Before the backdrop: this is the palette it tints its blur with.
        WindowsDwm.SetDarkMode(hwnd, Colors.Luminance(window.Theme.Background) < 0.5f);
        WindowsDwm.SetBackdrop(hwnd, Resolve());
    }

    private WindowsDwm.Backdrop Resolve() => window.Settings.WindowBlur switch
    {
        WindowBlurMode.None => WindowsDwm.Backdrop.None,
        WindowBlurMode.Acrylic => WindowsDwm.Backdrop.Acrylic,
        WindowBlurMode.Mica => WindowsDwm.Backdrop.Mica,
        _ => IsTranslucent ? WindowsDwm.Backdrop.Acrylic : WindowsDwm.Backdrop.None,
    };

    /// <summary>
    /// Hook from the window's focus change. Windows draws acrylic only for the active window and fills an inactive one
    /// with a flat gray instead, which hides everything a translucent window was showing. While it is in the
    /// background the window stays see-through without the blur; the blur comes back with the focus.
    /// </summary>
    public void UpdateForFocus(bool focused)
    {
        if (!OperatingSystem.IsWindows() || Resolve() != WindowsDwm.Backdrop.Acrylic) return;
        if (Sdl.Hwnd(window.SdlWindow) is var hwnd && hwnd == 0) return;
        WindowsDwm.SetBackdrop(hwnd, focused ? WindowsDwm.Backdrop.Acrylic : WindowsDwm.Backdrop.None);
    }

    /// <summary>Whether anything would show through: the configured opacity, unless Toggle Transparency is on.</summary>
    private bool IsTranslucent => _opaqueOverride != true && window.Settings.WindowBackgroundOpacity < 0.999;
}
