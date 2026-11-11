namespace ErikTerm.App.Rendering;

/// <summary>
/// Draws box-drawing (U+2500–257F), block elements (U+2580–259F), the black triangles U+25E2–25E5,
/// braille (U+2800–28FF) and the powerline separators (U+E0B0–E0BF) procedurally, as coverage
/// bitmaps at the exact cell size. Font glyphs for these rarely fill the cell — with line_height &gt; 1
/// they leave gaps between rows — so like kitty and wezterm we draw them ourselves.
/// Straight strokes are pixel-snapped (crisp, and identical in every cell so lines join); curves,
/// diagonals and triangles are anti-aliased by supersampling. No dependencies: unit-tested directly.
/// </summary>
internal static class BoxDrawing
{
    // Arm weights.
    public const int None = 0, Light = 1, Heavy = 2, Double = 3;

    public static bool Handles(int cp) =>
        cp is (>= 0x2500 and <= 0x259F) or (>= 0x25E2 and <= 0x25E5) or (>= 0x2800 and <= 0x28FF) or (>= 0xE0B0 and <= 0xE0BF);

    private static byte PackArms(int l, int r, int u, int d) => (byte)(l | r << 2 | u << 4 | d << 6);

    /// <summary>Arms of the plain line characters, packed as l | r&lt;&lt;2 | u&lt;&lt;4 | d&lt;&lt;6; 0 for the special ones.</summary>
    private static readonly byte[] ArmTable = BuildArms();

    private static byte[] BuildArms()
    {
        const int L = Light, H = Heavy, D = Double;
        var t = new byte[0x80];
        void S(int cp, int l, int r, int u, int d) => t[cp - 0x2500] = PackArms(l, r, u, d);
        S(0x2500, L, L, 0, 0); S(0x2501, H, H, 0, 0); S(0x2502, 0, 0, L, L); S(0x2503, 0, 0, H, H);
        // 2504–250B: dashed, drawn separately
        S(0x250C, 0, L, 0, L); S(0x250D, 0, H, 0, L); S(0x250E, 0, L, 0, H); S(0x250F, 0, H, 0, H);
        S(0x2510, L, 0, 0, L); S(0x2511, H, 0, 0, L); S(0x2512, L, 0, 0, H); S(0x2513, H, 0, 0, H);
        S(0x2514, 0, L, L, 0); S(0x2515, 0, H, L, 0); S(0x2516, 0, L, H, 0); S(0x2517, 0, H, H, 0);
        S(0x2518, L, 0, L, 0); S(0x2519, H, 0, L, 0); S(0x251A, L, 0, H, 0); S(0x251B, H, 0, H, 0);
        S(0x251C, 0, L, L, L); S(0x251D, 0, H, L, L); S(0x251E, 0, L, H, L); S(0x251F, 0, L, L, H);
        S(0x2520, 0, L, H, H); S(0x2521, 0, H, H, L); S(0x2522, 0, H, L, H); S(0x2523, 0, H, H, H);
        S(0x2524, L, 0, L, L); S(0x2525, H, 0, L, L); S(0x2526, L, 0, H, L); S(0x2527, L, 0, L, H);
        S(0x2528, L, 0, H, H); S(0x2529, H, 0, H, L); S(0x252A, H, 0, L, H); S(0x252B, H, 0, H, H);
        S(0x252C, L, L, 0, L); S(0x252D, H, L, 0, L); S(0x252E, L, H, 0, L); S(0x252F, H, H, 0, L);
        S(0x2530, L, L, 0, H); S(0x2531, H, L, 0, H); S(0x2532, L, H, 0, H); S(0x2533, H, H, 0, H);
        S(0x2534, L, L, L, 0); S(0x2535, H, L, L, 0); S(0x2536, L, H, L, 0); S(0x2537, H, H, L, 0);
        S(0x2538, L, L, H, 0); S(0x2539, H, L, H, 0); S(0x253A, L, H, H, 0); S(0x253B, H, H, H, 0);
        S(0x253C, L, L, L, L); S(0x253D, H, L, L, L); S(0x253E, L, H, L, L); S(0x253F, H, H, L, L);
        S(0x2540, L, L, H, L); S(0x2541, L, L, L, H); S(0x2542, L, L, H, H); S(0x2543, H, L, H, L);
        S(0x2544, L, H, H, L); S(0x2545, H, L, L, H); S(0x2546, L, H, L, H); S(0x2547, H, H, H, L);
        S(0x2548, H, H, L, H); S(0x2549, H, L, H, H); S(0x254A, L, H, H, H); S(0x254B, H, H, H, H);
        // 254C–254F: dashed
        S(0x2550, D, D, 0, 0); S(0x2551, 0, 0, D, D);
        S(0x2552, 0, D, 0, L); S(0x2553, 0, L, 0, D); S(0x2554, 0, D, 0, D);
        S(0x2555, D, 0, 0, L); S(0x2556, L, 0, 0, D); S(0x2557, D, 0, 0, D);
        S(0x2558, 0, D, L, 0); S(0x2559, 0, L, D, 0); S(0x255A, 0, D, D, 0);
        S(0x255B, D, 0, L, 0); S(0x255C, L, 0, D, 0); S(0x255D, D, 0, D, 0);
        S(0x255E, 0, D, L, L); S(0x255F, 0, L, D, D); S(0x2560, 0, D, D, D);
        S(0x2561, D, 0, L, L); S(0x2562, L, 0, D, D); S(0x2563, D, 0, D, D);
        S(0x2564, D, D, 0, L); S(0x2565, L, L, 0, D); S(0x2566, D, D, 0, D);
        S(0x2567, D, D, L, 0); S(0x2568, L, L, D, 0); S(0x2569, D, D, D, 0);
        S(0x256A, D, D, L, L); S(0x256B, L, L, D, D); S(0x256C, D, D, D, D);
        // 256D–2573: arcs and diagonals
        S(0x2574, L, 0, 0, 0); S(0x2575, 0, 0, L, 0); S(0x2576, 0, L, 0, 0); S(0x2577, 0, 0, 0, L);
        S(0x2578, H, 0, 0, 0); S(0x2579, 0, 0, H, 0); S(0x257A, 0, H, 0, 0); S(0x257B, 0, 0, 0, H);
        S(0x257C, L, H, 0, 0); S(0x257D, 0, 0, L, H); S(0x257E, H, L, 0, 0); S(0x257F, 0, 0, H, L);
        return t;
    }

    /// <summary>(left, right, up, down) weights of a plain line character, or null if it isn't one.</summary>
    public static (int Left, int Right, int Up, int Down)? Arms(int cp)
    {
        if (cp is < 0x2500 or > 0x257F) return null;
        int a = ArmTable[cp - 0x2500];
        if (a == 0) return null;
        return (a & 3, a >> 2 & 3, a >> 4 & 3, a >> 6 & 3);
    }

    /// <summary>Heavy strokes are twice the light thickness.</summary>
    public static int HeavyThickness(int light) => 2 * light;

    /// <summary>
    /// Renders <paramref name="cp"/> into <paramref name="alpha"/> (w*h coverage bytes, row-major).
    /// Returns false (leaving the buffer cleared) for codepoints this class doesn't draw.
    /// </summary>
    public static bool Render(int cp, int w, int h, int thickness, Span<byte> alpha)
    {
        var c = new Canvas(alpha, w, h);
        c.Clear();
        int t = Math.Max(1, thickness);
        if (Arms(cp) is var (l, r, u, d))
        {
            Lines(ref c, t, l, r, u, d);
            return true;
        }
        if (DrawDashOrCurve(ref c, cp, t)) return true;
        if (cp is >= 0x2580 and <= 0x259F) { Block(ref c, cp); return true; }
        if (cp is >= 0x2800 and <= 0x28FF) { Braille(ref c, cp - 0x2800); return true; }
        return DrawTriangleOrPowerline(ref c, cp, t);
    }

    private static bool DrawDashOrCurve(ref Canvas c, int cp, int t)
    {
        int w = c.W, h = c.H;
        switch (cp)
        {
            // Dashed lines: (count, weight) along one axis.
            case 0x2504: Dashed(ref c, t, false, 3, Light); return true;
            case 0x2505: Dashed(ref c, t, false, 3, Heavy); return true;
            case 0x2506: Dashed(ref c, t, true, 3, Light); return true;
            case 0x2507: Dashed(ref c, t, true, 3, Heavy); return true;
            case 0x2508: Dashed(ref c, t, false, 4, Light); return true;
            case 0x2509: Dashed(ref c, t, false, 4, Heavy); return true;
            case 0x250A: Dashed(ref c, t, true, 4, Light); return true;
            case 0x250B: Dashed(ref c, t, true, 4, Heavy); return true;
            case 0x254C: Dashed(ref c, t, false, 2, Light); return true;
            case 0x254D: Dashed(ref c, t, false, 2, Heavy); return true;
            case 0x254E: Dashed(ref c, t, true, 2, Light); return true;
            case 0x254F: Dashed(ref c, t, true, 2, Heavy); return true;

            // Rounded corners ╭╮╯╰: (horizontal arm direction, vertical arm direction).
            case 0x256D: Arc(ref c, t, +1, +1); return true;
            case 0x256E: Arc(ref c, t, -1, +1); return true;
            case 0x256F: Arc(ref c, t, -1, -1); return true;
            case 0x2570: Arc(ref c, t, +1, -1); return true;

            // Diagonals ╱╲╳: lines through the cell corners, extended so they continue across cells.
            case 0x2571: c.Fill(new Band(w, 0, 0, h, t / 2.0)); return true;
            case 0x2572: c.Fill(new Band(0, 0, w, h, t / 2.0)); return true;
            case 0x2573: c.Fill(new Band(w, 0, 0, h, t / 2.0)); c.Fill(new Band(0, 0, w, h, t / 2.0)); return true;
        }
        return false;
    }

    private static bool DrawTriangleOrPowerline(ref Canvas c, int cp, int t)
    {
        int w = c.W, h = c.H;
        switch (cp)
        {
            case 0x25E2: c.Fill(new Triangle(w, 0, w, h, 0, h)); return true; // ◢
            case 0x25E3: c.Fill(new Triangle(0, 0, w, h, 0, h)); return true; // ◣
            case 0x25E4: c.Fill(new Triangle(0, 0, w, 0, 0, h)); return true; // ◤
            case 0x25E5: c.Fill(new Triangle(0, 0, w, 0, w, h)); return true; // ◥

            // Powerline: solid triangles and thin chevrons.
            case 0xE0B0: c.Fill(new Triangle(0, 0, w, h / 2.0, 0, h)); return true;
            case 0xE0B1: Chevron(ref c, t, 0, w); return true;
            case 0xE0B2: c.Fill(new Triangle(w, 0, 0, h / 2.0, w, h)); return true;
            case 0xE0B3: Chevron(ref c, t, w, 0); return true;
            // Half circles (solid and outline).
            case 0xE0B4: c.Fill(new Ellipse(0, h / 2.0, w, h / 2.0, -1)); return true;
            case 0xE0B5: c.Fill(new Ellipse(0, h / 2.0, w - t / 2.0, h / 2.0 - t / 2.0, t / 2.0)); return true;
            case 0xE0B6: c.Fill(new Ellipse(w, h / 2.0, w, h / 2.0, -1)); return true;
            case 0xE0B7: c.Fill(new Ellipse(w, h / 2.0, w - t / 2.0, h / 2.0 - t / 2.0, t / 2.0)); return true;
            // Diagonal triangles and slashes.
            case 0xE0B8: c.Fill(new Triangle(0, 0, 0, h, w, h)); return true;
            case 0xE0B9: c.Fill(new Band(0, 0, w, h, t / 2.0)); return true;
            case 0xE0BA: c.Fill(new Triangle(w, 0, w, h, 0, h)); return true;
            case 0xE0BB: c.Fill(new Band(w, 0, 0, h, t / 2.0)); return true;
            case 0xE0BC: c.Fill(new Triangle(0, 0, w, 0, 0, h)); return true;
            case 0xE0BD: c.Fill(new Band(w, 0, 0, h, t / 2.0)); return true;
            case 0xE0BE: c.Fill(new Triangle(0, 0, w, 0, w, h)); return true;
            case 0xE0BF: c.Fill(new Band(0, 0, w, h, t / 2.0)); return true;
        }
        return false;
    }

    // ---- Lines --------------------------------------------------------------------------------

    /// <summary>Start of a band of <paramref name="width"/> pixels centred on <paramref name="mid"/>, pixel-snapped.</summary>
    internal static int BandStart(int length, int width) => (int)Math.Floor((length - width) / 2.0 + 0.5);

    /// <summary>Total width of a stroke of the given weight (a double line is stroke, gap, stroke).</summary>
    private static int Width(int weight, int t) => weight switch { Heavy => HeavyThickness(t), Double => 3 * t, _ => t };

    private static void Lines(ref Canvas c, int t, int l, int r, int u, int d)
    {
        Arms(ref c, new Axis(false, c.W, c.H, t), l, r, u, d);
        Arms(ref c, new Axis(true, c.H, c.W, t), u, d, l, r);
    }

    /// <summary>
    /// The axis arms are drawn along: x unless <see cref="Transposed"/>, <see cref="Along"/> pixels long and
    /// <see cref="Across"/> wide, in strokes <see cref="T"/> pixels thick.
    /// </summary>
    private readonly record struct Axis(bool Transposed, int Along, int Across, int T);

    /// <summary>
    /// Draws the arms along one axis: <paramref name="neg"/> towards 0 and <paramref name="pos"/> towards its far end,
    /// meeting the perpendicular arms <paramref name="pn"/> (towards 0 across) and <paramref name="pp"/>.
    /// </summary>
    private static void Arms(ref Canvas c, in Axis axis, int neg, int pos, int pn, int pp)
    {
        var crossing = new Crossing(axis.Along, axis.T, pn, pp);
        for (int side = 0; side < 2; side++)
        {
            int weight = side == 0 ? neg : pos;
            if (weight == None) continue;
            bool towardsZero = side == 0;
            if (weight == Double) DoubleArm(ref c, in axis, towardsZero, crossing);
            else SingleArm(ref c, in axis, weight, towardsZero, (towardsZero ? pos : neg) != None, crossing);
        }
    }

    /// <summary>The perpendicular arms an axis meets and where they lie along it.</summary>
    private readonly struct Crossing
    {
        public readonly int Neg, Pos, Widest;
        /// <summary>Double stroke extents: [DoubleStart, +t) gap [+t, +2t) [+2t, +3t).</summary>
        public readonly int DoubleStart;
        /// <summary>Widest perpendicular stroke [StrokeStart, StrokeEnd).</summary>
        public readonly int StrokeStart, StrokeEnd;

        public Crossing(int along, int t, int pn, int pp)
        {
            Neg = pn;
            Pos = pp;
            Widest = Math.Max(pn, pp);
            DoubleStart = BandStart(along, 3 * t);
            if (Widest == None) return;
            int pw = Width(Widest, t);
            StrokeStart = BandStart(along, pw);
            StrokeEnd = StrokeStart + pw;
        }
    }

    private static void SingleArm(ref Canvas c, in Axis axis, int weight, bool towardsZero, bool other, in Crossing crossing)
    {
        bool tr = axis.Transposed;
        int along = axis.Along, across = axis.Across, t = axis.T;
        int bw = Width(weight, t), b0 = BandStart(across, bw);
        int end = SingleArmEnd(along, t, bw, towardsZero, other, crossing);
        FillArm(ref c, tr, towardsZero, end, along, b0, b0 + bw);
    }

    /// <summary>Where a single arm stops, measured from its own edge towards the far one.</summary>
    private static int SingleArmEnd(int along, int t, int bw, bool towardsZero, bool other, in Crossing crossing)
    {
        if (crossing.Widest == None) return StraightArmEnd(along, bw, towardsZero, other);
        if (crossing.Neg == Double && crossing.Pos == Double && !other)
            return towardsZero ? crossing.DoubleStart + t : crossing.DoubleStart + 2 * t; // tee into a double line: stop at its near stroke
        return towardsZero ? crossing.StrokeEnd : crossing.StrokeStart;
    }

    private static int StraightArmEnd(int along, int bw, bool towardsZero, bool other)
    {
        if (other) return towardsZero ? along / 2 + bw : along / 2 - bw; // straight line: overlap at the centre
        int own = BandStart(along, bw);
        return towardsZero ? own + bw : own;
    }

    /// <summary>Double arm: two strokes, each meeting the perpendicular arms on its own side.</summary>
    private static void DoubleArm(ref Canvas c, in Axis axis, bool towardsZero, in Crossing crossing)
    {
        bool tr = axis.Transposed;
        int along = axis.Along, across = axis.Across, t = axis.T;
        int s0 = BandStart(across, 3 * t);
        for (int stroke = 0; stroke < 2; stroke++)
        {
            int y0 = stroke == 0 ? s0 : s0 + 2 * t;
            int near = stroke == 0 ? crossing.Neg : crossing.Pos, far = stroke == 0 ? crossing.Pos : crossing.Neg;
            int end = DoubleStrokeEnd(t, near, far, towardsZero, crossing);
            FillArm(ref c, tr, towardsZero, end, along, y0, y0 + t);
        }
    }

    private static int DoubleStrokeEnd(int t, int near, int far, bool towardsZero, in Crossing crossing)
    {
        int dStart = crossing.DoubleStart;
        if (near == Double) return towardsZero ? dStart + t : dStart + 2 * t;      // inner corner
        if (far == Double) return towardsZero ? dStart + 3 * t : dStart;           // outer corner
        if (crossing.Widest != None) return towardsZero ? crossing.StrokeEnd : crossing.StrokeStart; // across a single line
        return towardsZero ? dStart + 3 * t : dStart;                              // straight: overlap
    }

    /// <summary>Fills a stroke from the cell edge to <paramref name="end"/>, on the side the arm points to.</summary>
    private static void FillArm(ref Canvas c, bool tr, bool towardsZero, int end, int along, int b0, int b1)
    {
        if (towardsZero) FillT(ref c, tr, 0, b0, end, b1);
        else FillT(ref c, tr, end, b0, along, b1);
    }

    private static void FillT(ref Canvas c, bool transposed, int a0, int b0, int a1, int b1)
    {
        if (transposed) c.FillRect(b0, a0, b1, a1);
        else c.FillRect(a0, b0, a1, b1);
    }

    private static void Dashed(ref Canvas c, int t, bool vertical, int count, int weight)
    {
        int along = vertical ? c.H : c.W, across = vertical ? c.W : c.H;
        int bw = Width(weight, t), b0 = BandStart(across, bw);
        // Gaps sit at segment ends (half at each end), so the pattern tiles across neighbouring cells.
        double seg = along / (double)count;
        double gap = Math.Max(1, Math.Round(seg * (vertical ? 0.4 : 0.35)));
        for (int i = 0; i < count; i++)
        {
            int a0 = (int)Math.Round(i * seg + gap / 2), a1 = (int)Math.Round((i + 1) * seg - gap / 2);
            if (a1 <= a0) a1 = a0 + 1;
            FillT(ref c, vertical, a0, b0, a1, b0 + bw);
        }
    }

    /// <summary>Rounded corner joining an arm towards <paramref name="sx"/> (±1 in x) and one towards <paramref name="sy"/>.</summary>
    private static void Arc(ref Canvas c, int t, int sx, int sy)
    {
        int lx = BandStart(c.W, t), ly = BandStart(c.H, t);
        double cx = lx + t / 2.0, cy = ly + t / 2.0; // centre lines of the straight strokes
        // Leave at least one straight pixel before the cell edges so the join with the next cell is exact.
        double r = Math.Min(Math.Min(cx, c.W - cx), Math.Min(cy, c.H - cy)) - 1;
        r = Math.Max(r, t / 2.0);
        double ax = cx + sx * r, ay = cy + sy * r;  // arc centre
        c.Fill(new ArcStroke(ax, ay, r, t / 2.0, sx, sy));
        // Straight continuations from the arc's ends to the cell edges.
        int axi = (int)Math.Round(ax), ayi = (int)Math.Round(ay);
        if (sx > 0) c.FillRect(axi, ly, c.W, ly + t); else c.FillRect(0, ly, axi, ly + t);
        if (sy > 0) c.FillRect(lx, ayi, lx + t, c.H); else c.FillRect(lx, 0, lx + t, ayi);
    }

    private static void Chevron(ref Canvas c, int t, double baseX, double tipX)
    {
        double half = t / 2.0;
        c.Fill(new Segment(baseX, 0, tipX, c.H / 2.0, half));
        c.Fill(new Segment(tipX, c.H / 2.0, baseX, c.H, half));
    }

    // ---- Blocks -------------------------------------------------------------------------------

    private static int Frac(int length, int eighths) => (int)Math.Round(length * eighths / 8.0);

    private static void Block(ref Canvas c, int cp)
    {
        int w = c.W, h = c.H;
        int mx = Frac(w, 4), my = Frac(h, 4);
        switch (cp)
        {
            case 0x2580: c.FillRect(0, 0, w, my); break;                       // ▀ upper half
            // Lower/right parts start at the complement's boundary, so ▀ + ▄ and ▌ + ▐ tile exactly.
            case >= 0x2581 and <= 0x2588: c.FillRect(0, Frac(h, 8 - (cp - 0x2580)), w, h); break; // ▁..█ lower n/8
            case >= 0x2589 and <= 0x258F: c.FillRect(0, 0, Frac(w, 8 - (cp - 0x2588)), h); break; // ▉..▏ left n/8
            case 0x2590: c.FillRect(mx, 0, w, h); break;                       // ▐ right half
            case 0x2591: c.FillRect(0, 0, w, h, 64); break;                    // ░
            case 0x2592: c.FillRect(0, 0, w, h, 128); break;                   // ▒
            case 0x2593: c.FillRect(0, 0, w, h, 191); break;                   // ▓
            case 0x2594: c.FillRect(0, 0, w, Frac(h, 1)); break;               // ▔ upper 1/8
            case 0x2595: c.FillRect(Frac(w, 7), 0, w, h); break;               // ▕ right 1/8
            default:
                // Quadrants ▖▗▘▙▚▛▜▝▞▟ as bit sets: 1 = upper left, 2 = upper right, 4 = lower left, 8 = lower right.
                int q = cp switch
                {
                    0x2596 => 4, 0x2597 => 8, 0x2598 => 1, 0x2599 => 1 | 4 | 8, 0x259A => 1 | 8,
                    0x259B => 1 | 2 | 4, 0x259C => 1 | 2 | 8, 0x259D => 2, 0x259E => 2 | 4, _ => 2 | 4 | 8,
                };
                if ((q & 1) != 0) c.FillRect(0, 0, mx, my);
                if ((q & 2) != 0) c.FillRect(mx, 0, w, my);
                if ((q & 4) != 0) c.FillRect(0, my, mx, h);
                if ((q & 8) != 0) c.FillRect(mx, my, w, h);
                break;
        }
    }

    /// <summary>Braille: dots 1–3 and 7 down the left column, 4–6 and 8 down the right (bits 0–7).</summary>
    private static void Braille(ref Canvas c, int bits)
    {
        double rx = c.W / 4.0, ry = c.H / 8.0;
        double radius = Math.Max(0.6, Math.Min(rx, ry) * 0.72);
        ReadOnlySpan<int> col = [0, 0, 0, 1, 1, 1, 0, 1];
        ReadOnlySpan<int> row = [0, 1, 2, 0, 1, 2, 3, 3];
        for (int i = 0; i < 8; i++)
        {
            if ((bits & 1 << i) == 0) continue;
            c.Fill(new Ellipse(rx * (1 + 2 * col[i]), ry * (1 + 2 * row[i]), radius, radius, -1));
        }
    }

    // ---- Canvas and shapes --------------------------------------------------------------------

    internal interface IShape
    {
        /// <summary>Bounding box (pixels) to sample; may exceed the canvas.</summary>
        void Bounds(out double x0, out double y0, out double x1, out double y1);
        bool Inside(double x, double y);
    }

    internal ref struct Canvas(Span<byte> alpha, int w, int h)
    {
        public readonly Span<byte> A = alpha;
        public readonly int W = w, H = h;
        private const int Samples = 5; // per axis: 26 coverage levels

        public readonly void Clear() => A[..(W * H)].Clear();

        public readonly void FillRect(int x0, int y0, int x1, int y1, byte a = 255)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(W, x1); y1 = Math.Min(H, y1);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (A[y * W + x] < a) A[y * W + x] = a;
        }

        /// <summary>Anti-aliased union of a shape into the canvas (supersampled coverage, max-blended).</summary>
        public readonly void Fill<T>(T shape) where T : struct, IShape
        {
            shape.Bounds(out double bx0, out double by0, out double bx1, out double by1);
            int x0 = Math.Max(0, (int)Math.Floor(bx0)), y0 = Math.Max(0, (int)Math.Floor(by0));
            int x1 = Math.Min(W, (int)Math.Ceiling(bx1)), y1 = Math.Min(H, (int)Math.Ceiling(by1));
            const double step = 1.0 / Samples;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int hits = Hits(shape, x, y, step);
                    if (hits == 0) continue;
                    byte a = (byte)((hits * 255 + Samples * Samples / 2) / (Samples * Samples));
                    if (A[y * W + x] < a) A[y * W + x] = a;
                }
            }
        }

        private static int Hits<T>(T shape, int x, int y, double step) where T : struct, IShape
        {
            int hits = 0;
            for (int sy = 0; sy < Samples; sy++)
                for (int sx = 0; sx < Samples; sx++)
                    if (shape.Inside(x + (sx + 0.5) * step, y + (sy + 0.5) * step)) hits++;
            return hits;
        }
    }

    internal readonly struct Triangle(double ax, double ay, double bx, double by, double cx, double cy) : IShape
    {
        public void Bounds(out double x0, out double y0, out double x1, out double y1)
        {
            x0 = Math.Min(ax, Math.Min(bx, cx)); x1 = Math.Max(ax, Math.Max(bx, cx));
            y0 = Math.Min(ay, Math.Min(by, cy)); y1 = Math.Max(ay, Math.Max(by, cy));
        }

        public bool Inside(double x, double y)
        {
            double d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by);
            double d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy);
            double d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }

    /// <summary>A stroke along the infinite line through two points (diagonals that continue across cells).</summary>
    internal readonly struct Band(double ax, double ay, double bx, double by, double half) : IShape
    {
        public void Bounds(out double x0, out double y0, out double x1, out double y1)
        {
            x0 = Math.Min(ax, bx) - half - 1; x1 = Math.Max(ax, bx) + half + 1;
            y0 = Math.Min(ay, by) - half - 1; y1 = Math.Max(ay, by) + half + 1;
        }

        public bool Inside(double x, double y)
        {
            double dx = bx - ax, dy = by - ay;
            double dist = Math.Abs((x - ax) * dy - (y - ay) * dx) / Math.Sqrt(dx * dx + dy * dy);
            return dist <= half;
        }
    }

    /// <summary>A stroke along a segment with round caps.</summary>
    internal readonly struct Segment(double ax, double ay, double bx, double by, double half) : IShape
    {
        public void Bounds(out double x0, out double y0, out double x1, out double y1)
        {
            x0 = Math.Min(ax, bx) - half; x1 = Math.Max(ax, bx) + half;
            y0 = Math.Min(ay, by) - half; y1 = Math.Max(ay, by) + half;
        }

        public bool Inside(double x, double y)
        {
            double dx = bx - ax, dy = by - ay;
            double k = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1);
            double ex = x - (ax + k * dx), ey = y - (ay + k * dy);
            return ex * ex + ey * ey <= half * half;
        }
    }

    /// <summary>The quarter of a circle's outline facing (−sx, −sy) from its centre.</summary>
    internal readonly struct ArcStroke(double cx, double cy, double r, double half, int sx, int sy) : IShape
    {
        public void Bounds(out double x0, out double y0, out double x1, out double y1)
        {
            x0 = cx - r - half; x1 = cx + r + half; y0 = cy - r - half; y1 = cy + r + half;
        }

        public bool Inside(double x, double y)
        {
            if ((x - cx) * sx > 0 || (y - cy) * sy > 0) return false;
            double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
            return Math.Abs(d) <= half;
        }
    }

    /// <summary>A filled ellipse (<paramref name="half"/> &lt; 0) or its outline of half-width <paramref name="half"/>.</summary>
    internal readonly struct Ellipse(double cx, double cy, double rx, double ry, double half) : IShape
    {
        public void Bounds(out double x0, out double y0, out double x1, out double y1)
        {
            double e = Math.Max(0, half);
            x0 = cx - rx - e; x1 = cx + rx + e; y0 = cy - ry - e; y1 = cy + ry + e;
        }

        public bool Inside(double x, double y)
        {
            double nx = (x - cx) / rx, ny = (y - cy) / ry;
            double f = nx * nx + ny * ny - 1;
            if (half < 0) return f <= 0;
            // First-order distance to the outline: f / |grad f|.
            double gx = 2 * (x - cx) / (rx * rx), gy = 2 * (y - cy) / (ry * ry);
            double g = Math.Sqrt(gx * gx + gy * gy);
            return g > 0 && Math.Abs(f / g) <= half;
        }
    }
}
