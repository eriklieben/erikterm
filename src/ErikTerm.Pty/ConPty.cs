using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static ErikTerm.Pty.Win32;

namespace ErikTerm.Pty;

/// <summary>
/// Where the pseudoconsole comes from. The one built into Windows moves at the speed of Windows releases, so when
/// Microsoft's redistributable <c>conpty.dll</c> and <c>OpenConsole.exe</c> sit next to the executable they are used
/// instead (the same host Windows Terminal ships); otherwise, or if the bundled one can't start, it's kernel32's.
/// Setting <c>ERIKTERM_CONPTY=system</c> forces kernel32's.
/// <para>
/// An HPCON has to be resized and closed by whichever library made it, so the bundled ones are remembered here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
#pragma warning disable S6640 // calls the bundled conpty.dll through unmanaged function pointers
internal static unsafe class ConPty
#pragma warning restore S6640
{
    private static readonly nint BundledCreate, BundledResize, BundledClose;
    private static readonly ConcurrentDictionary<nint, byte> Bundled = new();

    static ConPty()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("ERIKTERM_CONPTY"), "system", StringComparison.OrdinalIgnoreCase)) return;
        string dll = Path.Combine(AppContext.BaseDirectory, "conpty.dll");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenConsole.exe"))) return;
        if (!NativeLibrary.TryLoad(dll, out nint library)) return;
        if (NativeLibrary.TryGetExport(library, "ConptyCreatePseudoConsole", out nint create)
            && NativeLibrary.TryGetExport(library, "ConptyResizePseudoConsole", out nint resize)
            && NativeLibrary.TryGetExport(library, "ConptyClosePseudoConsole", out nint close))
            (BundledCreate, BundledResize, BundledClose) = (create, resize, close);
    }

    public static int Create(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console)
    {
        if (BundledCreate != 0)
        {
            nint handle;
            int hr = ((delegate* unmanaged[Stdcall]<COORD, nint, nint, uint, nint*, int>)BundledCreate)(
#pragma warning disable S3869 // a function pointer can't marshal SafeHandle; the caller keeps both handles alive and disposes them only after this returns
                size, input.DangerousGetHandle(), output.DangerousGetHandle(), flags, &handle);
#pragma warning restore S3869
            if (hr >= 0)
            {
                Bundled[handle] = 0;
                console = handle;
                return hr;
            }
        }
        return Kernel32.CreatePseudoConsole(size, input, output, flags, out console);
    }

    public static int Resize(nint console, COORD size) => Bundled.ContainsKey(console)
        ? ((delegate* unmanaged[Stdcall]<nint, COORD, int>)BundledResize)(console, size)
        : Kernel32.ResizePseudoConsole(console, size);

    public static void Close(nint console)
    {
        if (Bundled.TryRemove(console, out _)) ((delegate* unmanaged[Stdcall]<nint, void>)BundledClose)(console);
        else Kernel32.ClosePseudoConsole(console);
    }
}
