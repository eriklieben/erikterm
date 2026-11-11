using System.Runtime.InteropServices;
using ErikTerm.Core.Images;

namespace ErikTerm.App.Native;

/// <summary>
/// The window icon: assets/icon/png/erikterm-128.png, embedded by build/AppIcon.props and decoded once with the
/// in-house PNG decoder. X11 (and XWayland) shows it in task bars and window switchers (_NET_WM_ICON). On Wayland
/// SDL sends it with xdg-toplevel-icon-v1 where the compositor has that; compositors without it, and most docks
/// anyway, find the icon through the app id instead: "erikterm" → erikterm.desktop → Icon=erikterm
/// (contrib/linux, scripts/install-desktop.sh). Windows is left alone: SDL gives its window class the exe's icon
/// resource (ApplicationIcon, assets/icon/erikterm.ico), which has hand-tuned small sizes.
/// </summary>
#pragma warning disable S6640 // SDL reads the icon's pinned pixels through a pointer
internal static unsafe class WindowIcon
#pragma warning restore S6640
{
    /// <summary>The manifest resource name (LogicalName in build/AppIcon.props).</summary>
    internal const string ResourceName = "erikterm-icon.png";

    private static readonly Lazy<DecodedImage?> Image = new(Load);

    /// <summary>Decodes the embedded icon; null when it is missing or broken (no icon beats no window).</summary>
    internal static DecodedImage? Load()
    {
        try
        {
            using var stream = typeof(WindowIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Png.Decode(bytes);
        }
        catch (Exception ex) when (ex is ImageDecodeException or IOException)
        {
            return null;
        }
    }

    /// <summary>Hook from TermWindow.Open, right after the window is created.</summary>
    public static void Apply(nint window)
    {
        if (window == 0 || OperatingSystem.IsWindows() || Image.Value is not { } image) return;
        fixed (byte* pixels = image.Rgba)
        {
            nint surface = Sdl.CreateSurfaceFrom(image.Width, image.Height, Sdl.PIXELFORMAT_RGBA32, pixels, image.Width * 4);
            if (surface == 0) return;
            Sdl.SetWindowIcon(window, surface); // false on Wayland without xdg-toplevel-icon: the .desktop icon is used
            Sdl.DestroySurface(surface);        // SDL keeps its own converted copy
        }
    }
}

#pragma warning disable S6640 // binding: SDL's surface API takes a pixel pointer
internal static unsafe partial class Sdl
#pragma warning restore S6640
{
    /// <summary>A surface over caller-owned pixels (not copied: keep them pinned while the surface exists).</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_CreateSurfaceFrom")]
    public static partial nint CreateSurfaceFrom(int width, int height, uint format, void* pixels, int pitch);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowIcon")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowIcon(nint window, nint icon);
}
