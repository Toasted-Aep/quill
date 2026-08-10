using Quill.Models;

namespace Quill.Helpers;

/// <summary>
/// WHAT A PEN LOOKS LIKE WHEN IT IS NOT DRAWING A STROKE.
///
/// <para>CONCEPTS-REF §11.20 item 16: <i>"Shapes in the objects library are drawn
/// with the current pen style and pen colour, rather than a fixed preview
/// style."</i> A shape is not a stroke — it has no pressure samples and no
/// direction to take a nib angle from — so it cannot go through
/// <c>InkSurface.SegmentWidth</c>. But it must not look like a different
/// instrument either, or placing a highlighter shape would draw a hairline and
/// the library's preview would be a lie about what the tap is going to put on
/// the page.</para>
///
/// <para>So this is the stroke renderer's own per-pen treatment <b>evaluated at
/// neutral pressure</b> (pr = 0.5, sens = 1) and at the nib's average angle. The
/// numbers are not invented: each one is the corresponding arm of
/// <c>SegmentWidth</c> and of the alpha block above it, solved at that point.
/// One helper, three callers — the shape renderer, the Objects library's preview
/// tiles, and any future preview — so a shape, its preview and the pen that made
/// it cannot drift apart.</para>
/// </summary>
public static class PenStyle
{
    /// <summary>Mean of <c>|sin(angle - nibTilt)|</c> over all directions: the
    /// width a chisel or broad-edge nib averages out to when the geometry it is
    /// tracing runs every which way, which is exactly what a rectangle or an
    /// ellipse does. 2/pi, and it is why the marker and calligraphy arms below
    /// are not simply their maximum width.</summary>
    private const float MeanNib = 0.6366f;

    /// <summary>The outline width this pen draws a shape at. Mirrors
    /// <c>InkSurface.SegmentWidth</c> at pr = 0.5, sens = 1, plus the four pens
    /// whose width is decided before that method is reached (highlighter,
    /// pencil, crayon, watercolor).</summary>
    public static float Width(PenType pen, float size)
    {
        const float pr = 0.5f;
        float w = pen switch
        {
            // ---- decided ahead of SegmentWidth, in the renderer's own blocks
            PenType.Highlighter => size * 2.4f,
            PenType.Pencil => size * (0.45f + 0.7f * pr),
            PenType.Crayon => size * (0.9f + 0.7f * pr),
            PenType.Watercolor => size * 2.2f,
            PenType.Monoline => size,
            // ---- SegmentWidth's arms, solved at neutral pressure
            PenType.Marker => size * (0.5f + 1.25f * MeanNib),
            PenType.Calligraphy => size * (0.2f + 1.75f * MeanNib) * (0.4f + 0.95f * pr),
            PenType.Standard => size * (0.5f + 1.0f * pr),
            PenType.Brush => size * (0.12f + 3.2f * pr * pr),
            PenType.Fountain => size * 0.62f * (0.22f + 1.15f * MeanNib) * (0.78f + 0.5f * pr),
            PenType.Rollerball => size * (0.7f + 0.5f * pr),
            PenType.Gel => size * (0.85f + 0.5f * pr),
            PenType.Ballpoint => size * (0.55f + 0.6f * pr),
            PenType.FeltTip => size * (0.95f + 0.35f * pr),
            _ => size,
        };
        return Math.Max(0.6f, w);
    }

    /// <summary>The alpha this pen lays its core down at, before the stroke's own
    /// opacity is applied. The renderer's block: highlighter 110, pencil 145,
    /// crayon 210, watercolor 70, marker 235, ballpoint 240, everything else
    /// opaque.</summary>
    public static byte Alpha(PenType pen) => pen switch
    {
        PenType.Highlighter => 110,
        PenType.Pencil => 145,
        PenType.Crayon => 210,
        PenType.Watercolor => 70,
        PenType.Marker => 235,
        PenType.Ballpoint => 240,
        _ => 255,
    };

    /// <summary>One faint offset pass of grain, as (dx, dy, widthFactor,
    /// alpha) — the extra strokes the pencil, the crayon and the watercolor wash
    /// lay beside their core. Empty for every pen that draws a single clean
    /// line, so a caller can loop over it unconditionally.</summary>
    public static IReadOnlyList<(float Dx, float Dy, float WidthK, byte Alpha)> Grain(PenType pen) => pen switch
    {
        PenType.Pencil => new[]
        {
            (0.5f, 0.45f, 0.50f, (byte)55),
            (-0.45f, -0.4f, 0.45f, (byte)55),
        },
        PenType.Crayon => new[]
        {
            (0.8f, 0.7f, 0.55f, (byte)70),
            (-0.7f, -0.6f, 0.50f, (byte)70),
            (0.2f, -0.8f, 0.40f, (byte)70),
        },
        // The wash's wide, fainter under-pass. Concentric rather than offset,
        // which is how the renderer lays it.
        PenType.Watercolor => new[]
        {
            (0f, 0f, 1.5f, (byte)42),
        },
        _ => Array.Empty<(float, float, float, byte)>(),
    };

    /// <summary>True when the pen's ends are cut square rather than rounded. Only
    /// the highlighter, and the renderer is explicit about why: round caps bead
    /// into blobs at the end of a wide translucent band.</summary>
    public static bool FlatCaps(PenType pen) => pen == PenType.Highlighter;

    /// <summary>The wash's under-pass is drawn BEFORE the core; the pencil's and
    /// the crayon's grain is drawn after. Answers which, so a caller does not
    /// have to know the difference per pen.</summary>
    public static bool GrainUnderneath(PenType pen) => pen == PenType.Watercolor;
}
