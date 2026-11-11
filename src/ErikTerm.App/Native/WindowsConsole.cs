using System.Runtime.InteropServices;

namespace ErikTerm.App.Native;

/// <summary>
/// On Windows erikterm.exe is a GUI-subsystem app (OutputType WinExe), so starting it from Explorer or a shortcut
/// opens no console window. Started from a terminal it has no console either, so stderr diagnostics, <c>--help</c>
/// and <c>erikterm cli</c> output would vanish: <see cref="AttachToParent"/> attaches to the parent's console (if
/// there is one) and points the standard handles that aren't redirected at it. Redirected handles
/// (<c>erikterm cli ... &gt; out.txt</c>, pipes) are inherited as usual and left alone.
/// <para>
/// Caveats of a GUI app on a console: cmd.exe doesn't wait for it, so output can land after the next prompt (use
/// <c>start /wait erikterm ...</c> or pipe it, e.g. <c>| more</c>); and closing that console window ends an erikterm
/// that was started from it (like SIGHUP on Unix). Ctrl+C in the parent console is ignored, except for <c>cli</c>.
/// </para>
/// </summary>
#pragma warning disable S6640 // binding: the Win32 console API takes pointers
internal static unsafe partial class WindowsConsole
#pragma warning restore S6640
{
    private const string Lib = "kernel32";
    private const uint AttachParentProcess = unchecked((uint)-1);
    private const int StdInput = -10, StdOutput = -11, StdError = -12;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint CtrlCEvent = 0, CtrlBreakEvent = 1;

    /// <summary>
    /// Call first thing in Main, before anything touches <see cref="Console"/>. No-op off Windows.
    /// <paramref name="shortLived"/>: this run is the CLI or the tmux shim, not the window, so Ctrl+C in the
    /// console that started it should end it as usual.
    /// </summary>
    public static void AttachToParent(string[] args, bool shortLived = false)
    {
        if (!OperatingSystem.IsWindows()) return;
        bool missing = IsMissing(StdInput) || IsMissing(StdOutput) || IsMissing(StdError);
        if (!missing || !AttachConsole(AttachParentProcess)) return; // all redirected, or no parent console

        Reopen(StdInput, "CONIN$", GenericRead | GenericWrite);
        Reopen(StdOutput, "CONOUT$", GenericRead | GenericWrite);
        Reopen(StdError, "CONOUT$", GenericRead | GenericWrite);

        // The window shouldn't die from Ctrl+C typed at the shell it was started from; the CLI and the shim should.
        bool cli = shortLived || (args.Length > 0 && args[0] == "cli");
        if (!cli) SetConsoleCtrlHandler(&IgnoreInterrupt, 1);
    }

    private static bool IsMissing(int std)
    {
        nint h = GetStdHandle(std);
        return h == 0 || h == -1;
    }

    private static void Reopen(int std, string device, uint access)
    {
        if (!IsMissing(std)) return; // redirected: keep it
        nint h = CreateFileW(device, access, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (h != -1) SetStdHandle(std, h);
    }

    [UnmanagedCallersOnly]
    private static int IgnoreInterrupt(uint type) => type is CtrlCEvent or CtrlBreakEvent ? 1 : 0;

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport(Lib)]
    private static partial nint GetStdHandle(int std);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int std, nint handle);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport(Lib)]
    private static partial int SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, int add);
}
