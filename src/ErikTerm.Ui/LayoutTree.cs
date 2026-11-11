namespace ErikTerm.Ui;

/// <summary>
/// A leaf that can be hidden: it stays in the tree (its place, its splits, the ratios around it) but takes no room
/// while <see cref="LayoutHidden"/> — its sibling fills the split, and no divider shows. Panes parked as pills
/// (erikterm's agent-team teammates) are such leaves.
/// </summary>
public interface ILayoutLeaf
{
    bool LayoutHidden { get; }
}

/// <summary>
/// Binary split tree of panes. Leaves hold a pane; inner nodes split their rectangle along an axis
/// at a ratio, leaving a <c>gap</c>-pixel divider between the two children.
/// </summary>
public sealed class LayoutTree<T> where T : class
{
    public sealed class Node
    {
        public Node? Parent { get; set; }
        public T? Pane { get; set; }            // leaf when non-null
        public SplitAxis Axis { get; set; }
        public double Ratio { get; set; } = 0.5; // share of the first child
        public Node? First { get; set; }
        public Node? Second { get; set; }
        public bool IsLeaf => Pane is not null;
    }

    /// <summary>A divider between two children of <see cref="Split"/>; <see cref="Bounds"/> is the split's whole area.</summary>
    public readonly record struct Divider(Node Split, RectI Rect, RectI Bounds);

    public Node Root { get; private set; }

    public LayoutTree(T first) => Root = new Node { Pane = first };

    public int Count => LeafList.Count;

    private List<T>? _leafList;

    /// <summary>
    /// The leaves in order, cached until the tree changes (no allocation per call; iterate it with foreach on the
    /// List for a struct enumerator). Changes build a new list, so iterating while splitting/removing is safe.
    /// Don't modify it.
    /// </summary>
    public List<T> LeafList => _leafList ??= [.. Leaves()];

    public bool Contains(T pane) => LeafList.Contains(pane);

    /// <summary>Panes changed places between leaves (<see cref="LayoutMoves"/>): the leaf list is built again.</summary>
    internal void PanesMoved() => _leafList = null;

    /// <summary>Replaces the whole tree (LayoutPresets builds one); parent links are set here.</summary>
    public void SetRoot(Node root)
    {
        root.Parent = null;
        Link(root);
        Root = root;
        _leafList = null;

        static void Link(Node n)
        {
            if (n.IsLeaf) return;
            n.First!.Parent = n;
            n.Second!.Parent = n;
            Link(n.First);
            Link(n.Second);
        }
    }


    public IEnumerable<T> Leaves()
    {
        var stack = new Stack<Node>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsLeaf) { yield return n.Pane!; continue; }
            stack.Push(n.Second!);
            stack.Push(n.First!);
        }
    }

    public Node? Find(T pane)
    {
        var stack = new Stack<Node>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsLeaf)
            {
                if (ReferenceEquals(n.Pane, pane)) return n;
                continue;
            }
            stack.Push(n.Second!);
            stack.Push(n.First!);
        }
        return null;
    }

    /// <summary>Splits <paramref name="target"/>'s area; the new pane goes right (Horizontal) or below (Vertical), or before it when <paramref name="newFirst"/>.</summary>
    public void Split(T target, T newPane, SplitAxis axis, bool newFirst = false, double ratio = 0.5)
    {
        var leaf = Find(target) ?? throw new ArgumentException("pane is not in this layout", nameof(target));
        _leafList = null;
        var existing = new Node { Pane = leaf.Pane };
        var added = new Node { Pane = newPane };
        leaf.Pane = null;
        leaf.Axis = axis;
        leaf.Ratio = newFirst ? 1 - ratio : ratio;
        leaf.First = newFirst ? added : existing;
        leaf.Second = newFirst ? existing : added;
        existing.Parent = leaf;
        added.Parent = leaf;
    }

    /// <summary>Removes a pane; its sibling takes the space. Returns the pane that should get focus, or null if the layout is now empty.</summary>
    public T? Remove(T pane)
    {
        var leaf = Find(pane) ?? throw new ArgumentException("pane is not in this layout", nameof(pane));
        var parent = leaf.Parent;
        if (parent is null) return null;
        _leafList = null;

        var sibling = ReferenceEquals(parent.First, leaf) ? parent.Second! : parent.First!;
        // Hoist the sibling into the parent's slot.
        parent.Pane = sibling.Pane;
        parent.Axis = sibling.Axis;
        parent.Ratio = sibling.Ratio;
        parent.First = sibling.First;
        parent.Second = sibling.Second;
        if (parent.First is not null) parent.First.Parent = parent;
        if (parent.Second is not null) parent.Second.Parent = parent;

        // Focus the pane nearest to where the removed one was.
        var n = parent;
        bool removedWasFirst = ReferenceEquals(sibling, parent.Second); // sibling was second → removed was first
        while (!n.IsLeaf) n = removedWasFirst ? n.First! : n.Second!;
        return n.Pane;
    }

    /// <summary>
    /// The rectangles of the shown panes (hidden leaves take no room: see <see cref="ILayoutLeaf"/>), or with
    /// <paramref name="includeHidden"/> of every pane as if none were hidden — where a hidden pane would be.
    /// </summary>
    public void Layout(RectI bounds, int gap, List<(T Pane, RectI Rect)> panes, List<Divider>? dividers = null, bool includeHidden = false)
    {
        panes.Clear();
        dividers?.Clear();
        Walk(Root, bounds, gap, panes, dividers, includeHidden);
    }

    private static void Walk(Node n, RectI r, int gap, List<(T, RectI)> panes, List<Divider>? dividers, bool all)
    {
        if (n.IsLeaf)
        {
            if (all || !IsHidden(n.Pane!)) panes.Add((n.Pane!, r));
            return;
        }
        if (!all)
        {
            bool a1 = Shows(n.First!), b1 = Shows(n.Second!);
            if (!a1 || !b1)
            {
                if (a1) Walk(n.First!, r, gap, panes, dividers, all);
                else if (b1) Walk(n.Second!, r, gap, panes, dividers, all);
                return;
            }
        }
        var (a, d, b) = SplitRect(r, n.Axis, n.Ratio, gap);
        dividers?.Add(new Divider(n, d, r));
        Walk(n.First!, a, gap, panes, dividers, all);
        Walk(n.Second!, b, gap, panes, dividers, all);
    }

    private static bool IsHidden(T pane) => pane is ILayoutLeaf { LayoutHidden: true };

    /// <summary>Whether any leaf of the subtree is shown (takes room).</summary>
    public static bool Shows(Node n) => n.IsLeaf ? !IsHidden(n.Pane!) : Shows(n.First!) || Shows(n.Second!);

    /// <summary>The smallest subtree holding all of <paramref name="panes"/> (null when one isn't in the tree).</summary>
    public Node? CommonAncestor(IReadOnlyList<T> panes)
    {
        Node? common = null;
        foreach (var p in panes)
        {
            var n = Find(p);
            if (n is null) return null;
            if (common is null) { common = n; continue; }
            while (!IsWithin(n, common)) common = common.Parent!;
        }
        return common;

        static bool IsWithin(Node n, Node ancestor)
        {
            for (var x = n; x is not null; x = x.Parent) if (ReferenceEquals(x, ancestor)) return true;
            return false;
        }
    }

    /// <summary>Puts <paramref name="replacement"/> where <paramref name="node"/> was (LayoutPresets on part of the tree).</summary>
    public void Replace(Node node, Node replacement)
    {
        if (node.Parent is not { } parent) { SetRoot(replacement); return; }
        if (ReferenceEquals(parent.First, node)) parent.First = replacement; else parent.Second = replacement;
        SetRoot(Root); // relinks parents
    }

    public static (RectI First, RectI Divider, RectI Second) SplitRect(RectI r, SplitAxis axis, double ratio, int gap)
    {
        if (axis == SplitAxis.Horizontal)
        {
            int avail = Math.Max(0, r.W - gap);
            int w1 = (int)Math.Round(avail * Math.Clamp(ratio, 0, 1));
            return (r with { W = w1 }, r with { X = r.X + w1, W = gap }, r with { X = r.X + w1 + gap, W = avail - w1 });
        }
        else
        {
            int avail = Math.Max(0, r.H - gap);
            int h1 = (int)Math.Round(avail * Math.Clamp(ratio, 0, 1));
            return (r with { H = h1 }, r with { Y = r.Y + h1, H = gap }, r with { Y = r.Y + h1 + gap, H = avail - h1 });
        }
    }

    /// <summary>The pane adjacent to <paramref name="from"/> in a direction: overlapping on the other axis, nearest edge, most overlap.</summary>
    public T? Neighbor(T from, Direction dir, RectI bounds, int gap)
    {
        var panes = new List<(T Pane, RectI Rect)>();
        Layout(bounds, gap, panes);
        var src = panes.FirstOrDefault(p => ReferenceEquals(p.Pane, from));
        if (src.Pane is null) return null;
        var s = src.Rect;

        T? best = null;
        int bestDistance = int.MaxValue, bestOverlap = 0;
        foreach (var (pane, r) in panes)
        {
            if (ReferenceEquals(pane, from)) continue;
            int distance, overlap;
            switch (dir)
            {
                case Direction.Left: distance = s.X - r.Right; overlap = Overlap(s.Y, s.Bottom, r.Y, r.Bottom); break;
                case Direction.Right: distance = r.X - s.Right; overlap = Overlap(s.Y, s.Bottom, r.Y, r.Bottom); break;
                case Direction.Up: distance = s.Y - r.Bottom; overlap = Overlap(s.X, s.Right, r.X, r.Right); break;
                default: distance = r.Y - s.Bottom; overlap = Overlap(s.X, s.Right, r.X, r.Right); break;
            }
            if (distance < 0 || overlap <= 0) continue;
            if (distance < bestDistance || (distance == bestDistance && overlap > bestOverlap))
            {
                best = pane;
                bestDistance = distance;
                bestOverlap = overlap;
            }
        }
        return best;
    }

    private static int Overlap(int a0, int a1, int b0, int b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

    /// <summary>
    /// Moves the nearest divider on <paramref name="pane"/>'s side in <paramref name="dir"/> by
    /// <paramref name="pixels"/> (wezterm's AdjustPaneSize). Returns false when there is no such divider.
    /// </summary>
    public bool Adjust(T pane, Direction dir, int pixels, RectI bounds, int gap, int minSize)
    {
        var axis = dir is Direction.Left or Direction.Right ? SplitAxis.Horizontal : SplitAxis.Vertical;
        var target = DividerToAdjust(Find(pane), axis, dir);
        if (target is null) return false;

        var area = BoundsOf(target, bounds, gap);
        int size = (axis == SplitAxis.Horizontal ? area.W : area.H) - gap;
        if (size <= 0) return false;
        int delta = dir is Direction.Right or Direction.Down ? pixels : -pixels;
        SetDivider(target, (int)Math.Round(target.Ratio * size) + delta, size, minSize);
        return true;
    }

    /// <summary>The divider on the side being pushed toward; failing that, any divider on this axis that's on screen.</summary>
    private static Node? DividerToAdjust(Node? node, SplitAxis axis, Direction dir)
    {
        Node? target = null;
        for (var n = node; n?.Parent is { } p; n = p)
        {
            if (p.Axis != axis) continue;
            bool isFirst = ReferenceEquals(p.First, n);
            if (!Shows(isFirst ? p.Second! : p.First!)) continue; // a divider that isn't on screen
            bool sideMatches = dir is Direction.Right or Direction.Down ? isFirst : !isFirst;
            target ??= p;
            if (sideMatches) return p;
        }
        return target;
    }

    /// <summary>Places a split's divider at <paramref name="offset"/> pixels into its area, keeping both sides ≥ minSize.</summary>
    public static void SetDivider(Node split, int offset, int size, int minSize)
    {
        int lo = Math.Min(minSize, size / 2), hi = Math.Max(size - minSize, size / 2);
        split.Ratio = Math.Clamp(offset, lo, hi) / (double)size;
    }

    public RectI BoundsOf(Node target, RectI bounds, int gap)
    {
        var path = new Stack<Node>();
        for (var n = target; n.Parent is { } p; n = p) path.Push(n);
        var r = bounds;
        var cur = Root;
        while (path.Count > 0)
        {
            var next = path.Pop();
            var other = ReferenceEquals(cur.First, next) ? cur.Second! : cur.First!;
            if (Shows(other)) // a hidden sibling takes no room (see Layout)
            {
                var (a, _, b) = SplitRect(r, cur.Axis, cur.Ratio, gap);
                r = ReferenceEquals(cur.First, next) ? a : b;
            }
            cur = next;
        }
        return r;
    }
}
