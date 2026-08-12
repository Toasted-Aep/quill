using Quill.Models;

namespace Quill.Services;

/// <summary>
/// What a grid editor PAGE is editing (CONCEPTS-REF §12).
///
/// <para>This is deliberately NOT <see cref="GridType"/>. §12.5 moves 1-Point,
/// 2-Point and 3-Point into the same <c>Grid Type</c> row as the lattice grids,
/// but the model comment on <c>GridType</c> is explicit that perspective is an
/// overlay carried by <c>NotePage.Perspective</c> and must never become an enum
/// member — that enum serialises as its integer. So the UI has its own kind,
/// and <see cref="GridSpec.Resolve"/> maps it back onto the two model fields
/// that actually persist.</para>
/// </summary>
public enum GridKind { Dot, Lined, Graph, Isometric, Triangle, OnePoint, TwoPoint, ThreePoint }

/// <summary>Which controls a kind's page carries (§12.3). The rule the user
/// stated in words — <i>"if rotation does not change the form, do not have it in
/// page"</i> — is <see cref="Orientation"/> being absent from Dot and Graph
/// rather than present and disabled, so it lives here in one table instead of
/// being re-decided at each call site.</summary>
[Flags]
public enum GridPart
{
    None = 0,
    Preset = 1 << 0,
    Spacing = 1 << 1,
    Divisions = 1 << 2,
    Vanishing = 1 << 3,
    Density = 1 << 4,
    Weight = 1 << 5,
    Colour = 1 << 6,
    Opacity = 1 << 7,
    Orientation = 1 << 8,
    Confine = 1 << 9,
    /// <summary>12.10: the angle of the diagonals, for the isometric and
    /// triangle grids only.</summary>
    Angle = 1 << 10,
}

/// <summary>Where a perspective preset puts its points, in FRAME FRACTIONS —
/// 0 is the left/top edge, 1 the right/bottom, and anything outside that is off
/// the frame. Fractions rather than canvas coordinates because the same shape
/// has to draw into an 86 DIP thumbnail, a 300 DIP preview strip and a page of
/// unbounded canvas, and only the frame differs.</summary>
/// <param name="HorizonF">Where the horizon crosses the frame (§12.4's 1/2, 1/4, 3/4).</param>
/// <param name="VpXF">1–3 vanishing points along the horizon. Distance apart is
/// §12.4's Narrow / Wide / Ultrawide.</param>
/// <param name="ThirdYF">3-point only: the third point's y. Above the horizon
/// unless the preset name says <c>Below</c>.</param>
public sealed record VpShape(double HorizonF, double[] VpXF, double? ThirdYF);

/// <summary>Everything a grid renderer needs, gathered off the page. A plain
/// mutable bag: the editor page mutates one field per control and hands the same
/// instance straight back to the preview, which is what makes a slider drag cost
/// one redraw instead of a rebuild.</summary>
public sealed class GridSpec
{
    public GridKind Kind { get; set; } = GridKind.Lined;
    public string Preset { get; set; } = "Custom";
    /// <summary>World units between main lines.</summary>
    public double Spacing { get; set; } = 32;
    /// <summary>Minor divisions between main lines. 1 = main lines only (§12.3).</summary>
    public int Divisions { get; set; } = 1;
    /// <summary>Stroke width in points, §12.2's <c>1 pts</c> box.</summary>
    public double Weight { get; set; } = 1;
    /// <summary>0..1, a MULTIPLIER over the automatic alpha exactly as
    /// <c>NotePage.GridOpacity</c> already is — 1 is the grid this build has
    /// always drawn.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>null = Automatic (§12.2: "adapts to your background color").</summary>
    public string? Colour { get; set; }
    /// <summary>Isometric axis tilt.</summary>
    public double Angle { get; set; } = 30;
    /// <summary>Vanishing lines per point (§12.3's Density caption).</summary>
    public int Density { get; set; } = 24;
    /// <summary>false = Landscape. Only ever read by a kind whose page carries
    /// the control at all (§12.3).</summary>
    public bool Portrait { get; set; }
    /// <summary>"Only show the grid lines inside the artboard."</summary>
    public bool Confine { get; set; }

    public bool IsPerspective =>
        Kind is GridKind.OnePoint or GridKind.TwoPoint or GridKind.ThreePoint;

    public int VpCount => Kind switch
    {
        GridKind.OnePoint => 1,
        GridKind.TwoPoint => 2,
        GridKind.ThreePoint => 3,
        _ => 0,
    };

    public GridSpec Clone() => (GridSpec)MemberwiseClone();

    /// <summary>The lattice half of the kind. <c>None</c> for the perspective
    /// kinds, which carry no lattice of their own.</summary>
    public GridType AsGridType => Kind switch
    {
        GridKind.Dot => GridType.Dotted,
        GridKind.Lined => GridType.Lines,
        GridKind.Graph => GridType.Square,
        GridKind.Isometric => GridType.Isometric,
        GridKind.Triangle => GridType.Triangle,
        _ => GridType.None,
    };

    /// <summary>12.10's defaults: isometric's true value is 30 degrees and the
    /// equilateral triangle case is 60. Both are offered as the default and the
    /// user may go off them.</summary>
    public static double DefaultAngle(GridKind k) => k == GridKind.Triangle ? 60 : 30;

    /// <summary>Reads a page into a spec. A page written before §12 existed has
    /// zeroes in the new fields, and zero is never a legal value for any of them,
    /// so "unset" and "default" are the same answer and no migration is needed.</summary>
    public static GridSpec FromPage(NotePage? p, GridKind kind)
    {
        var s = new GridSpec { Kind = kind };
        if (p == null) return s;
        s.Preset = string.IsNullOrEmpty(p.GridPreset) ? "Custom" : p.GridPreset!;
        s.Spacing = p.GridSpacing > 0 ? p.GridSpacing : 32;
        s.Divisions = p.GridDivisions > 0 ? p.GridDivisions : 1;
        s.Weight = p.GridWeight > 0 ? p.GridWeight : 1;
        s.Opacity = Math.Clamp(p.GridOpacity, 0, 1);
        s.Colour = p.GridColor;
        s.Angle = p.GridAngle > 0 ? p.GridAngle : DefaultAngle(kind);
        s.Portrait = p.GridPortrait;
        s.Confine = p.GridConfine;
        s.Density = p.Perspective?.RayCount is int r && r > 0 ? r : 24;
        return s;
    }

    /// <summary>Which kind a page is currently ON. The perspective overlay wins,
    /// because §12.5 makes the perspective kinds members of the same one-of row
    /// as the lattices.</summary>
    public static GridKind? KindOf(NotePage? p)
    {
        if (p == null) return null;
        int vps = p.Perspective?.Vps.Count ?? 0;
        if (vps >= 3) return GridKind.ThreePoint;
        if (vps == 2) return GridKind.TwoPoint;
        if (vps == 1) return GridKind.OnePoint;
        return p.Grid switch
        {
            GridType.Dotted => GridKind.Dot,
            GridType.Lines => GridKind.Lined,
            GridType.Square => GridKind.Graph,
            GridType.Isometric => GridKind.Isometric,
            GridType.Triangle => GridKind.Triangle,
            _ => null,
        };
    }

    /// <summary>Resolves the frame-relative shape this spec's preset describes,
    /// or null when the spec is not a perspective grid.</summary>
    public VpShape? Shape() => GridPresets.Shape(Kind, Preset, VpCount);
}

/// <summary>
/// §12.4's baked-in preset lists, exactly as written, each row ending in
/// <c>Custom</c>.
///
/// <para>The perspective names describe GEOMETRY, and that is load-bearing:
/// <c>Narrow</c> / <c>Wide</c> / <c>Ultrawide</c> is how far apart the vanishing
/// points sit, the fraction is where the horizon crosses the frame, <c>Side</c>
/// puts a point off the edge, and <c>Below</c> drops the third point beneath the
/// horizon rather than above. Every thumbnail is drawn under its OWN shape,
/// which is why they differ and why one shared glyph will not do.</para>
/// </summary>
public static class GridPresets
{
    // =======================================================================
    // 14.5 - QUARTERING THE REFERENCE FRAME
    //
    // Every position below is an integer number of QUARTERS of the reference
    // frame, because that is how 14.5 says the points are placed:
    //
    //     quarter 1 - the first vanishing point
    //     quarter 2 - the centre
    //     quarter 3 - the second vanishing point
    //
    // So the plain "2 Point" preset puts its points on the frame's own quarter
    // marks, 0.25 and 0.75, and the named presets move OFF THAT QUARTERING
    // rather than off the window's edge - which is what makes "Side" mean "off
    // the frame" instead of "at the edge of whatever is on screen right now".
    //
    // What this replaced: separations of 1.7, 3.2 and 6.0 FRAME WIDTHS with the
    // bare preset defaulting to 3.2, which put the left point of a default
    // 2-point grid more than a frame width off the left edge. That is the
    // "far left edge of the page" 14.5 rejects.
    // =======================================================================
    private const double Q = 0.25;

    // Half the separation between the outer points, IN QUARTERS. Narrow IS the
    // quartering - one quarter either side of centre - and each step out adds
    // another quarter, so every point still lands on a quarter mark: Wide on the
    // frame's own edges, Ultrawide one quarter beyond them.
    //
    // FORK, flagged to the user: 14.5 fixes the quartering and 12.4 orders the
    // three names, but neither says how much wider Wide and Ultrawide are than
    // Narrow. One quarter per step is the reading that keeps every preset on a
    // quarter mark of the frame; the alternative (a geometric run, so Ultrawide
    // goes nearly orthographic) cannot be quartered.
    private const int HalfNarrow = 1;   // points at 0.25 / 0.75 - the quartering
    private const int HalfWide = 2;     // points at 0.00 / 1.00 - the frame edges
    private const int HalfUltra = 3;    // points at -0.25 / 1.25 - off both edges

    // "Side puts a point off the edge" (12.4). The pair keeps its separation and
    // slides two quarters - half a frame - to the left, so the FIRST point is
    // always off the frame whichever separation it is carrying. Anchoring the
    // first point AT a fixed -0.25 instead would have made "Side Ultrawide"
    // identical to plain "Ultrawide", since that is already where the centred
    // arrangement puts it.
    private const int SideShift = 2;

    // The third point sits on the frame's vertical centre line - quarter 2, the
    // same centre the horizon's points are measured from - four quarters, one
    // whole frame height, from the horizon.
    private const int ThirdQuarters = 4;

    private static readonly string[] NoPresets = { };

    public static IReadOnlyList<string> Names(GridKind kind) => kind switch
    {
        // §12.4 has no list for Dot: a dot grid has nothing but its spacing.
        GridKind.Dot => NoPresets,
        GridKind.Lined => new[] { "Narrow", "Medium", "Wide", "Custom" },
        GridKind.Graph => new[] { "Square Grid", "10 / 100", "16 / 64", "Custom" },
        GridKind.Isometric => new[] { "Isometric", "Custom" },
        GridKind.Triangle => new[] { "Triangle", "Custom" },
        GridKind.OnePoint => new[] { "1 Point", "Custom" },
        GridKind.TwoPoint => new[]
        {
            "2 Point", "1/2 Narrow", "1/4 Narrow", "Side Narrow", "1/2 Wide",
            "1/4 Wide", "Side Wide", "1/2 Wide Below", "Side Ultrawide", "Custom",
        },
        GridKind.ThreePoint => new[]
        {
            "3 Point", "3/4 Narrow", "1/2 Narrow", "3/4 Wide", "1/4 Wide",
            "Side Wide Below", "1/4 Wide Below", "3/4 Ultrawide Below",
            "3/4 Ultrawide", "Custom",
        },
        _ => NoPresets,
    };

    /// <summary>Which controls this kind's page carries (§12.3's table, read
    /// across). Orientation is ABSENT from Dot and Graph, never disabled.</summary>
    public static GridPart Parts(GridKind kind) => kind switch
    {
        GridKind.Dot =>
            GridPart.Spacing | GridPart.Weight | GridPart.Colour | GridPart.Opacity |
            GridPart.Confine,
        GridKind.Lined =>
            GridPart.Preset | GridPart.Spacing | GridPart.Weight | GridPart.Colour |
            GridPart.Opacity | GridPart.Orientation | GridPart.Confine,
        GridKind.Graph =>
            GridPart.Preset | GridPart.Spacing | GridPart.Divisions | GridPart.Weight |
            GridPart.Colour | GridPart.Opacity | GridPart.Confine,
        // 12.10: isometric and the triangle grid both gain an Angle row, and
        // both keep Orientation - a 90 degree turn genuinely changes each.
        GridKind.Isometric or GridKind.Triangle =>
            GridPart.Preset | GridPart.Spacing | GridPart.Angle | GridPart.Weight |
            GridPart.Colour | GridPart.Opacity | GridPart.Orientation | GridPart.Confine,
        _ =>
            GridPart.Preset | GridPart.Vanishing | GridPart.Density | GridPart.Weight |
            GridPart.Colour | GridPart.Opacity | GridPart.Orientation | GridPart.Confine,
    };

    public static bool Has(GridKind kind, GridPart part) => (Parts(kind) & part) != 0;

    /// <summary>The page title §12.1 item 4 sets in 34 DIP bold.</summary>
    public static string Title(GridKind kind) => kind switch
    {
        GridKind.Dot => "Dot Grid",
        GridKind.Lined => "Lined Paper",
        GridKind.Graph => "Graph Paper",
        GridKind.Isometric => "Isometric Grid",
        GridKind.Triangle => "Triangle Grid",
        GridKind.OnePoint => "1-Point",
        GridKind.TwoPoint => "2-Point",
        _ => "3-Point",
    };

    /// <summary>The caption under the row on the Grid Type circles.</summary>
    public static string RowLabel(GridKind kind) => kind switch
    {
        GridKind.Dot => "Dot Grid",
        GridKind.Lined => "Lined Paper",
        GridKind.Graph => "Graph Paper",
        GridKind.Isometric => "Isometric",
        GridKind.Triangle => "Triangle",
        GridKind.OnePoint => "1-Point",
        GridKind.TwoPoint => "2-Point",
        _ => "3-Point",
    };

    /// <summary>Applies a preset's numbers onto a spec. <c>Custom</c> changes
    /// nothing — it is the row's way of saying "whatever you have dialled in".</summary>
    public static void Apply(GridSpec s, string preset)
    {
        s.Preset = preset;
        if (preset == "Custom") return;

        switch (s.Kind)
        {
            case GridKind.Lined:
                s.Spacing = preset switch { "Narrow" => 22, "Wide" => 52, _ => 34 };
                break;
            case GridKind.Graph:
                // 10 / 100 and 16 / 64 are the metric and imperial engineering
                // stocks: a main line every 1/10 or 1/16 of the document unit,
                // with the second number the cell count that falls out of it.
                switch (preset)
                {
                    case "Square Grid": s.Spacing = 32; s.Divisions = 1; break;
                    case "10 / 100": s.Spacing = 100; s.Divisions = 10; break;
                    case "16 / 64": s.Spacing = 96; s.Divisions = 16; break;
                }
                break;
            case GridKind.Isometric:
                if (preset == "Isometric") { s.Spacing = 32; s.Angle = 30; }
                break;
            case GridKind.Triangle:
                if (preset == "Triangle") { s.Spacing = 32; s.Angle = 60; }
                break;
        }
    }

    /// <summary>The default preset for a perspective kind - the row's first
    /// entry, and the shape a page gets when the points are placed without one
    /// being named.</summary>
    public static string DefaultPreset(int vpCount) => vpCount switch
    {
        1 => "1 Point",
        2 => "2 Point",
        _ => "3 Point",
    };

    /// <summary>The frame-relative shape a perspective preset describes, as
    /// FRACTIONS of 14.5's reference frame. Returns null for a lattice grid, and
    /// the default shape for <c>Custom</c> so the preview has something to draw
    /// before the user has moved a point.
    ///
    /// <para>Read the name and the answer falls out of the quartering: a fraction
    /// is where the horizon crosses the frame, Narrow / Wide / Ultrawide is how
    /// many quarters the points sit either side of the centre, Side slides the
    /// pair off the frame, and Below drops the third point beneath the horizon
    /// instead of above it.</para></summary>
    public static VpShape? Shape(GridKind kind, string preset, int vpCount)
    {
        if (vpCount == 0) return null;
        // 14.4: one point, on the frame's centre, with a horizon through it. No
        // separation to name and nothing else to place.
        if (vpCount == 1) return new VpShape(0.5, new[] { 0.5 }, null);

        // Read the name: a fraction, a separation, and the two modifiers.
        double horizon = Fraction(preset) ?? 0.5;   // no fraction = quarter 2
        int half = HalfQuarters(preset);
        bool side = preset.Contains("Side", StringComparison.Ordinal);
        bool below = preset.Contains("Below", StringComparison.Ordinal);

        // Centred on quarter 2, then slid left by SideShift quarters if the name
        // says Side. Both points stay on quarter marks either way.
        double centre = 0.5 - (side ? SideShift * Q : 0);
        double x0 = centre - half * Q;
        double x1 = centre + half * Q;

        if (vpCount == 2)
        {
            // A 2-point grid has no third point for "Below" to move, so the only
            // reading left of the word is the eye line dropping a quarter further
            // down the frame. FORK, flagged to the user: §12.4 defines Below only
            // for the third point but its own 2-Point row contains
            // "1/2 Wide Below".
            if (below) horizon += Q;
            return new VpShape(horizon, new[] { x0, x1 }, null);
        }

        double reach = ThirdQuarters * Q;
        double third = below ? horizon + reach : horizon - reach;
        return new VpShape(horizon, new[] { x0, x1, 0.5 }, third);
    }

    private static double? Fraction(string preset)
    {
        if (preset.Contains("3/4", StringComparison.Ordinal)) return 0.75;
        if (preset.Contains("1/4", StringComparison.Ordinal)) return 0.25;
        if (preset.Contains("1/2", StringComparison.Ordinal)) return 0.5;
        return null;
    }

    /// <summary>How many quarters each point sits from the pair's centre. The
    /// bare "2 Point" / "3 Point" carries no separation word and takes the
    /// quartering itself, which is Narrow's value.</summary>
    private static int HalfQuarters(string preset) =>
        preset.Contains("Ultrawide", StringComparison.Ordinal) ? HalfUltra
        : preset.Contains("Wide", StringComparison.Ordinal) ? HalfWide
        : HalfNarrow;
}
