namespace ErikTerm.Ui;

/// <summary>tmux's preset layouts (select-layout), as split trees.</summary>
public enum LayoutPreset { EvenHorizontal, EvenVertical, MainHorizontal, MainVertical, Tiled }

/// <summary>Rebuilds a <see cref="LayoutTree{T}"/> in one of tmux's layouts, and tmux-style absolute resizes.</summary>
public static class LayoutPresets
{
    public static LayoutPreset? Parse(string name) => name switch
    {
        "even-horizontal" => LayoutPreset.EvenHorizontal,
        "even-vertical" => LayoutPreset.EvenVertical,
        "main-horizontal" => LayoutPreset.MainHorizontal,
        "main-vertical" => LayoutPreset.MainVertical,
        "tiled" => LayoutPreset.Tiled,
        _ => null,
    };

    /// <summary>
    /// Arranges the tree's panes (in their current order) in <paramref name="preset"/>. The main layouts give the
    /// first pane <paramref name="mainShare"/> of the width (main-vertical) or height (main-horizontal) and stack the
    /// rest evenly beside / below it; tiled makes a grid of ⌈√n⌉ columns, filled row by row.
    /// </summary>
    public static void Apply<T>(LayoutTree<T> tree, LayoutPreset preset, double mainShare = 0.5) where T : class
    {
        var panes = tree.Leaves().ToList();
        tree.SetRoot(Build(preset, panes, mainShare));
    }

    public static LayoutTree<T>.Node Build<T>(LayoutPreset preset, IReadOnlyList<T> panes, double mainShare = 0.5) where T : class
    {
        if (panes.Count == 0) throw new ArgumentException("no panes", nameof(panes));
        if (panes.Count == 1) return Leaf(panes[0]);
        switch (preset)
        {
            case LayoutPreset.EvenHorizontal: return Chain(panes, SplitAxis.Horizontal);
            case LayoutPreset.EvenVertical: return Chain(panes, SplitAxis.Vertical);
            case LayoutPreset.MainVertical:
                return Split(SplitAxis.Horizontal, mainShare, Leaf(panes[0]), Chain(panes.Skip(1).ToList(), SplitAxis.Vertical));
            case LayoutPreset.MainHorizontal:
                return Split(SplitAxis.Vertical, mainShare, Leaf(panes[0]), Chain(panes.Skip(1).ToList(), SplitAxis.Horizontal));
            default:
            {
                int columns = (int)Math.Ceiling(Math.Sqrt(panes.Count));
                var rows = new List<LayoutTree<T>.Node>();
                for (int i = 0; i < panes.Count; i += columns)
                    rows.Add(Chain(panes.Skip(i).Take(columns).ToList(), SplitAxis.Horizontal));
                return ChainNodes(rows, SplitAxis.Vertical);
            }
        }
    }

    /// <summary>Every split divides its space in proportion to the panes on each side (tmux select-layout -E).</summary>
    public static void Equalize<T>(LayoutTree<T> tree) where T : class
    {
        Visit(tree.Root);

        static int Visit(LayoutTree<T>.Node n)
        {
            if (n.IsLeaf) return 1;
            int a = Visit(n.First!), b = Visit(n.Second!);
            // Only count panes side by side along this axis: a nested split across the other axis is one column/row.
            int ca = Along(n.First!, n.Axis), cb = Along(n.Second!, n.Axis);
            n.Ratio = ca / (double)(ca + cb);
            return a + b;
        }

        static int Along(LayoutTree<T>.Node n, SplitAxis axis)
        {
            if (n.IsLeaf) return 1;
            int first = Along(n.First!, axis), second = Along(n.Second!, axis);
            return n.Axis == axis ? first + second : Math.Max(first, second);
        }
    }

    /// <summary>
    /// Sets <paramref name="pane"/>'s size along <paramref name="axis"/> to <paramref name="pixels"/> by moving the
    /// nearest divider across that axis (tmux resize-pane -x / -y). Returns false when no split runs that way.
    /// </summary>
    public static bool Resize<T>(LayoutTree<T> tree, T pane, SplitAxis axis, int pixels, RectI bounds, int gap, int minSize) where T : class
    {
        var node = tree.Find(pane);
        for (var n = node; n?.Parent is { } p; n = p)
        {
            if (p.Axis != axis) continue;
            var area = tree.BoundsOf(p, bounds, gap);
            int size = (axis == SplitAxis.Horizontal ? area.W : area.H) - gap;
            if (size <= 0) return false;
            bool first = ReferenceEquals(p.First, n);
            LayoutTree<T>.SetDivider(p, first ? pixels : size - pixels, size, minSize);
            return true;
        }
        return false;
    }

    private static LayoutTree<T>.Node Leaf<T>(T pane) where T : class => new() { Pane = pane };

    private static LayoutTree<T>.Node Split<T>(SplitAxis axis, double ratio, LayoutTree<T>.Node first, LayoutTree<T>.Node second) where T : class =>
        new() { Axis = axis, Ratio = ratio, First = first, Second = second };

    private static LayoutTree<T>.Node Chain<T>(IReadOnlyList<T> panes, SplitAxis axis) where T : class =>
        ChainNodes(panes.Select(Leaf).ToList(), axis);

    /// <summary>n nodes side by side with equal shares: a right-leaning chain whose first child gets 1/k of what's left.</summary>
    private static LayoutTree<T>.Node ChainNodes<T>(IReadOnlyList<LayoutTree<T>.Node> nodes, SplitAxis axis) where T : class
    {
        var result = nodes[^1];
        for (int i = nodes.Count - 2; i >= 0; i--)
            result = Split(axis, 1.0 / (nodes.Count - i), nodes[i], result);
        return result;
    }
}
