using System.Runtime.InteropServices;
using ErikTerm.Config;

namespace ErikTerm.App.SystemPreferences;

/// <summary>
/// macOS: System Settings › Accessibility › Display's "Reduce motion" and "Increase contrast", from
/// [NSWorkspace sharedWorkspace] through the Objective-C runtime (one exact objc_msgSend prototype per call shape, as in
/// Native/MacMenu.cs). macOS has no system-wide text size for applications, so the text scale stays unknown. Read on
/// the main thread; it's two property reads.
/// </summary>
internal sealed partial class MacAccessibilityPreferences : IAccessibilityPreferences
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public bool ReadsSlowly => false;

    public SystemAccessibility Read()
    {
        if (!OperatingSystem.IsMacOS()) return SystemAccessibility.Unknown;
        try
        {
            nint workspaceClass = objc_getClass("NSWorkspace");
            nint workspace = workspaceClass == 0 ? 0 : Send(workspaceClass, sel_registerName("sharedWorkspace"));
            if (workspace == 0) return SystemAccessibility.Unknown;
            return new SystemAccessibility
            {
                ReduceMotion = SendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion")) != 0,
                HighContrast = SendBool(workspace, sel_registerName("accessibilityDisplayShouldIncreaseContrast")) != 0,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return SystemAccessibility.Unknown;
        }
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    /// <summary>A BOOL result: one byte on both arm64 and x86-64.</summary>
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBool(nint receiver, nint selector);
}
