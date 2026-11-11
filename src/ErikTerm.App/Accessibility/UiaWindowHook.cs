using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ErikTerm.App.Accessibility;

/// <summary>
/// Answers WM_GETOBJECT for erikterm's windows, which is how UI Automation asks a window for its provider. SDL's
/// message hook (Sdl.SetWindowsMessageHook) can't do it: it only sees posted messages, and only to let them through
/// or swallow them, while WM_GETOBJECT is sent straight to the window procedure and needs a value back. So the window
/// is subclassed: its procedure is replaced by <see cref="WndProc"/>, which handles that one message and passes every
/// other one to SDL's procedure unchanged. Main thread only, like the window procedure itself.
/// </summary>
internal static partial class UiaWindowHook
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETOBJECT = 0x003D, WM_NCDESTROY = 0x0082;

    private static readonly Dictionary<nint, (WindowAccessibility Owner, nint Previous)> Windows = [];

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProc(nint previous, nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>Starts answering for <paramref name="owner"/>'s window. False when the window couldn't be subclassed.</summary>
#pragma warning disable S6640 // Native interop: the window procedure is an unmanaged function pointer.
    public static unsafe bool Install(WindowAccessibility owner)
    {
        nint hwnd = owner.Hwnd;
        if (hwnd == 0 || Windows.ContainsKey(hwnd)) return false;
        nint previous = SetWindowLongPtr(hwnd, GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc);
        if (previous == 0) return false;
        Windows[hwnd] = (owner, previous);
        return true;
    }
#pragma warning restore S6640

    /// <summary>Gives the window its own procedure back (before it is destroyed).</summary>
    public static void Remove(nint hwnd)
    {
        if (!Windows.Remove(hwnd, out var entry)) return;
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, entry.Previous);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var entry)) return 0;
        try
        {
            if (msg == WM_GETOBJECT && unchecked((int)lParam) == Uia.RootObjectId) return entry.Owner.OnGetObject(wParam, lParam);
            if (msg == WM_NCDESTROY)
            {
                Windows.Remove(hwnd);
                SetWindowLongPtr(hwnd, GWLP_WNDPROC, entry.Previous);
            }
        }
#pragma warning disable S2221 // nothing may escape into the window procedure: it would take the process down
        catch (Exception)
        {
            // A provider that can't be handed out leaves the window without one, not the process without a window.
        }
#pragma warning restore S2221
        return CallWindowProc(entry.Previous, hwnd, msg, wParam, lParam);
    }
}
