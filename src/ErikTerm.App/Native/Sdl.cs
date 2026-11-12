using System.Runtime.InteropServices;

namespace ErikTerm.App.Native;

/// <summary>Minimal SDL3 bindings (zlib license, system library). Constants/offsets verified against SDL 3.4 headers.</summary>
#pragma warning disable S6640 // binding: SDL's API takes pointers and native callbacks
internal static unsafe partial class Sdl
#pragma warning restore S6640
{
    private const string Lib = NativeLibraries.Sdl3;

    public const uint INIT_VIDEO = 0x20;
    public const uint INIT_EVENTS = 0x4000;

    public const ulong WINDOW_BORDERLESS = 0x10;
    public const ulong WINDOW_RESIZABLE = 0x20;
    public const ulong WINDOW_TRANSPARENT = 0x40000000;
    public const ulong WINDOW_HIGH_PIXEL_DENSITY = 0x2000;
    public const ulong WINDOW_HIDDEN = 0x8;
    public const ulong WINDOW_TOOLTIP = 0x40000;
    public const int WINDOWPOS_UNDEFINED = 0x1FFF0000;

    public const uint PIXELFORMAT_RGBA32 = 0x16762004;
    public const int TEXTUREACCESS_STATIC = 0;
    public const uint BLENDMODE_BLEND = 1;
    public const int SCALEMODE_NEAREST = 0;
    public const int SCALEMODE_LINEAR = 1;
    public const int SCALEMODE_PIXELART = 2; // SDL 3.4: crisp upscaling of pixel art, antialiased texel edges

    public const uint EVENT_QUIT = 0x100;
    /// <summary>Light/dark changed (Windows: WM_SETTINGCHANGE "ImmersiveColorSet"; macOS: the appearance; Linux: the portal's color-scheme). Has no window.</summary>
    public const uint EVENT_SYSTEM_THEME_CHANGED = 0x108;
    public const uint EVENT_WINDOW_FIRST = 0x202;
    public const uint EVENT_WINDOW_EXPOSED = 0x204;
    public const uint EVENT_WINDOW_RESIZED = 0x206;
    public const uint EVENT_WINDOW_PIXEL_SIZE_CHANGED = 0x207;
    public const uint EVENT_WINDOW_FOCUS_GAINED = 0x20E;
    public const uint EVENT_WINDOW_FOCUS_LOST = 0x20F;
    public const uint EVENT_WINDOW_CLOSE_REQUESTED = 0x210;
    public const uint EVENT_WINDOW_DISPLAY_SCALE_CHANGED = 0x214;
    public const uint EVENT_WINDOW_LAST = 0x21A;
    public const uint EVENT_WINDOW_MOUSE_ENTER = 0x20C;
    public const uint EVENT_WINDOW_MOUSE_LEAVE = 0x20D;
    public const uint EVENT_WINDOW_MOVED = 0x205;
    public const uint EVENT_KEY_DOWN = 0x300;
    public const uint EVENT_KEY_UP = 0x301;
    public const uint EVENT_TEXT_EDITING = 0x302;
    public const uint EVENT_TEXT_INPUT = 0x303;
    public const uint EVENT_MOUSE_MOTION = 0x400;
    public const uint EVENT_MOUSE_BUTTON_DOWN = 0x401;
    public const uint EVENT_MOUSE_BUTTON_UP = 0x402;
    public const uint EVENT_MOUSE_WHEEL = 0x403;
    // SDL_DropEvent (verified against SDL3 headers with offsetof: windowID 16, x 20, y 24, source 32, data 40).
    public const uint EVENT_DROP_FILE = 0x1000, EVENT_DROP_TEXT = 0x1001, EVENT_DROP_BEGIN = 0x1002, EVENT_DROP_COMPLETE = 0x1003, EVENT_DROP_POSITION = 0x1004;

    public const byte BUTTON_LEFT = 1, BUTTON_MIDDLE = 2, BUTTON_RIGHT = 3;

    public const int SYSTEM_CURSOR_DEFAULT = 0, SYSTEM_CURSOR_TEXT = 1, SYSTEM_CURSOR_EW_RESIZE = 7,
        SYSTEM_CURSOR_NS_RESIZE = 8, SYSTEM_CURSOR_POINTER = 11;

    public const ushort KMOD_SHIFT = 0x0003;
    public const ushort KMOD_CTRL = 0x00C0;
    public const ushort KMOD_LALT = 0x0100; // RALT is AltGr on many layouts: not treated as Meta
    public const ushort KMOD_GUI = 0x0C00;

    public const uint K_RETURN = 0x0D, K_ESCAPE = 0x1B, K_BACKSPACE = 0x08, K_TAB = 0x09, K_SPACE = 0x20, K_DELETE = 0x7F;
    public const uint K_EQUALS = 0x3D, K_MINUS = 0x2D, K_0 = 0x30;
    public const uint K_RIGHT = 0x4000004F, K_LEFT = 0x40000050, K_DOWN = 0x40000051, K_UP = 0x40000052;
    public const uint K_INSERT = 0x40000049, K_HOME = 0x4000004A, K_PAGEUP = 0x4000004B, K_END = 0x4000004D, K_PAGEDOWN = 0x4000004E;
    public const uint K_F1 = 0x4000003A, K_F12 = 0x40000045;
    public const uint K_KP_ENTER = 0x40000058;

    /// <summary>SDL_Event is a 128-byte union; only the fields we read are mapped.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct Event
    {
        [FieldOffset(0)] public uint Type;
        /// <summary>The window the event is for (window, keyboard, text, mouse and user events; 0 = none).</summary>
        [FieldOffset(16)] public uint WindowID;
        // SDL_WindowEvent
        [FieldOffset(20)] public int WindowData1;
        [FieldOffset(24)] public int WindowData2;
        // SDL_KeyboardEvent
        [FieldOffset(24)] public uint KeyScancode;
        [FieldOffset(28)] public uint Key;
        [FieldOffset(32)] public ushort KeyMod;
        [FieldOffset(37)] public byte KeyRepeat;
        // SDL_MouseButtonEvent / SDL_MouseMotionEvent (x/y are in window points)
        [FieldOffset(24)] public byte MouseButton;
        [FieldOffset(26)] public byte MouseClicks;
        [FieldOffset(24)] public uint MotionState;
        [FieldOffset(28)] public float MouseX;
        [FieldOffset(32)] public float MouseY;
        // SDL_TextInputEvent
        [FieldOffset(24)] public byte* Text;
        // SDL_MouseWheelEvent
        [FieldOffset(28)] public float WheelY;
        [FieldOffset(36)] public float WheelMouseX;
        [FieldOffset(40)] public float WheelMouseY;
        [FieldOffset(48)] public int WheelIntegerY;
        // SDL_DropEvent (x/y in window points; data: a file path or UTF-8 text, owned by SDL)
        [FieldOffset(20)] public float DropX;
        [FieldOffset(24)] public float DropY;
        [FieldOffset(40)] public byte* DropData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public float X, Y;
        public float R, G, B, A;
        public float U, V;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int X, Y, W, H;
    }

    [LibraryImport(Lib, EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Init(uint flags);

    [LibraryImport(Lib, EntryPoint = "SDL_Quit")]
    public static partial void Quit();

    [LibraryImport(Lib, EntryPoint = "SDL_GetError")]
    private static partial byte* GetErrorPtr();

    public static string GetError() => Marshal.PtrToStringUTF8((nint)GetErrorPtr()) ?? "";

    [LibraryImport(Lib, EntryPoint = "SDL_SetTextInputArea")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetTextInputArea(nint window, Rect* rect, int cursor);

    [LibraryImport(Lib, EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetHint(string name, string value);

    /// <summary>SDL_CreateWindow (see <see cref="CreateOffscreenWindow"/> for SDL's offscreen video driver).</summary>
    public static nint CreateWindow(string title, int w, int h, ulong flags) =>
        IsOffscreenVideo() ? CreateOffscreenWindow(title, 0, 0, 0, w, h, flags) : CreateWindowNative(title, w, h, flags);

    [LibraryImport(Lib, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CreateWindowNative(string title, int w, int h, ulong flags);

    [LibraryImport(Lib, EntryPoint = "SDL_DestroyWindow")]
    public static partial void DestroyWindow(nint window);

    /// <summary>A popup (tooltip / menu) positioned relative to <paramref name="parent"/>, in points.</summary>
    public static nint CreatePopupWindow(nint parent, int offsetX, int offsetY, int w, int h, ulong flags) =>
        IsOffscreenVideo() ? CreateOffscreenWindow(null, parent, offsetX, offsetY, w, h, flags)
            : CreatePopupWindowNative(parent, offsetX, offsetY, w, h, flags);

    [LibraryImport(Lib, EntryPoint = "SDL_CreatePopupWindow")]
    private static partial nint CreatePopupWindowNative(nint parent, int offsetX, int offsetY, int w, int h, ulong flags);

    /// <summary>
    /// SDL's offscreen video driver (SDL_VIDEODRIVER=offscreen: the integration tests, scripts/reference-shots.sh),
    /// which never shows anything. Only there do windows go through <see cref="CreateOffscreenWindow"/>.
    /// </summary>
    private static bool IsOffscreenVideo() => GetCurrentVideoDriver() == "offscreen";

    /// <summary>
    /// What SDL_CreateWindow / SDL_CreatePopupWindow do (a popup when <paramref name="parent"/> isn't 0), with the
    /// window marked as having an external graphics context. Only used under the offscreen video driver: on macOS SDL
    /// adds SDL_WINDOW_OPENGL to every window created without a graphics flag (SDL_DefaultGraphicsBackends in
    /// SDL_video.c), and the offscreen driver's OpenGL is EGL, which macOS doesn't have, so SDL_CreateWindow failed
    /// with "Could not initialize OpenGL / GLES library". erikterm never draws with OpenGL itself (the renderer picks
    /// its own backend, and off-screen that is the software one, onto the window surface); the external-context
    /// property only makes SDL skip that default, and nothing else reads it on these platforms.
    /// </summary>
    private static nint CreateOffscreenWindow(string? title, nint parent, int x, int y, int w, int h, ulong flags)
    {
        uint props = CreateProperties();
        if (props == 0) return 0;
        try
        {
            if (title is not null) SetStringProperty(props, "SDL.window.create.title", title);
            if (parent != 0)
            {
                SetPointerProperty(props, "SDL.window.create.parent", parent);
                SetNumberProperty(props, "SDL.window.create.x", x);
                SetNumberProperty(props, "SDL.window.create.y", y);
            }
            SetNumberProperty(props, "SDL.window.create.width", w);
            SetNumberProperty(props, "SDL.window.create.height", h);
            SetNumberProperty(props, "SDL.window.create.flags", unchecked((long)flags));
            SetBooleanProperty(props, "SDL.window.create.external_graphics_context", true);
            return CreateWindowWithProperties(props);
        }
        finally
        {
            DestroyProperties(props);
        }
    }

    [LibraryImport(Lib, EntryPoint = "SDL_CreateWindowWithProperties")]
    private static partial nint CreateWindowWithProperties(uint props);

    [LibraryImport(Lib, EntryPoint = "SDL_CreateProperties")]
    private static partial uint CreateProperties();

    [LibraryImport(Lib, EntryPoint = "SDL_DestroyProperties")]
    private static partial void DestroyProperties(uint props);

    [LibraryImport(Lib, EntryPoint = "SDL_SetStringProperty", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetStringProperty(uint props, string name, string value);

    [LibraryImport(Lib, EntryPoint = "SDL_SetPointerProperty", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetPointerProperty(uint props, string name, nint value);

    [LibraryImport(Lib, EntryPoint = "SDL_SetNumberProperty", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetNumberProperty(uint props, string name, long value);

    [LibraryImport(Lib, EntryPoint = "SDL_SetBooleanProperty", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetBooleanProperty(uint props, string name, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowID")]
    public static partial uint GetWindowID(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowFromID")]
    public static partial nint GetWindowFromID(uint id);

    [LibraryImport(Lib, EntryPoint = "SDL_ShowWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ShowWindow(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_RaiseWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RaiseWindow(nint window);

    /// <summary>Window position in desktop points; Wayland doesn't tell clients where their windows are (0, 0 there).</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowPosition")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowPosition(nint window, int* x, int* y);

    /// <summary>Ignored by Wayland compositors for toplevels (they place windows); popups move relative to their parent.</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowPosition")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowPosition(nint window, int x, int y);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowSize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowSize(nint window, int* w, int* h);

    /// <summary>Pointer position in desktop points (not meaningful on Wayland outside our windows).</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_GetGlobalMouseState")]
    public static partial uint GetGlobalMouseState(float* x, float* y);

    /// <summary>Pointer position relative to the window with mouse focus, in points.</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_GetMouseState")]
    public static partial uint GetMouseState(float* x, float* y);

    [LibraryImport(Lib, EntryPoint = "SDL_GetMouseFocus")]
    public static partial nint GetMouseFocus();

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowTitle", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowTitle(nint window, string title);

    /// <summary>SDL_FlashOperation: 0 cancel, 1 briefly, 2 until focused.</summary>
    public const int FLASH_CANCEL = 0, FLASH_BRIEFLY = 1, FLASH_UNTIL_FOCUSED = 2;

    [LibraryImport(Lib, EntryPoint = "SDL_FlashWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool FlashWindow(nint window, int operation);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowSizeInPixels")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowSizeInPixels(nint window, int* w, int* h);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowSize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowSize(nint window, int w, int h);

    /// <summary>The display's bounds minus whatever the desktop reserves (taskbar, dock, panels), in desktop points.</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_GetDisplayUsableBounds")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetDisplayUsableBounds(uint displayId, Rect* rect);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowBordered")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowBordered(nint window, [MarshalAs(UnmanagedType.U1)] bool bordered);

    public const ulong WINDOW_MAXIMIZED = 0x80, WINDOW_MINIMIZED = 0x40;

    [LibraryImport(Lib, EntryPoint = "SDL_MinimizeWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool MinimizeWindow(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_MaximizeWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool MaximizeWindow(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_RestoreWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RestoreWindow(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowProperties")]
    public static partial uint GetWindowProperties(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetPointerProperty", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetPointerProperty(uint props, string name, nint defaultValue);

    /// <summary>The Win32 HWND behind a window, or 0 off Windows.</summary>
    public static nint Hwnd(nint window) =>
        window == 0 ? 0 : GetPointerProperty(GetWindowProperties(window), "SDL.window.win32.hwnd", 0);

    /// <summary>SDL_HitTestResult, in header order.</summary>
    public const int HITTEST_NORMAL = 0, HITTEST_DRAGGABLE = 1, HITTEST_RESIZE_TOPLEFT = 2, HITTEST_RESIZE_TOP = 3,
        HITTEST_RESIZE_TOPRIGHT = 4, HITTEST_RESIZE_RIGHT = 5, HITTEST_RESIZE_BOTTOMRIGHT = 6,
        HITTEST_RESIZE_BOTTOM = 7, HITTEST_RESIZE_BOTTOMLEFT = 8, HITTEST_RESIZE_LEFT = 9;

    /// <summary>SDL_Point: the hit-tested position, in window points.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    /// <summary>
    /// Marks regions of a window as draggable or resizable, which is the only way a borderless window can be moved.
    /// The callback runs during event processing and must not allocate; a null callback disables hit testing.
    /// Platforms without support return false.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowHitTest")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowHitTest(nint window, delegate* unmanaged[Cdecl]<nint, Point*, nint, int> callback, nint callbackData);

    /// <summary>Windows only: a raw window message, as the message hook is handed it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        #pragma warning disable S3218 // mirrors the hwnd field of Win32's MSG, and Sdl.Hwnd() is the call that yields that same handle: one name for one thing
        public nint Hwnd;
        #pragma warning restore S3218
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX, PtY;
    }

    /// <summary>Non-client mouse messages: what a window gets instead of the ordinary ones where its hit test claims a region.</summary>
    public const uint WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3;

    /// <summary>
    /// Windows only, and process-wide: sees every message before SDL does, and swallows the ones the callback
    /// answers false for. The only way to hear about clicks on a region the hit test gave away (Chrome/WindowHitTest.cs),
    /// which stops being ordinary client area and so stops producing ordinary mouse events.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowsMessageHook")]
    public static partial void SetWindowsMessageHook(delegate* unmanaged[Cdecl]<nint, Msg*, byte> callback, nint userdata);

    [LibraryImport(Lib, EntryPoint = "SDL_GetModState")]
    public static partial ushort GetModState();

    /// <summary>The keycode a scancode produces with <paramref name="modstate"/> in the current layout;
    /// <paramref name="keyEvent"/> false ignores SDL_HINT_KEYCODE_OPTIONS (no unmodified/latin_letters mapping).</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_GetKeyFromScancode")]
    public static partial uint GetKeyFromScancode(uint scancode, ushort modstate, [MarshalAs(UnmanagedType.U1)] bool keyEvent);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowPixelDensity")]
    public static partial float GetWindowPixelDensity(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_SetWindowFullscreen")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowFullscreen(nint window, [MarshalAs(UnmanagedType.U1)] bool fullscreen);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowFlags")]
    public static partial ulong GetWindowFlags(nint window);

    public const ulong WINDOW_FULLSCREEN = 0x1;

    [LibraryImport(Lib, EntryPoint = "SDL_CreateSystemCursor")]
    public static partial nint CreateSystemCursor(int id);

    [LibraryImport(Lib, EntryPoint = "SDL_SetCursor")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetCursor(nint cursor);

    [LibraryImport(Lib, EntryPoint = "SDL_SetRenderClipRect")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetRenderClipRect(nint renderer, Rect* rect);

    [LibraryImport(Lib, EntryPoint = "SDL_SetClipboardText", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetClipboardText(string text);

    [LibraryImport(Lib, EntryPoint = "SDL_SetPrimarySelectionText", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetPrimarySelectionText(string text);

    [LibraryImport(Lib, EntryPoint = "SDL_GetPrimarySelectionText")]
    private static partial byte* GetPrimarySelectionTextPtr();

    public static string GetPrimarySelectionText()
    {
        byte* p = GetPrimarySelectionTextPtr();
        if (p == null) return "";
        string s = Marshal.PtrToStringUTF8((nint)p) ?? "";
        Free(p);
        return s;
    }

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowDisplayScale")]
    public static partial float GetWindowDisplayScale(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_StartTextInput")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool StartTextInput(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_CreateRenderer", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateRenderer(nint window, string? name);

    [LibraryImport(Lib, EntryPoint = "SDL_DestroyRenderer")]
    public static partial void DestroyRenderer(nint renderer);

    [LibraryImport(Lib, EntryPoint = "SDL_SetRenderVSync")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetRenderVSync(nint renderer, int vsync);

    [LibraryImport(Lib, EntryPoint = "SDL_SetRenderDrawColor")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetRenderDrawColor(nint renderer, byte r, byte g, byte b, byte a);

    [LibraryImport(Lib, EntryPoint = "SDL_RenderClear")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RenderClear(nint renderer);

    [LibraryImport(Lib, EntryPoint = "SDL_RenderGeometry")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RenderGeometry(nint renderer, nint texture, Vertex* vertices, int numVertices, int* indices, int numIndices);

    [LibraryImport(Lib, EntryPoint = "SDL_RenderPresent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RenderPresent(nint renderer);

    [LibraryImport(Lib, EntryPoint = "SDL_CreateTexture")]
    public static partial nint CreateTexture(nint renderer, uint format, int access, int w, int h);

    [LibraryImport(Lib, EntryPoint = "SDL_DestroyTexture")]
    public static partial void DestroyTexture(nint texture);

    [LibraryImport(Lib, EntryPoint = "SDL_UpdateTexture")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool UpdateTexture(nint texture, Rect* rect, void* pixels, int pitch);

    [LibraryImport(Lib, EntryPoint = "SDL_SetTextureBlendMode")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetTextureBlendMode(nint texture, uint blendMode);

    [LibraryImport(Lib, EntryPoint = "SDL_SetTextureScaleMode")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetTextureScaleMode(nint texture, int scaleMode);

    [LibraryImport(Lib, EntryPoint = "SDL_WaitEventTimeout")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool WaitEventTimeout(Event* ev, int timeoutMs);

    [LibraryImport(Lib, EntryPoint = "SDL_PollEvent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool PollEvent(Event* ev);

    [LibraryImport(Lib, EntryPoint = "SDL_PushEvent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool PushEvent(Event* ev);

    [LibraryImport(Lib, EntryPoint = "SDL_RegisterEvents")]
    public static partial uint RegisterEvents(int count);

    /// <summary>
    /// SDL_AddEventWatch: an SDL_EventFilter called for every event as it is created, on the thread that creates it,
    /// and — unlike the event queue — from inside a platform's modal loop (Windows resizes that way), which is what
    /// makes drawing during a resize drag possible at all (Host/LiveResizeWatch.cs). A watch's return value is ignored, but
    /// it shares SDL_EventFilter's signature, so it must still return true rather than drop the event.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "SDL_AddEventWatch")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddEventWatch(delegate* unmanaged[Cdecl]<nint, Event*, byte> filter, nint userdata);

    [LibraryImport(Lib, EntryPoint = "SDL_RemoveEventWatch")]
    public static partial void RemoveEventWatch(delegate* unmanaged[Cdecl]<nint, Event*, byte> filter, nint userdata);

    [LibraryImport(Lib, EntryPoint = "SDL_RenderReadPixels")]
    public static partial nint RenderReadPixels(nint renderer, Rect* rect);

    [LibraryImport(Lib, EntryPoint = "SDL_SaveBMP", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SaveBMP(nint surface, string file);

    [LibraryImport(Lib, EntryPoint = "SDL_DestroySurface")]
    public static partial void DestroySurface(nint surface);

    [LibraryImport(Lib, EntryPoint = "SDL_GetClipboardText")]
    private static partial byte* GetClipboardTextPtr();

    [LibraryImport(Lib, EntryPoint = "SDL_free")]
    private static partial void Free(void* p);

    public static string GetClipboardText()
    {
        byte* p = GetClipboardTextPtr();
        if (p == null) return "";
        string s = Marshal.PtrToStringUTF8((nint)p) ?? "";
        Free(p);
        return s;
    }
}
