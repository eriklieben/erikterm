namespace ErikTerm.Ui;

/// <summary>
/// Rearranging a <see cref="LayoutTree{T}"/>'s panes without changing which panes there are: swap, rotate, equalize,
/// move to an edge, grow. Built on the tree's public structure (its nodes, bounds and dividers).
/// </summary>
public static class LayoutMoves
{
    /// <summary>Exchanges two panes' places (the splits stay as they are). False when either isn't in the tree.</summary>
    public static bool Swap<T>(this LayoutTree<T> tree, T a, T b) where T : class
    {
        if (ReferenceEquals(a, b)) return false;
        if (tree.Find(a) is not { } na || tree.Find(b) is not { } nb) return false;
        (na.Pane, nb.Pane) = (nb.Pane, na.Pane);
        tree.PanesMoved();
        return true;
    }

    /// <summary>
    /// Swaps <paramref name="pane"/> with the next pane in order (<paramref name="offset"/> -1: the previous one),
    /// wrapping around — vim's Ctrl-w x. Returns the pane it swapped with, or null with a single pane.
    /// </summary>
    public static T? SwapWithNext<T>(this LayoutTree<T> tree, T pane, int offset = 1) where T : class
    {
        var leaves = tree.LeafList;
        int i = leaves.IndexOf(pane);
        if (i < 0 || leaves.Count < 2) return null;
        int n = leaves.Count;
        var other = leaves[((i + offset) % n + n) % n];
        tree.Swap(pane, other);
        return other;
    }

    /// <summary>
    /// Moves every pane one place along the leaf order, keeping the splits: clockwise the last pane goes first and the
    /// rest shift on (vim's Ctrl-w r, tmux rotate-window -D); counter-clockwise the other way (Ctrl-w R).
    /// </summary>
    public static bool Rotate<T>(this LayoutTree<T> tree, bool clockwise) where T : class
    {
        var leaves = tree.LeafList;
        int n = leaves.Count;
        if (n < 2) return false;
        var nodes = new List<LayoutTree<T>.Node>(n);
        Collect(tree.Root, nodes);
        var panes = nodes.Select(x => x.Pane!).ToList();
        for (int i = 0; i < n; i++) nodes[i].Pane = panes[((i + (clockwise ? -1 : 1)) % n + n) % n];
        tree.PanesMoved();
        return true;

        static void Collect(LayoutTree<T>.Node node, List<LayoutTree<T>.Node> into)
        {
            if (node.IsLeaf) { into.Add(node); return; }
            Collect(node.First!, into);
            Collect(node.Second!, into);
        }
    }

    /// <summary>Every split shares its space in proportion to the panes side by side in it (vim's Ctrl-w =).</summary>
    public static void Equalize<T>(this LayoutTree<T> tree) where T : class => LayoutPresets.Equalize(tree);

    /// <summary>
    /// Moves <paramref name="pane"/> to an edge of the whole layout, spanning it (vim's Ctrl-w H / J / K / L): it is
    /// taken out (its sibling fills the gap) and the tree is split again at the root, the pane getting about 1/n of
    /// the room. False with a single pane.
    /// </summary>
    public static bool MoveToEdge<T>(this LayoutTree<T> tree, T pane, Direction edge) where T : class
    {
        if (tree.Count < 2 || tree.Find(pane) is null) return false;
        int n = tree.Count;
        tree.Remove(pane);
        var old = tree.Root;
        var leaf = new LayoutTree<T>.Node { Pane = pane };
        double share = Math.Clamp(1.0 / n, 0.25, 0.5);
        var root = edge switch
        {
            Direction.Left => new LayoutTree<T>.Node { Axis = SplitAxis.Horizontal, Ratio = share, First = leaf, Second = old },
            Direction.Right => new LayoutTree<T>.Node { Axis = SplitAxis.Horizontal, Ratio = 1 - share, First = old, Second = leaf },
            Direction.Up => new LayoutTree<T>.Node { Axis = SplitAxis.Vertical, Ratio = share, First = leaf, Second = old },
            _ => new LayoutTree<T>.Node { Axis = SplitAxis.Vertical, Ratio = 1 - share, First = old, Second = leaf },
        };
        tree.SetRoot(root);
        return true;
    }

    /// <summary>
    /// Makes <paramref name="pane"/> <paramref name="pixels"/> wider (<see cref="SplitAxis.Horizontal"/>) or taller
    /// (negative: narrower / shorter) by moving the nearest divider on that axis — vim's Ctrl-w &gt; &lt; + -, which
    /// grow the current window whichever side its divider is on. False when no split on that axis holds the pane.
    /// </summary>
    public static bool Grow<T>(this LayoutTree<T> tree, T pane, SplitAxis axis, int pixels, RectI bounds, int gap, int minSize) where T : class
    {
        for (var n = tree.Find(pane); n?.Parent is { } p; n = p)
        {
            if (p.Axis != axis || !LayoutTree<T>.Shows(ReferenceEquals(p.First, n) ? p.Second! : p.First!)) continue;
            var area = tree.BoundsOf(p, bounds, gap);
            int size = (axis == SplitAxis.Horizontal ? area.W : area.H) - gap;
            if (size <= 0) return false;
            int delta = ReferenceEquals(p.First, n) ? pixels : -pixels; // the first side grows as its divider moves on
            LayoutTree<T>.SetDivider(p, (int)Math.Round(p.Ratio * size) + delta, size, minSize);
            return true;
        }
        return false;
    }
}
