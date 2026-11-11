using System.Reflection;
using System.Runtime.InteropServices;

namespace ErikTerm.App.Native;

/// <summary>
/// Maps the logical library names used in [LibraryImport] to per-OS file names. Linux uses the
/// system sonames; Windows loads them only from erikterm.exe's directory, by full path (where
/// scripts/publish-windows.ps1 puts the DLLs) — never from the current directory or PATH, where anyone who can drop
/// a file (a downloaded folder you start erikterm in) could plant an optional DLL like harfbuzz.dll. Works under
/// Native AOT: P/Invokes there are bound lazily at runtime and go through the registered resolver.
/// </summary>
internal static class NativeLibraries
{
    public const string Sdl3 = "SDL3";
    public const string FreeType = "freetype";
    public const string HarfBuzz = "harfbuzz";

    private static int _registered;

    /// <summary>Call once, before the first P/Invoke into SDL, FreeType or HarfBuzz.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    /// <summary>File names to try for a logical library, most specific first. Empty if not ours.</summary>
    internal static string[] Candidates(string logicalName, bool windows, bool macOS) => logicalName switch
    {
        Sdl3 when windows => ["SDL3.dll"],
        Sdl3 when macOS => ["libSDL3.0.dylib", "libSDL3.dylib"],
        Sdl3 => ["libSDL3.so.0", "libSDL3.so"],
        // freetype.dll: CMake / vcpkg builds; libfreetype-6.dll: MSYS2 / MinGW builds.
        FreeType when windows => ["freetype.dll", "libfreetype-6.dll"],
        FreeType when macOS => ["libfreetype.6.dylib", "libfreetype.dylib"],
        FreeType => ["libfreetype.so.6", "libfreetype.so"],
        // harfbuzz.dll: CMake / vcpkg builds; libharfbuzz-0.dll: MSYS2 / MinGW builds.
        HarfBuzz when windows => ["harfbuzz.dll", "libharfbuzz-0.dll"],
        HarfBuzz when macOS => ["libharfbuzz.0.dylib", "libharfbuzz.dylib"],
        HarfBuzz => ["libharfbuzz.so.0", "libharfbuzz.so"],
        _ => [],
    };

    /// <summary>Windows: the full paths a library may be loaded from (only the application directory). Empty if not ours.</summary>
    internal static string[] WindowsPaths(string logicalName, string appDirectory) =>
        [.. Candidates(logicalName, windows: true, macOS: false).Select(name => Path.Combine(appDirectory, name))];

    /// <summary>macOS: where Homebrew (or MacPorts) puts SDL3, FreeType and HarfBuzz, tried after the default search.</summary>
    internal static string[] MacLibraryDirectories() => ["/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib"];

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var candidates = Candidates(libraryName, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());
        if (candidates.Length == 0) return 0; // not ours: the runtime's default probing
        if (OperatingSystem.IsWindows()) return LoadFromAppDirectory(libraryName, assembly);
        foreach (var name in candidates)
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out nint handle)) return handle;
        return OperatingSystem.IsMacOS() ? LoadFromPackageManagerPrefixes(candidates) : 0; // not found: fall back to the runtime's default probing (and its error)
    }

    /// <summary>Full paths in the app directory only; the DLL's own dependencies are searched there and in System32.</summary>
    private static nint LoadFromAppDirectory(string libraryName, Assembly assembly)
    {
        foreach (var path in WindowsPaths(libraryName, AppContext.BaseDirectory))
            if (File.Exists(path) && NativeLibrary.TryLoad(path, assembly, DllImportSearchPath.SafeDirectories | DllImportSearchPath.UseDllDirectoryForDependencies, out nint local))
                return local;
        // Not found: fail here rather than return 0, which would let the runtime probe the current directory and PATH.
        throw new DllNotFoundException($"{libraryName} not found in {AppContext.BaseDirectory}");
    }

    /// <summary>dlopen's default search doesn't cover Homebrew's prefixes (Apple silicon: /opt/homebrew, Intel: /usr/local).</summary>
    private static nint LoadFromPackageManagerPrefixes(string[] candidates)
    {
        foreach (var dir in MacLibraryDirectories())
            foreach (var name in candidates)
                if (NativeLibrary.TryLoad(Path.Combine(dir, name), out nint brew)) return brew;
        return 0;
    }
}
