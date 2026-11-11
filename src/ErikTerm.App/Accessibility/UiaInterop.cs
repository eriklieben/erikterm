using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ErikTerm.App.Accessibility;

// UI Automation's provider interfaces (UIAutomationCore.h), declared for the COM source generator: no reflection, no
// built-in COM, so Native AOT compiles them like any other code. Interface pointers cross as nint and are made and
// unwrapped by Uia.ComPointer / Uia.Unwrap, so reference counts stay explicit. Method order is the vtable's order.
// Names follow the IDL (get_X for properties) because the generator turns each method into exactly one vtable slot.
#pragma warning disable IDE1006, CA1707 // the IDL's names, so the vtable can be checked against UIAutomationCore.h
#pragma warning disable S6640 // binding: UI Automation's interfaces take VARIANTs and SAFEARRAY elements by pointer

[GeneratedComInterface]
[Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal unsafe partial interface IRawElementProviderSimple
{
    int get_ProviderOptions();
    nint GetPatternProvider(int patternId);
    void GetPropertyValue(int propertyId, Variant* pRetVal);
    nint get_HostRawElementProvider();
}

[GeneratedComInterface]
[Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal unsafe partial interface IRawElementProviderFragment
{
    nint Navigate(int direction);
    nint GetRuntimeId();
    UiaRect get_BoundingRectangle();
    nint GetEmbeddedFragmentRoots();
    void SetFocus();
    nint get_FragmentRoot();
}

[GeneratedComInterface]
[Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    nint ElementProviderFromPoint(double x, double y);
    nint GetFocus();
}

[GeneratedComInterface]
[Guid("3589c92c-63f3-4367-99bb-ada653b77cf2")]
internal partial interface ITextProvider
{
    nint GetSelection();
    nint GetVisibleRanges();
    nint RangeFromChild(nint childElement);
    nint RangeFromPoint(UiaPoint point);
    nint get_DocumentRange();
    int get_SupportedTextSelection();
}

[GeneratedComInterface]
[Guid("0dc5e6ed-3e16-4bf1-8f9a-a979878bc195")]
internal partial interface ITextProvider2 : ITextProvider
{
    nint RangeFromAnnotation(nint annotationElement);
    nint GetCaretRange(out int isActive);
}

[GeneratedComInterface]
[Guid("5347ad7b-c355-46f8-aff5-909033582f63")]
internal unsafe partial interface ITextRangeProvider
{
    nint Clone();
    int Compare(nint range);
    int CompareEndpoints(int endpoint, nint targetRange, int targetEndpoint);
    void ExpandToEnclosingUnit(int unit);
    nint FindAttribute(int attributeId, Variant val, int backward);
    nint FindText([MarshalUsing(typeof(BStrStringMarshaller))] string text, int backward, int ignoreCase);
    void GetAttributeValue(int attributeId, Variant* pRetVal);
    nint GetBoundingRectangles();
    nint GetEnclosingElement();
    [return: MarshalUsing(typeof(BStrStringMarshaller))]
    string GetText(int maxLength);
    int Move(int unit, int count);
    int MoveEndpointByUnit(int endpoint, int unit, int count);
    void MoveEndpointByRange(int endpoint, nint targetRange, int targetEndpoint);
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    void ScrollIntoView(int alignToTop);
    nint GetChildren();
}

#pragma warning restore IDE1006, CA1707

[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public double Left, Top, Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UiaPoint
{
    public double X, Y;
}

/// <summary>A VARIANT: 16 bytes on 32-bit, 24 on 64-bit (the type tag, three reserved words, two pointers of payload).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Variant
{
    public ushort Vt;
    private ushort _reserved1, _reserved2, _reserved3;
    private nint _data;
    private nint _data2;

    public const ushort VT_EMPTY = 0, VT_I4 = 3, VT_BSTR = 8, VT_BOOL = 11, VT_UNKNOWN = 13;

    public static void SetInt(Variant* v, int value)
    {
        v->Vt = VT_I4;
        *(int*)&v->_data = value;
    }

    public static void SetBool(Variant* v, bool value)
    {
        v->Vt = VT_BOOL;
        *(short*)&v->_data = value ? (short)-1 : (short)0;
    }

    public static void SetString(Variant* v, string value)
    {
        v->Vt = VT_BSTR;
        v->_data = Marshal.StringToBSTR(value);
    }

    /// <summary>Takes over a reference the caller already holds.</summary>
    public static void SetUnknown(Variant* v, nint unknown)
    {
        v->Vt = VT_UNKNOWN;
        v->_data = unknown;
    }
}

/// <summary>Native entry points and constants of UI Automation, and the COM plumbing the providers share.</summary>
internal static unsafe partial class Uia
{
    // Property, pattern, control type and event ids (UIAutomationClient.h).
    public const int RuntimeIdProperty = 30000, ControlTypeProperty = 30003, LocalizedControlTypeProperty = 30004,
        NameProperty = 30005, HasKeyboardFocusProperty = 30008, IsKeyboardFocusableProperty = 30009,
        IsEnabledProperty = 30010, AutomationIdProperty = 30011, ClassNameProperty = 30012, HelpTextProperty = 30013,
        IsControlElementProperty = 30016, IsContentElementProperty = 30017, IsPasswordProperty = 30019,
        IsOffscreenProperty = 30022, FrameworkIdProperty = 30024, IsTextPatternAvailableProperty = 30040,
        ProviderDescriptionProperty = 30107;

    public const int TextPattern = 10014, TextPattern2 = 10024;
    public const int EditControlType = 50004;
    public const int FocusChangedEvent = 20005, TextSelectionChangedEvent = 20014, TextChangedEvent = 20015,
        NotificationEvent = 20035;
    public const int IsReadOnlyAttribute = 40015;

    public const int ProviderOptionsServerSide = 0x2, ProviderOptionsUseComThreading = 0x20;
    public const int NavigateParent = 0, NavigateNextSibling = 1, NavigatePreviousSibling = 2, NavigateFirstChild = 3, NavigateLastChild = 4;
    public const int AppendRuntimeId = 3;
    public const int EndpointStart = 0, EndpointEnd = 1;
    public const int SupportedTextSelectionSingle = 1;
    public const int StructureChildrenInvalidated = 2;
    public const int NotificationKindActionCompleted = 2;
    public const int NotificationProcessingAll = 2, NotificationProcessingMostRecent = 3;
    /// <summary>The object id WM_GETOBJECT carries in lParam when UI Automation asks for the window's provider.</summary>
    public const int RootObjectId = -25;

    /// <summary>UIA_E_ELEMENTNOTAVAILABLE: the pane is gone.</summary>
    public const int ElementNotAvailable = unchecked((int)0x80040201);

    public static readonly Guid IidSimple = new("d6dd68d1-86fd-4332-8666-9abedea2d24c");
    public static readonly Guid IidFragment = new("f7063da8-8359-439c-9297-bbc5299a7d87");
    public static readonly Guid IidFragmentRoot = new("620ce2a5-ab8f-40a9-86cb-de3c75599b58");
    public static readonly Guid IidTextRange = new("5347ad7b-c355-46f8-aff5-909033582f63");

    private const string Core = "uiautomationcore.dll";

    [LibraryImport(Core)]
    public static partial nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, nint provider);

    [LibraryImport(Core)]
    public static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);

    [LibraryImport(Core)]
    public static partial int UiaRaiseAutomationEvent(nint provider, int eventId);

    [LibraryImport(Core)]
    public static partial int UiaRaiseStructureChangedEvent(nint provider, int structureChangeType, int* runtimeId, int runtimeIdLength);

    [LibraryImport(Core)]
    public static partial int UiaRaiseNotificationEvent(nint provider, int kind, int processing, nint displayString, nint activityId);

    [LibraryImport(Core)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UiaClientsAreListening();

    [LibraryImport(Core)]
    public static partial int UiaDisconnectProvider(nint provider);

    [LibraryImport(Core)]
    public static partial int UiaGetReservedNotSupportedValue(out nint unknown);

    [LibraryImport("oleaut32.dll")]
    private static partial nint SafeArrayCreateVector(ushort vt, int lowerBound, uint elements);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayPutElement(nint array, int* index, void* value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, ref Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref Point point);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    // ---- COM objects --------------------------------------------------------------------------

    /// <summary>One ComWrappers for every provider: CCWs made by the COM source generator.</summary>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>A new reference to <paramref name="obj"/>'s COM interface <paramref name="iid"/>; 0 for null.</summary>
    public static nint ComPointer(object? obj, in Guid iid)
    {
        if (obj is null) return 0;
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in iid, out nint p) == 0 ? p : 0;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>A new IUnknown reference to <paramref name="obj"/>.</summary>
    public static nint Unknown(object obj) => Wrappers.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);

    /// <summary>The managed provider behind a COM pointer UI Automation handed back (one of ours), or null.</summary>
    public static T? Unwrap<T>(nint p) where T : class =>
        p == 0 ? null : Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.Unwrap) as T;

    /// <summary>A SAFEARRAY of IUnknown holding a reference to each of <paramref name="items"/>.</summary>
    public static nint UnknownArray(IReadOnlyList<object> items)
    {
        nint array = SafeArrayCreateVector(Variant.VT_UNKNOWN, 0, (uint)items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            nint p = Unknown(items[i]);
            SafeArrayPutElement(array, &i, (void*)p); // takes its own reference
            Marshal.Release(p);
        }
        return array;
    }

    public static nint DoubleArray(ReadOnlySpan<double> values)
    {
        nint array = SafeArrayCreateVector(5 /* VT_R8 */, 0, (uint)values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            double v = values[i];
            SafeArrayPutElement(array, &i, &v);
        }
        return array;
    }

    public static nint IntArray(ReadOnlySpan<int> values)
    {
        nint array = SafeArrayCreateVector(Variant.VT_I4, 0, (uint)values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            SafeArrayPutElement(array, &i, &v);
        }
        return array;
    }

    /// <summary>Raises <paramref name="eventId"/> on a provider (call only while clients listen).</summary>
    public static void RaiseEvent(object provider, int eventId)
    {
        nint p = ComPointer(provider, IidSimple);
        if (p == 0) return;
        UiaRaiseAutomationEvent(p, eventId);
        Marshal.Release(p);
    }

    /// <summary>Asks the screen reader to say <paramref name="text"/> on behalf of <paramref name="provider"/>.</summary>
    public static void RaiseNotification(object provider, string text, bool replaceable)
    {
        nint p = ComPointer(provider, IidSimple);
        if (p == 0) return;
        nint display = Marshal.StringToBSTR(text);
        nint activity = Marshal.StringToBSTR(replaceable ? "erikterm.output.latest" : "erikterm.output");
        UiaRaiseNotificationEvent(p, NotificationKindActionCompleted,
            replaceable ? NotificationProcessingMostRecent : NotificationProcessingAll, display, activity);
        Marshal.FreeBSTR(display);
        Marshal.FreeBSTR(activity);
        Marshal.Release(p);
    }

    /// <summary>Tells clients the children of <paramref name="root"/> changed (panes came or went).</summary>
    public static void RaiseChildrenInvalidated(object root)
    {
        nint p = ComPointer(root, IidSimple);
        if (p == 0) return;
        int id = 0; // ChildrenInvalidated takes the parent's runtime id; the root's comes from its host
        UiaRaiseStructureChangedEvent(p, StructureChildrenInvalidated, &id, 0);
        Marshal.Release(p);
    }

    public static void Disconnect(object provider)
    {
        nint p = ComPointer(provider, IidSimple);
        if (p == 0) return;
        UiaDisconnectProvider(p);
        Marshal.Release(p);
    }

    /// <summary>The value an attribute or property nobody supports reads as.</summary>
    public static void SetNotSupported(Variant* v)
    {
        if (UiaGetReservedNotSupportedValue(out nint unknown) == 0) Variant.SetUnknown(v, unknown);
    }
}
