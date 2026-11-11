using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ErikTerm.Core.Accessibility;

namespace ErikTerm.App.Accessibility;

/// <summary>
/// A pane's element: an edit control holding the terminal's text (scrollback and screen) through the text pattern,
/// focusable, focused when the pane has the keyboard. Windows Terminal's control is a "Text" element named
/// "TermControl", which screen readers recognise by that name and treat specially; erikterm doesn't pretend to be it,
/// and as an edit control screen readers follow the caret and read what it moves over.
/// </summary>
[GeneratedComClass]
internal sealed partial class PaneProvider(WindowAccessibility owner, int paneId) : IRawElementProviderSimple, IRawElementProviderFragment, ITextProvider2
{
    public int PaneId => paneId;

    public WindowAccessibility Owner => owner;

    /// <summary>The pane as published, or UIA_E_ELEMENTNOTAVAILABLE when it closed or left the screen.</summary>
    public PaneView View => owner.PaneView(paneId) ?? throw new COMException("The pane is gone.", Uia.ElementNotAvailable);

    // ---- IRawElementProviderSimple -------------------------------------------------------------

    public int get_ProviderOptions() => Uia.ProviderOptionsServerSide | Uia.ProviderOptionsUseComThreading;

    public nint GetPatternProvider(int patternId) =>
        patternId is Uia.TextPattern or Uia.TextPattern2 ? Uia.Unknown(this) : 0;

#pragma warning disable S6640 // Native interop: UI Automation passes the VARIANT to fill in by pointer (IDL: VARIANT* pRetVal).
    public unsafe void GetPropertyValue(int propertyId, Variant* pRetVal)
    {
        *pRetVal = default;
        var pane = View;
        switch (propertyId)
        {
            case Uia.ControlTypeProperty: Variant.SetInt(pRetVal, Uia.EditControlType); break;
            case Uia.LocalizedControlTypeProperty: Variant.SetString(pRetVal, "terminal"); break;
            case Uia.NameProperty: Variant.SetString(pRetVal, pane.Name); break;
            case Uia.AutomationIdProperty: Variant.SetString(pRetVal, $"pane-{paneId}"); break;
            case Uia.ClassNameProperty: Variant.SetString(pRetVal, "ErikTermPane"); break;
            case Uia.FrameworkIdProperty: Variant.SetString(pRetVal, "erikterm"); break;
            case Uia.ProviderDescriptionProperty: Variant.SetString(pRetVal, "erikterm pane"); break;
            case Uia.HasKeyboardFocusProperty: Variant.SetBool(pRetVal, pane.Focused && owner.View.WindowFocused); break;
            case Uia.IsKeyboardFocusableProperty or Uia.IsEnabledProperty or Uia.IsControlElementProperty
                or Uia.IsContentElementProperty or Uia.IsTextPatternAvailableProperty:
                Variant.SetBool(pRetVal, true);
                break;
            case Uia.IsPasswordProperty or Uia.IsOffscreenProperty: Variant.SetBool(pRetVal, false); break;
        }
    }
#pragma warning restore S6640

    public nint get_HostRawElementProvider() => 0;

    // ---- IRawElementProviderFragment -----------------------------------------------------------

    public nint Navigate(int direction)
    {
        if (direction == Uia.NavigateParent) return Uia.ComPointer(owner.Root, Uia.IidFragment);
        if (direction is not (Uia.NavigateNextSibling or Uia.NavigatePreviousSibling)) return 0; // no children
        var panes = owner.View.Panes;
        int self = Array.FindIndex(panes, p => p.Id == paneId);
        if (self < 0) return 0;
        int i = self + (direction == Uia.NavigateNextSibling ? 1 : -1);
        return i >= 0 && i < panes.Length ? Uia.ComPointer(owner.Provider(panes[i].Id), Uia.IidFragment) : 0;
    }

    public nint GetRuntimeId() => Uia.IntArray([Uia.AppendRuntimeId, paneId]);

    public UiaRect get_BoundingRectangle()
    {
        var g = View.Grid;
        return owner.ToScreen(g.X, g.Y, g.W, g.H);
    }

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() => owner.RequestFocus(paneId);

    public nint get_FragmentRoot() => Uia.ComPointer(owner.Root, Uia.IidFragmentRoot);

    // ---- ITextProvider / ITextProvider2 --------------------------------------------------------

    /// <summary>erikterm's selection, or the caret (a degenerate range) when nothing is selected.</summary>
    public nint GetSelection() => Uia.UnknownArray([new TextRangeProvider(this, SelectionOrCaret())]);

    public nint GetVisibleRanges()
    {
        var pane = View;
        TextSpan span;
        lock (owner.Gate)
        {
            var t = pane.Rows.Terminal;
            span = new TextSpan(new TextPoint(t.ViewTopLine, 0), new TextPoint(t.ViewTopLine + t.Rows, 0));
        }
        return Uia.UnknownArray([new TextRangeProvider(this, TextNavigator.Normalize(pane.Rows, span))]);
    }

    public nint RangeFromChild(nint childElement) => throw new ArgumentException("A pane has no child elements.");

    public nint RangeFromPoint(UiaPoint point)
    {
        var pane = View;
        var (x, y) = owner.FromScreen(point.X, point.Y);
        var view = owner.View;
        TextPoint p;
        lock (owner.Gate)
        {
            var t = pane.Rows.Terminal;
            int col = Math.Clamp((int)MathF.Floor((x - pane.Grid.X) / view.CellWidth), 0, t.Columns - 1);
            int row = Math.Clamp((int)MathF.Floor((y - pane.Grid.Y) / view.CellHeight), 0, t.Rows - 1);
            p = new TextPoint(t.ViewTopLine + row, col);
        }
        p = TextNavigator.Normalize(pane.Rows, p);
        return Range(new TextSpan(p, p));
    }

    public nint get_DocumentRange() => Range(TextNavigator.Document(View.Rows));

    public int get_SupportedTextSelection() => Uia.SupportedTextSelectionSingle;

    public nint RangeFromAnnotation(nint annotationElement) => throw new ArgumentException("A pane has no annotations.");

    public nint GetCaretRange(out int isActive)
    {
        isActive = View.Focused && owner.View.WindowFocused ? 1 : 0;
        var caret = Caret();
        return Range(new TextSpan(caret, caret));
    }

    // ---- For the ranges ------------------------------------------------------------------------

    /// <summary>A new ITextRangeProvider reference over <paramref name="span"/>.</summary>
    public nint Range(TextSpan span) => Uia.ComPointer(new TextRangeProvider(this, span), Uia.IidTextRange);

    public TextPoint Caret()
    {
        var pane = View;
        TextPoint p;
        lock (owner.Gate)
        {
            var t = pane.Rows.Terminal;
            p = new TextPoint(t.ScreenTopLine + t.CursorY, Math.Min(t.CursorX, t.Columns - 1));
        }
        return TextNavigator.Normalize(pane.Rows, p);
    }

    private TextSpan SelectionOrCaret()
    {
        var pane = View;
        (long, int, long, int)? range;
        lock (owner.Gate) range = pane.Pane.Selection?.Range(pane.Rows.Terminal);
        if (range is not var (sl, sc, el, ec))
        {
            var caret = Caret();
            return new TextSpan(caret, caret);
        }
        // Inclusive cells to an exclusive end; a block selection reads as the stream from its first to its last cell.
        return TextNavigator.Normalize(pane.Rows, new TextSpan(new TextPoint(sl, sc), new TextPoint(el, ec + 1)));
    }

    /// <summary>A range's rows as screen rectangles: left, top, width, height for each row on screen.</summary>
    public double[] Rectangles(List<RowSpan> spans)
    {
        var pane = View;
        var view = owner.View;
        long top;
        int rows;
        lock (owner.Gate) (top, rows) = (pane.Rows.Terminal.ViewTopLine, pane.Rows.Terminal.Rows);
        var result = new List<double>(spans.Count * 4);
        foreach (var s in spans)
        {
            if (s.Line < top || s.Line >= top + rows) continue;
            var r = owner.ToScreen(pane.Grid.X + s.StartCol * view.CellWidth, pane.Grid.Y + (s.Line - top) * view.CellHeight,
                (s.EndCol - s.StartCol) * view.CellWidth, view.CellHeight);
            result.AddRange([r.Left, r.Top, r.Width, r.Height]);
        }
        return [.. result];
    }
}
