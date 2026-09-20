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
/// unbounded canvas, and only the frame differs.
///
/// <para>This is also, unconverted, the unit CONCEPTS-REF §15.5c's measurement
/// used: "fractions of the ORIGINAL 2880x1800 frame", x as a fraction of the
/// 2880 width and every y as a fraction of the 1800 height — exactly what
/// <see cref="HorizonF"/> applied to a frame's height and <see cref="VpXF"/>
/// applied to its width already mean here. So a measured row drops straight
/// into this record with no rescaling: the "convert once" step §15.5c's own
/// remarks call for is applying the fraction to whatever frame is at hand
/// (<c>GridArt.Draw</c>'s bounded strip, or <c>MainWindow.PlacePerspectiveShape</c>'s
/// <see cref="Quill.Models.NotePage.RefFrame"/>), not any change to the number
/// itself — see <see cref="GridPresets.Shape"/> for where that one conversion
/// lives.</para></summary>
/// <param name="HorizonF">Where the horizon crosses the frame (§12.4's 1/2, 1/4, 3/4).</param>
/// <param name="VpXF">1–3 vanishing points along the horizon. Distance apart is
/// §12.4's Narrow / Wide / Ultrawide.</param>
/// <param name="ThirdXF">3-point only: the third point's x. §15.5d.2 found this
/// is NOT the frame's centre line for most three-point presets — only measured
/// `3 Point` (0.5004) sits close to it — so this has to be its own measured
/// number rather than the assumed 0.5 three call sites used to hardcode
/// independently (GridPresets.Shape, GridArt.DrawPerspective and
/// MainWindow.PlacePerspectiveShape all wrote literal 0.5, which is the exact
/// "same quantity computed in more than one place" shape §0 and §49 both
/// warn about). Null only for a kind/preset with no third point.</param>
/// <param name="ThirdYF">3-point only: the third point's y. Above the horizon
/// unless the preset name says <c>Below</c>.</param>
public sealed record VpShape(double HorizonF, double[] VpXF, double? ThirdXF, double? ThirdYF);

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
    // 14.5's QUARTERING GRAMMAR — SUPERSEDED 2026-08-25, see CONCEPTS-REF
    // §15.5b/§15.5d. This used to be the whole of Shape(): a fraction plus a
    // separation-in-quarters plus two boolean modifiers (Side, Below),
    // re-derived from the preset's NAME on every call. It was a reasonable
    // guess before Concepts was actually measured, and the guess was wrong in
    // a specific, load-bearing way: §15.5d.4 found a POSITION name (`1/4
    // Narrow` vs `Side Narrow`) can move the horizon and leave the points
    // alone, exactly what §15.5b already found `Below` doing, so position and
    // spread and the horizon are NOT three independent axes multiplying out
    // into a grammar — "THE THREE AXES ARE NOT SEPARABLE. Do not build the
    // grammar" (§15.5b.3). §15.1's own ruling already said to mirror Concepts
    // exactly with no cross product; the measurement then said the same thing
    // about the numbers that the enumeration had already said about the names.
    //
    // So Shape() below is now a LOOKUP into the 19 individually measured
    // configurations §15.5c's table records — not a formula — and nothing
    // computes a preset's geometry from its name any more.
    // =======================================================================

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

    // =======================================================================
    // CONCEPTS-REF §15.5c — THE 19 MEASURED SHAPES, taken verbatim off "The
    // complete table — fractions of the ORIGINAL 2880x1800 frame". Six rows
    // (marked "100%" there) were read straight off a frozen 100% capture; the
    // other thirteen (marked "10%") were recovered through a zoomed-out
    // capture and an explicitly measured, leave-one-out-controlled mapping
    // back to frame fractions, worst control residual 0.0084 of the frame
    // (§15.5c). `1/4 Wide` (3-Point) is the one hybrid row: its horizon and
    // two on-horizon points are the 100% figures, its third point is the 10%
    // one, per §15.5c's "On the one hybrid row" note.
    //
    // THESE ARE THE SOURCE OF TRUTH. Not re-derived, not fitted, not rounded
    // beyond what the table itself already carries — a checker that wants to
    // confirm that is tools/GridPresetProof, which links this class directly
    // rather than re-typing the numbers a second time.
    //
    // The third point's x is carried explicitly (VpShape.ThirdXF) rather than
    // assumed to be the frame's centre line: §15.5d.2 found it is NOT, for
    // every 3-Point preset except the near-centred `3 Point` itself.
    // =======================================================================

    private static readonly Dictionary<string, VpShape> OnePointShapes = new()
    {
        ["1 Point"] = new VpShape(0.4989, new[] { 0.4998 }, null, null),
    };

    private static readonly Dictionary<string, VpShape> TwoPointShapes = new()
    {
        ["2 Point"]        = new VpShape(0.4989, new[] { 0.2570, 0.7430 }, null, null),
        ["1/2 Narrow"]     = new VpShape(-0.7231, new[] { -0.2300, 2.5563 }, null, null),
        ["1/4 Narrow"]     = new VpShape(-0.0529, new[] { -1.1019, 1.6514 }, null, null),
        ["Side Narrow"]    = new VpShape(0.8948, new[] { -1.0947, 1.6514 }, null, null),
        ["1/2 Wide"]       = new VpShape(-0.2534, new[] { -0.5026, 0.9590 }, null, null),
        ["1/4 Wide"]       = new VpShape(0.2822, new[] { 0.1918, 1.5112 }, null, null),
        ["Side Wide"]      = new VpShape(0.6173, new[] { -0.7445, 0.7166 }, null, null),
        ["1/2 Wide Below"] = new VpShape(1.1996, new[] { -0.5004, 0.9563 }, null, null),
        ["Side Ultrawide"] = new VpShape(0.4989, new[] { 0.0839, 0.7009 }, null, null),
    };

    private static readonly Dictionary<string, VpShape> ThreePointShapes = new()
    {
        ["3 Point"]              = new VpShape(0.8322, new[] { 0.2221, 0.7777 }, 0.5004, 0.1657),
        ["3/4 Narrow"]           = new VpShape(-1.3411, new[] { -0.8953, 1.7131 }, 0.3220, 9.1370),
        ["1/2 Narrow"]           = new VpShape(-0.8715, new[] { -0.6686, 1.9270 }, 0.5979, 9.5874),
        ["3/4 Wide"]             = new VpShape(-0.3387, new[] { -0.0653, 1.2334 }, 0.5849, 5.7983),
        ["1/4 Wide"]             = new VpShape(0.2211, new[] { -0.2695, 1.0207 }, 0.3925, 7.2412),
        ["Side Wide Below"]      = new VpShape(0.8423, new[] { 0.0392, 1.3012 }, 0.5819, 5.2342),
        ["1/4 Wide Below"]       = new VpShape(0.7764, new[] { -0.4048, 1.0532 }, 0.3207, 5.1821),
        ["3/4 Ultrawide Below"]  = new VpShape(1.3365, new[] { -0.0967, 1.0895 }, 0.4957, -0.4554),
        ["3/4 Ultrawide"]        = new VpShape(-0.5642, new[] { -0.1638, 1.1556 }, 0.4923, 0.8827),
    };

    /// <summary>The frame-relative shape a perspective preset describes, as
    /// FRACTIONS of 14.5's reference frame — §15.5c's measured table, looked up
    /// by name. Returns null for a lattice grid (<paramref name="vpCount"/> 0),
    /// and the row's own default preset's shape for <c>Custom</c> or any other
    /// unrecognised name, so the preview has something to draw before the user
    /// has moved a point.</summary>
    public static VpShape? Shape(GridKind kind, string preset, int vpCount)
    {
        var table = kind switch
        {
            GridKind.OnePoint => OnePointShapes,
            GridKind.TwoPoint => TwoPointShapes,
            GridKind.ThreePoint => ThreePointShapes,
            _ => null,
        };
        if (table == null || vpCount == 0) return null;
        if (table.TryGetValue(preset, out var shape)) return shape;
        return table[DefaultPreset(vpCount)];
    }
}
