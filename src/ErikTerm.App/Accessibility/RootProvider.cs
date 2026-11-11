using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ErikTerm.App.Accessibility;

/// <summary>
/// The window's element, the root of its fragment tree. What the window itself is (its control type, its rectangle)
/// comes from the HWND's host provider; this adds the name ("erikterm — tab title") and the panes as children.
/// </summary>
[GeneratedComClass]
internal sealed partial class RootProvider(WindowAccessibility owner) : IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot
{
    // ---- IRawElementProviderSimple -------------------------------------------------------------

    public int get_ProviderOptions() => Uia.ProviderOptionsServerSide | Uia.ProviderOptionsUseComThreading;

    public nint GetPatternProvider(int patternId) => 0;

#pragma warning disable S6640 // Native interop: UI Automation passes the VARIANT to fill in by pointer (IDL: VARIANT* pRetVal).
    public unsafe void GetPropertyValue(int propertyId, Variant* pRetVal)
    {
        *pRetVal = default;
        switch (propertyId)
        {
            case Uia.NameProperty: Variant.SetString(pRetVal, owner.View.Name); break;
            case Uia.ProviderDescriptionProperty: Variant.SetString(pRetVal, "erikterm window"); break;
            case Uia.FrameworkIdProperty: Variant.SetString(pRetVal, "erikterm"); break;
        }
    }
#pragma warning restore S6640

    public nint get_HostRawElementProvider()
    {
        Marshal.ThrowExceptionForHR(Uia.UiaHostProviderFromHwnd(owner.Hwnd, out nint host));
        return host;
    }

    // ---- IRawElementProviderFragment -----------------------------------------------------------

    public nint Navigate(int direction)
    {
        var panes = owner.View.Panes;
        if (panes.Length == 0) return 0;
        var pane = direction switch
        {
            Uia.NavigateFirstChild => panes[0],
            Uia.NavigateLastChild => panes[^1],
            _ => null, // parent and siblings: the host provider's business
        };
        return pane is null ? 0 : Uia.ComPointer(owner.Provider(pane.Id), Uia.IidFragment);
    }

    /// <summary>A root hosted in an HWND has none of its own: UI Automation takes the window's.</summary>
    public nint GetRuntimeId() => 0;

    /// <summary>Likewise its rectangle: the host provider has the window's.</summary>
    public UiaRect get_BoundingRectangle() => default;

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus()
    {
        // The window is focused by the system; there is nothing in it to focus that isn't a pane.
    }

    public nint get_FragmentRoot() => Uia.ComPointer(this, Uia.IidFragmentRoot);

    // ---- IRawElementProviderFragmentRoot -------------------------------------------------------

    public nint ElementProviderFromPoint(double x, double y)
    {
        var (px, py) = owner.FromScreen(x, y);
        return Array.Find(owner.View.Panes, p => p.Grid.Contains(px, py)) is { } pane
            ? Uia.ComPointer(owner.Provider(pane.Id), Uia.IidFragment)
            : 0; // the window itself
    }

    public nint GetFocus()
    {
        var view = owner.View;
        return view.Focused is { } pane ? Uia.ComPointer(owner.Provider(pane.Id), Uia.IidFragment) : 0;
    }
}
