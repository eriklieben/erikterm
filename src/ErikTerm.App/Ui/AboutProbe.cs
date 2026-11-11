using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ErikTerm.App.Native;
using ErikTerm.App.Rendering;

namespace ErikTerm.App.Ui;

/// <summary>
/// Gathers what the About screen and <c>erikterm --version</c> show (<see cref="AboutInfo"/>): build, runtime, SDL,
/// font and process facts, from native queries, the assembly's stamps and /proc.
/// </summary>
internal static class AboutProbe
{
    /// <summary>
    /// Gathers build, runtime, SDL, font and process facts. Runs once when the About screen opens
    /// (a few native queries and a read of /proc/self/maps), never per frame.
    /// </summary>
#pragma warning disable S6640 // Native interop: SDL and FreeType queries fill caller-provided buffers.
    public static unsafe AboutInfo Collect(nint window, nint renderer, FontSet fonts, GlyphAtlas atlas)
#pragma warning restore S6640
    {
        var build = ReadBuild();

        int vsync = 0;
        bool? hasVsync = renderer != 0 && Sdl.GetRenderVSync(renderer, &vsync) ? vsync != 0 : null;
        int displayW = 0, displayH = 0;
        float refresh = 0;
        uint display = window != 0 ? Sdl.GetDisplayForWindow(window) : 0;
        if (display != 0 && Sdl.GetCurrentDisplayMode(display) is var mode && mode != null)
        {
            displayW = mode->W;
            displayH = mode->H;
            refresh = mode->RefreshRate;
        }

        long workingSet = 0;
        TimeSpan uptime = TimeSpan.Zero;
        try
        {
            using var self = Process.GetCurrentProcess();
            workingSet = self.WorkingSet64;
            uptime = DateTime.UtcNow - self.StartTime.ToUniversalTime();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { /* the About screen shows zero for these */ }

        var ft = FreeType.Version();

        return new AboutInfo
        {
            Version = build.Version,
            Commit = build.Commit,
            Dirty = build.Dirty,
            BuildDate = build.BuildDate,
            NativeAot = IsNativeAot(),
            Runtime = RuntimeInformation.FrameworkDescription,
            Os = OsName(),
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            WorkingSetBytes = workingSet,
            Uptime = uptime,
            ProcessId = Environment.ProcessId,
            SdlVersion = AboutFormat.SdlVersion(Sdl.GetVersion()),
            VideoDriver = Sdl.GetCurrentVideoDriver(),
            Renderer = renderer != 0 ? Sdl.GetRendererName(renderer) : null,
            VSync = hasVsync,
            DisplayWidth = displayW,
            DisplayHeight = displayH,
            RefreshHz = refresh,
            PixelDensity = window != 0 ? Sdl.GetWindowPixelDensity(window) : 1,
            DisplayScale = window != 0 ? Sdl.GetWindowDisplayScale(window) : 1,
            FreeTypeVersion = ft is { } v ? $"{v.Major}.{v.Minor}.{v.Patch}" : null,
            FreeTypeYear = ft is { } y ? AboutFormat.FreeTypeYear(y.Major, y.Minor, y.Patch, DateTime.UtcNow.Year) : DateTime.UtcNow.Year,
            HarfBuzzVersion = HarfBuzz.VersionString(),
            FontPath = fonts.PrimaryPath,
            FontPixels = fonts.PixelSize,
            CellWidth = fonts.CellWidth,
            CellHeight = fonts.CellHeight,
            LineHeight = fonts.LineHeight,
            FallbackFonts = fonts.FallbackCount,
            AtlasEntries = atlas.Entries,
            AtlasFill = atlas.Fill,
            UnicodeVersion = Core.CharWidth.UnicodeVersion,
            Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Libraries = LoadedLibraries(),
        };
    }

    /// <summary>`erikterm --version` / `erikterm cli --version`.</summary>
    public static string VersionText()
    {
        var (version, commit, dirty, date) = ReadBuild();
        return AboutFormat.VersionText(version, commit, dirty, date);
    }

    private static (string Version, string? Commit, bool Dirty, string? BuildDate) ReadBuild()
    {
        var asm = typeof(AboutInfo).Assembly;
        var meta = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var a in asm.GetCustomAttributes<AssemblyMetadataAttribute>()) meta[a.Key] = a.Value;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString(3);
        return AboutFormat.ParseBuild(info, meta);
    }

    /// <summary>
    /// PublishAot also flips RuntimeFeature.IsDynamicCodeSupported under the JIT, so detect AOT the way
    /// the benchmark does: a JIT app has its managed assembly next to the executable, a native one doesn't.
    /// </summary>
    private static bool IsNativeAot() =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, typeof(AboutInfo).Assembly.GetName().Name + ".dll"));

    /// <summary>"Arch Linux · Linux 7.2.11-arch1-1" (.NET reports the distro name on Linux, not the kernel).</summary>
    private static string OsName()
    {
        string description = RuntimeInformation.OSDescription.Trim();
        if (!OperatingSystem.IsLinux()) return description;
        try
        {
            string kernel = File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
            if (kernel.Length > 0 && !description.Contains(kernel, StringComparison.Ordinal)) return $"{description} · Linux {kernel}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the OS description alone is enough */ }
        return description;
    }

    /// <summary>Shared libraries in the process, in load order (Linux: /proc/self/maps; Windows: the module list).</summary>
    private static List<string> LoadedLibraries()
    {
        var result = new List<string>();
        try
        {
            if (OperatingSystem.IsLinux()) AddMappedLibraries(result);
            else AddProcessModules(result);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or System.ComponentModel.Win32Exception) { /* the About screen lists what could be read */ }
        return result;
    }

    /// <summary>The ".so" paths in /proc/self/maps, each once.</summary>
    private static void AddMappedLibraries(List<string> result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            int slash = line.IndexOf('/');
            if (slash < 0) continue;
            string path = line[slash..].Trim();
            string name = Path.GetFileName(path);
            if (!(name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal))) continue;
            if (seen.Add(path)) result.Add(path);
        }
    }

    private static void AddProcessModules(List<string> result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var self = Process.GetCurrentProcess();
        foreach (ProcessModule m in self.Modules)
            if (m.FileName is { Length: > 0 } path && seen.Add(path)) result.Add(path);
    }
}
