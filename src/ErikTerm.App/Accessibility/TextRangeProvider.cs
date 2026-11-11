using System.Runtime.InteropServices.Marshalling;
using ErikTerm.Core.Accessibility;

namespace ErikTerm.App.Accessibility;

/// <summary>
/// A range of a pane's text (ITextRangeProvider). It holds two points in absolute line numbers, so it stays on its
/// text while output scrolls; every operation is <see cref="TextNavigator"/> over the pane's live rows. Clients may
/// call it from several threads, so the span is read and written under the range's own lock.
/// </summary>
[GeneratedComClass]
internal sealed partial class TextRangeProvider(PaneProvider pane, TextSpan initialSpan) : ITextRangeProvider
{
    private readonly Lock _sync = new();
    private TextSpan _span = initialSpan;

    private TextSpan Span
    {
        get { lock (_sync) return _span; }
        set { lock (_sync) _span = value; }
    }

    private IAccessibleRows Rows => pane.View.Rows;

    private static TextUnit Unit(int unit) => TextNavigator.Supported((TextUnit)unit);

    public nint Clone() => pane.Range(Span);

    public int Compare(nint range) =>
        Uia.Unwrap<TextRangeProvider>(range) is { } other && ReferenceEquals(other.Pane, pane) && other.Span == Span ? 1 : 0;

    public int CompareEndpoints(int endpoint, nint targetRange, int targetEndpoint)
    {
        var other = Other(targetRange);
        return Endpoint(Span, endpoint).CompareTo(Endpoint(other.Span, targetEndpoint)) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    public void ExpandToEnclosingUnit(int unit) => Span = TextNavigator.Expand(Rows, Span, Unit(unit));

    public nint FindAttribute(int attributeId, Variant val, int backward) => 0; // no formatting attributes are exposed

    public nint FindText(string text, int backward, int ignoreCase) =>
        TextNavigator.Find(Rows, Span, text, backward != 0, ignoreCase != 0) is { } found ? pane.Range(found) : 0;

#pragma warning disable S6640 // Native interop: UI Automation passes the VARIANT to fill in by pointer (IDL: VARIANT* pRetVal).
    public unsafe void GetAttributeValue(int attributeId, Variant* pRetVal)
    {
        *pRetVal = default;
        if (attributeId == Uia.IsReadOnlyAttribute) Variant.SetBool(pRetVal, false); // typing goes to the program
        else Uia.SetNotSupported(pRetVal);
    }
#pragma warning restore S6640

    public nint GetBoundingRectangles() => Uia.DoubleArray(pane.Rectangles(TextNavigator.RowSpans(Rows, Span)));

    public nint GetEnclosingElement() => Uia.ComPointer(pane, Uia.IidSimple);

    public string GetText(int maxLength) => TextNavigator.GetText(Rows, Span, maxLength);

    public int Move(int unit, int count)
    {
        Span = TextNavigator.Move(Rows, Span, Unit(unit), count, out int moved);
        return moved;
    }

    public int MoveEndpointByUnit(int endpoint, int unit, int count)
    {
        var rows = Rows;
        var span = Span;
        var p = TextNavigator.MoveEndpoint(rows, Endpoint(span, endpoint), Unit(unit), count, out int moved);
        Span = WithEndpoint(span, endpoint, p);
        return moved;
    }

    public void MoveEndpointByRange(int endpoint, nint targetRange, int targetEndpoint) =>
        Span = WithEndpoint(Span, endpoint, Endpoint(Other(targetRange).Span, targetEndpoint));

    public void Select() => pane.Owner.RequestSelect(pane.PaneId, TextNavigator.Normalize(Rows, Span));

    public void AddToSelection() => throw new InvalidOperationException("A pane has one selection.");

    public void RemoveFromSelection() => throw new InvalidOperationException("A pane has one selection.");

    public void ScrollIntoView(int alignToTop)
    {
        var span = Span;
        pane.Owner.RequestScroll(pane.PaneId, alignToTop != 0 ? span.Start.Line : span.End.Line, alignToTop != 0);
    }

    public nint GetChildren() => Uia.UnknownArray([]);

    // ---- Helpers --------------------------------------------------------------------------------

    private PaneProvider Pane => pane;

    /// <summary>Another range of the same pane, or E_INVALIDARG.</summary>
    private TextRangeProvider Other(nint range) =>
        Uia.Unwrap<TextRangeProvider>(range) is { } other && ReferenceEquals(other.Pane, pane)
            ? other : throw new ArgumentException("The range belongs to another element.");

    private static TextPoint Endpoint(TextSpan span, int endpoint) => endpoint == Uia.EndpointStart ? span.Start : span.End;

    /// <summary>Moves one end; when it passes the other one, the other one comes along (the range collapses there).</summary>
    private static TextSpan WithEndpoint(TextSpan span, int endpoint, TextPoint p)
    {
        if (endpoint == Uia.EndpointStart) return new TextSpan(p, TextPoint.Max(p, span.End));
        return new TextSpan(TextPoint.Min(p, span.Start), p);
    }
}
