using ErikTerm.Ui;

namespace ErikTerm.App.Session;

/// <summary>Converts between a <see cref="LayoutTree{T}"/> and its saved form. Generic so it is testable without real panes.</summary>
internal static class SessionLayout
{
    private const string Vertical = "vertical";

    /// <summary>A corrupt or hand-edited file must not spawn hundreds of shells.</summary>
    public const int MaxPanesPerTab = 32;
    public const int MaxTabs = 64;
    private const double MinRatio = 0.05, MaxRatio = 0.95;

    /// <summary>
    /// The tree's saved form. Leaves <paramref name="skip"/> says aren't saved (ephemeral panes: agent-team teammates)
    /// are left out, their siblings taking the split; if that would leave nothing, everything is saved.
    /// </summary>
    public static SessionNode Capture<T>(LayoutTree<T> tree, Func<T, SessionPane> pane, Func<T, bool>? skip = null) where T : class =>
        (skip is null ? null : Capture(tree.Root, pane, skip)) ?? Capture(tree.Root, pane, null)!;

    private static SessionNode? Capture<T>(LayoutTree<T>.Node n, Func<T, SessionPane> pane, Func<T, bool>? skip) where T : class
    {
        if (n.IsLeaf) return skip?.Invoke(n.Pane!) == true ? null : new SessionNode { Pane = pane(n.Pane!) };
        var first = Capture(n.First!, pane, skip);
        var second = Capture(n.Second!, pane, skip);
        if (first is null || second is null) return first ?? second;
        return new SessionNode
        {
            Axis = n.Axis == SplitAxis.Horizontal ? "horizontal" : Vertical,
            Ratio = Math.Round(ClampRatio(n.Ratio), 4),
            First = first,
            Second = second,
        };
    }

    /// <summary>Index of <paramref name="pane"/> among the tree's leaves (depth-first, first child first; not counting skipped ones), or -1.</summary>
    public static int LeafIndex<T>(LayoutTree<T> tree, T? pane, Func<T, bool>? skip = null) where T : class
    {
        if (pane is null) return -1;
        int i = 0;
        foreach (var leaf in tree.Leaves())
        {
            if (skip?.Invoke(leaf) == true)
            {
                if (ReferenceEquals(leaf, pane)) return -1;
                continue;
            }
            if (ReferenceEquals(leaf, pane)) return i;
            i++;
        }
        return -1;
    }

    /// <summary>The leaves in depth-first order, matching <see cref="LayoutTree{T}.Leaves"/>.</summary>
    public static List<SessionPane> Leaves(SessionNode root)
    {
        var list = new List<SessionPane>();
        Collect(root, list);
        return list;

        static void Collect(SessionNode n, List<SessionPane> list)
        {
            if (n.IsSplit) { Collect(n.First!, list); Collect(n.Second!, list); }
            else list.Add(n.Pane ?? new SessionPane());
        }
    }

    public static int CountLeaves(SessionNode n) => n.IsSplit ? CountLeaves(n.First!) + CountLeaves(n.Second!) : 1;

    /// <summary>
    /// Makes a loaded tree safe to rebuild: a node without both children becomes a leaf, ratios are clamped,
    /// unknown axes fall back to horizontal, and subtrees past <paramref name="maxPanes"/> leaves are cut off.
    /// </summary>
    public static SessionNode Sanitize(SessionNode? n, int maxPanes = MaxPanesPerTab)
    {
        int budget = Math.Max(1, maxPanes);
        return Walk(n, ref budget, 0);

        static SessionNode Walk(SessionNode? n, ref int budget, int depth)
        {
            if (n is null || !n.IsSplit || budget < 2 || depth > 24)
            {
                budget--;
                return new SessionNode { Pane = n is null ? new SessionPane() : n.Pane ?? FirstPane(n) ?? new SessionPane() };
            }
            budget--; // keep one leaf for the second subtree
            var first = Walk(n.First, ref budget, depth + 1);
            budget++;
            var second = Walk(n.Second, ref budget, depth + 1);
            return new SessionNode
            {
                Axis = string.Equals(n.Axis, Vertical, StringComparison.OrdinalIgnoreCase) ? Vertical : "horizontal",
                Ratio = ClampRatio(n.Ratio ?? 0.5),
                First = first,
                Second = second,
            };
        }

        static SessionPane? FirstPane(SessionNode n) => n.Pane ?? (n.First is { } f ? FirstPane(f) : null);
    }

    private static double ClampRatio(double r) => double.IsFinite(r) ? Math.Clamp(r, MinRatio, MaxRatio) : 0.5;

    public static SplitAxis AxisOf(SessionNode n) =>
        string.Equals(n.Axis, Vertical, StringComparison.OrdinalIgnoreCase) ? SplitAxis.Vertical : SplitAxis.Horizontal;

    /// <summary>Each leaf's rectangle (same order as <see cref="Leaves"/>) — so panes can be spawned at their final size.</summary>
    public static List<RectI> LeafRects(SessionNode root, RectI bounds, int gap)
    {
        var list = new List<RectI>();
        Walk(root, bounds);
        return list;

        void Walk(SessionNode n, RectI r)
        {
            if (!n.IsSplit) { list.Add(r); return; }
            var (a, _, b) = LayoutTree<object>.SplitRect(r, AxisOf(n), n.Ratio ?? 0.5, gap);
            Walk(n.First!, a);
            Walk(n.Second!, b);
        }
    }

    /// <summary>
    /// Rebuilds the tree from <paramref name="root"/> with <paramref name="leaves"/> as its panes, in
    /// <see cref="Leaves"/> order (there must be exactly <see cref="CountLeaves"/> of them).
    /// </summary>
    public static LayoutTree<T> Rebuild<T>(SessionNode root, IReadOnlyList<T> leaves) where T : class
    {
        if (leaves.Count != CountLeaves(root)) throw new ArgumentException("leaf count doesn't match the layout", nameof(leaves));
        var tree = new LayoutTree<T>(leaves[0]);
        int i = 0;
        Build(root, tree, leaves, ref i);
        return tree;

        // Invariant: leaves[i] is in the tree, holding the whole area of n.
        static void Build(SessionNode n, LayoutTree<T> tree, IReadOnlyList<T> leaves, ref int i)
        {
            if (!n.IsSplit) { i++; return; }
            int second = i + CountLeaves(n.First!);
            tree.Split(leaves[i], leaves[second], AxisOf(n), ratio: n.Ratio ?? 0.5);
            Build(n.First!, tree, leaves, ref i);
            Build(n.Second!, tree, leaves, ref i);
        }
    }
}
