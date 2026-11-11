using System.Runtime.InteropServices;

namespace ErikTerm.App.Native;

// Version / capability queries used by the About screen. Kept apart from the core bindings: none of
// these are on a hot path, and each wrapper turns "not available" into null instead of throwing.

#pragma warning disable S6640 // binding: native version queries return C strings and out-pointers
internal static unsafe partial class Sdl
#pragma warning restore S6640
{
    /// <summary>SDL_DisplayMode, up to the fields we read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayMode
    {
        public uint DisplayId;
        public uint Format;
        public int W, H;
        public float PixelDensity;
        public float RefreshRate;
        public int RefreshRateNumerator, RefreshRateDenominator;
        public nint Internal;
    }

    [LibraryImport(Lib, EntryPoint = "SDL_GetVersion")]
    public static partial int GetVersion();

    [LibraryImport(Lib, EntryPoint = "SDL_GetRevision")]
    private static partial byte* GetRevisionPtr();

    [LibraryImport(Lib, EntryPoint = "SDL_GetRendererName")]
    private static partial byte* GetRendererNamePtr(nint renderer);

    [LibraryImport(Lib, EntryPoint = "SDL_GetCurrentVideoDriver")]
    private static partial byte* GetCurrentVideoDriverPtr();

    [LibraryImport(Lib, EntryPoint = "SDL_GetRenderVSync")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetRenderVSync(nint renderer, int* vsync);

    [LibraryImport(Lib, EntryPoint = "SDL_GetDisplayForWindow")]
    public static partial uint GetDisplayForWindow(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetDisplays")]
    private static partial uint* GetDisplaysPtr(int* count);

    [LibraryImport(Lib, EntryPoint = "SDL_GetDisplayName")]
    private static partial byte* GetDisplayNamePtr(uint displayId);

    public static string? GetDisplayName(uint displayId) => Marshal.PtrToStringUTF8((nint)GetDisplayNamePtr(displayId));

    /// <summary>The names of the connected displays; "" for one without a name. Main thread.</summary>
    public static string[] GetDisplayNames()
    {
        int count;
        uint* ids = GetDisplaysPtr(&count);
        if (ids == null) return [];
        try
        {
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = GetDisplayName(ids[i]) ?? "";
            return names;
        }
        finally
        {
            Free(ids);
        }
    }

    [LibraryImport(Lib, EntryPoint = "SDL_GetCurrentDisplayMode")]
    public static partial DisplayMode* GetCurrentDisplayMode(uint displayId);

    public static string? GetRevision() => Marshal.PtrToStringUTF8((nint)GetRevisionPtr());
    public static string? GetRendererName(nint renderer) => Marshal.PtrToStringUTF8((nint)GetRendererNamePtr(renderer));
    public static string? GetCurrentVideoDriver() => Marshal.PtrToStringUTF8((nint)GetCurrentVideoDriverPtr());
}

#pragma warning disable S6640 // binding: native version queries return C strings and out-pointers
internal static unsafe partial class FreeType
#pragma warning restore S6640
{
    [LibraryImport(Lib, EntryPoint = "FT_Library_Version")]
    private static partial void LibraryVersion(nint library, int* major, int* minor, int* patch);

    /// <summary>The loaded FreeType's version (a throwaway FT_Library is enough to ask).</summary>
    public static (int Major, int Minor, int Patch)? Version()
    {
        try
        {
            nint lib;
            if (InitFreeType(&lib) != 0) return null;
            int major, minor, patch;
            LibraryVersion(lib, &major, &minor, &patch);
            DoneFreeType(lib);
            return (major, minor, patch);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}

#pragma warning disable S6640 // binding: native version queries return C strings and out-pointers
internal static unsafe partial class HarfBuzz
#pragma warning restore S6640
{
    [LibraryImport(Lib, EntryPoint = "hb_version_string")]
    private static partial byte* VersionStringPtr();

    /// <summary>"11.2.1", or null when HarfBuzz isn't loaded.</summary>
    public static string? VersionString()
    {
        if (!Available) return null;
        try { return Marshal.PtrToStringUTF8((nint)VersionStringPtr()); }
        catch (EntryPointNotFoundException) { return null; }
    }
}
