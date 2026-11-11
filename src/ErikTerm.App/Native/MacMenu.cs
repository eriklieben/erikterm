using System.Runtime.InteropServices;

namespace ErikTerm.App.Native;

/// <summary>
/// macOS: SDL3 gives the app a default menu bar (SDL_cocoaevents.m CreateApplicationMenus) whose Window → Close item
/// has the key equivalent Cmd+W. AppKit handles key equivalents after SDL has already seen the key, so Cmd+W would both
/// run erikterm's binding (close the pane) and close the whole window. This takes the shortcut off that menu item (the
/// window's close button still works); the other items (Cmd+Q, Cmd+H, Cmd+M, Ctrl+Cmd+F) are left as they are.
/// Plain Objective-C runtime calls: objc_msgSend with one exact prototype per call shape.
/// </summary>
#pragma warning disable S6640 // binding: objc_msgSend is called through exact function pointer prototypes
internal static unsafe partial class MacMenu
#pragma warning restore S6640
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>Call once on the main thread, after SDL_Init(VIDEO). Does nothing elsewhere or if anything is missing.</summary>
    public static void DetachCloseShortcut()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            nint app = Send(Class("NSApplication"), Sel("sharedApplication"));
            nint menu = app == 0 ? 0 : Send(app, Sel("mainMenu"));
            if (menu == 0) return;
            nint performClose = Sel("performClose:");
            nint empty = 0;
            nint menus = SendInt(menu, Sel("numberOfItems"));
            for (nint i = 0; i < menus; i++)
                DetachFromSubmenu(Send(SendArg(menu, Sel("itemAtIndex:"), i), Sel("submenu")), performClose, ref empty);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No Objective-C runtime: nothing to fix.
        }
    }

    private static void DetachFromSubmenu(nint sub, nint performClose, ref nint empty)
    {
        if (sub == 0) return;
        nint items = SendInt(sub, Sel("numberOfItems"));
        for (nint j = 0; j < items; j++)
        {
            nint item = SendArg(sub, Sel("itemAtIndex:"), j);
            if (item == 0 || Send(item, Sel("action")) != performClose) continue;
            if (empty == 0) empty = EmptyNSString();
            SendArg(item, Sel("setKeyEquivalent:"), empty);
        }
    }

    private static nint EmptyNSString()
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8("");
        try { return SendArg(Send(Class("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    #pragma warning disable S4200 // a short name at many call sites of a lookup that has no arguments to check and no failure to translate
    private static nint Class(string name) => objc_getClass(name);
    #pragma warning restore S4200
    #pragma warning disable S4200 // a short name at many call sites of a lookup that has no arguments to check and no failure to translate
    private static nint Sel(string name) => sel_registerName(name);
    #pragma warning restore S4200

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendInt(nint receiver, nint selector); // NSInteger result

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendArg(nint receiver, nint selector, nint arg);
}
