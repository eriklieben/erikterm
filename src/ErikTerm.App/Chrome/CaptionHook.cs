using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ErikTerm.App.Native;

namespace ErikTerm.App.Chrome;

/// <summary>
/// The Win32 message hook that sees a double click on a borderless window's bar (<see cref="FloatingChrome.InstallCaptionHook"/>).
/// SDL takes one hook for the whole process, so the windows that answer to it are found by HWND. Process-wide, like
/// the hook itself, and only ever touched from the thread that pumps messages.
/// </summary>
internal static class CaptionHook
{
    private static readonly Dictionary<nint, FloatingChrome> Windows = [];
    private static bool _installed;

    public static void Add(nint hwnd, FloatingChrome chrome)
    {
        Windows[hwnd] = chrome;
        InstallOnce();
    }

    public static void Remove(nint hwnd) => Windows.Remove(hwnd);

    private static void InstallOnce()
    {
        if (_installed) return;
        _installed = true;
#pragma warning disable S6640 // Native interop: SDL takes the hook as an unmanaged function pointer.
        unsafe { Sdl.SetWindowsMessageHook(&Hook, 0); }
#pragma warning restore S6640
    }

    /// <summary>
    /// Runs inside SDL's message pump, so it does as little as possible: notes the click and lets the tick act on it.
    /// Returning 0 swallows the message, which keeps SDL from starting a window drag on the second click.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
#pragma warning disable S6640 // Native interop: SDL calls this with a pointer to the Win32 message.
    private static unsafe byte Hook(nint userdata, Sdl.Msg* msg)
#pragma warning restore S6640
    {
        try
        {
            if (msg is null || msg->Message != Sdl.WM_NCLBUTTONDBLCLK) return 1;
            if (!Windows.TryGetValue(msg->Hwnd, out var chrome)) return 1;
            chrome.NoteCaptionDoubleClick();
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
