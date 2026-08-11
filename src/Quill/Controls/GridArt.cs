using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Quill.Helpers;
using Quill.Services;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// Draws a <see cref="GridSpec"/> into a BOUNDED FRAME — the §12.1 preview strip
/// and the §12.2 preset circles.
///
/// <para>Deliberately separate from <c>InkSurface.DrawGrid</c>, which solves a
/// different problem: an unbounded plane clipped to a viewport that pans and
/// zooms. Here the frame IS the subject, which is what lets a perspective preset
/// be expressed in frame fractions (§12.4) and lets one preset's thumbnail be
/// drawn under its own configuration rather than from a shared glyph.</para>
///
/// <para>Every entry point returns a <see cref="CanvasImageSource"/> rather than
/// hosting a CanvasControl: this whole surface lives inside a WinUI
/// <c>Popup</c>, and an image source is composited through the XAML tree that
/// owns it — the same reason the Brushes preview strip and the settings swatches
/// are image sources.</para>
/// </summary>
internal static class GridArt
{
    // World units a preset thumbnail shows across its face. Fixed rather than
    // fitted, because the whole point of the Lined row is that Narrow shows more
    // lines than Wide — normalising the scale would make the three identical.
    private const float ThumbWorld = 150f;

    // =======================================================================
    // Colour
    // =======================================================================
    /// <summary>The automatic gridline colour: contrast with the ground at the
    /// unobtrusive alpha the canvas has always used, cooled toward blue-grey for
    /// the perspective guides (§12.5: "a wash of blue-grey guides, never heavy
    /// black lines"). Derived from the ground, never a named grey.</summary>
    public static Color InkFor(GridSpec s, Color ground)
    {
        bool dark = ColorUtil.IsDark(ground);
        Color ink = dark ? Color.FromArgb(70, 255, 255, 255)
                         : Color.FromArgb(46, 0, 0, 0);

        if (s.IsPerspective) ink = Cool(ink, dark);

        if (!string.IsNullOrEmpty(s.Colour))
        {
            // A custom colour keeps the automatic alpha, so picking a colour can
            // never turn a subtle guide into a hard rule.
            try { var c = ColorUtil.Parse(s.Colour!); ink = Color.FromArgb(ink.A, c.R, c.G, c.B); }
            catch { }
        }

        double o = Math.Clamp(s.Opacity, 0, 1);
        return Color.FromArgb((byte)Math.Round(ink.A * o), ink.R, ink.G, ink.B);
    }

    /// <summary>The cool bias of §12.5. Applied to the CONTRAST ink rather than
    /// to a named slate, so a brown page's guides stay a cool brown and a blue
    /// page's a cool blue instead of every page getting the same grey.</summary>
    private static Color Cool(Color c, bool dark) => dark
        ? Color.FromArgb(c.A, Dn(c.R, 30), Dn(c.G, 14), c.B)
        : Color.FromArgb(c.A, c.R, Up(c.G, 16), Up(c.B, 38));

    private static byte Dn(byte v, int d) => (byte)Math.Max(0, v - d);
    private static byte Up(byte v, int d) => (byte)Math.Min(255, v + d);

    public static Color Fade(Color c, double f) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(c.A * f), 0, 255), c.R, c.G, c.B);

    // =======================================================================
    // Entry points
    // =======================================================================
    /// <summary>§12.1's live preview strip: the grid exactly as configured, at
    /// 1:1 with the page's own units so the spacing box means something.</summary>
    public static CanvasImageSource? Strip(GridSpec s, Color ground, float w, float h, float dpi)
    {
        try
        {
            var src = new CanvasImageSource(CanvasDevice.GetSharedDevice(), w, h, dpi);
            using (var ds = src.CreateDrawingSession(ground))
                Draw(ds, s, w, h, ground, 1f);
            return src;
        }
        catch { return null; }
    }

    /// <summary>A preset circle's miniature (§12.2) — the preset's OWN grid, not
    /// a shared glyph.</summary>
    public static CanvasImageSource? Thumb(GridSpec s, Color ground, float px, float dpi)
    {
        try
        {
            var src = new CanvasImageSource(CanvasDevice.GetSharedDevice(), px, px, dpi);
            using (var ds = src.CreateDrawingSession(ground))
                Draw(ds, s, px, px, ground, px / ThumbWorld);
            return src;
        }
        catch { return null; }
    }

    // =======================================================================
    // The draw
    // =======================================================================
    /// <summary><paramref name="scale"/> is frame pixels per world unit; the
    /// perspective shapes ignore it because they are expressed in fractions of
    /// the frame itself.</summary>
    public static void Draw(CanvasDrawingSession ds, GridSpec s, float w, float h,
                            Color ground, float scale)
    {
        var ink = InkFor(s, ground);
        if (ink.A == 0 || w <= 1 || h <= 1) return;

        float lw = (float)Math.Clamp(s.Weight, 0.25, 8);

        // "Only show the grid lines inside the artboard." The strip has no
        // artboard of its own, so it shows one: an inset rectangle that the grid
        // is clipped into, which is the only way the checkbox can be seen to do
        // anything before the page is looked at.
        var clip = s.Confine
            ? new Windows.Foundation.Rect(w * 0.12, h * 0.10, w * 0.76, h * 0.80)
            : new Windows.Foundation.Rect(0, 0, w, h);

        if (s.Confine)
            ds.DrawRectangle((float)clip.X, (float)clip.Y, (float)clip.Width, (float)clip.Height,
                             Fade(ink, 1.6), lw);

        using (ds.CreateLayer(1f, clip))
        {
            if (s.IsPerspective) DrawPerspective(ds, s, w, h, ink, lw);
            else DrawLattice(ds, s, w, h, ink, lw, scale);
        }
    }

    // ---- lattice grids ---------------------------------------------------
    private static void DrawLattice(CanvasDrawingSession ds, GridSpec s, float w, float h,
                                    Color ink, float lw, float scale)
    {
        float step = (float)Math.Max(2, s.Spacing) * scale;
        // A frame this size cannot show a thousand cells legibly and a preview
        // that tries is a grey rectangle, so the step doubles until it can.
        while (w / step * (h / step) > 6000) step *= 2;
        if (step < 2) step = 2;

        int div = Math.Clamp(s.Divisions, 1, 64);
        float minor = step / div;
        // Minor lines are the fainter family; the main lines keep the full ink.
        var minorInk = Fade(ink, 0.45);
        bool portrait = s.Portrait && GridPresets.Has(s.Kind, GridPart.Orientation);

        switch (s.Kind)
        {
            case GridKind.Dot:
                for (float y = 0; y <= h; y += step)
                    for (float x = 0; x <= w; x += step)
                        ds.FillCircle(new Vector2(x, y), Math.Max(0.8f, lw * 1.3f), ink);
                break;

            case GridKind.Lined:
                if (portrait) for (float x = 0; x <= w; x += step) ds.DrawLine(x, 0, x, h, ink, lw);
                else for (float y = 0; y <= h; y += step) ds.DrawLine(0, y, w, y, ink, lw);
                break;

            case GridKind.Graph:
                if (div > 1)
                {
                    for (float x = 0; x <= w; x += minor) ds.DrawLine(x, 0, x, h, minorInk, lw * 0.75f);
                    for (float y = 0; y <= h; y += minor) ds.DrawLine(0, y, w, y, minorInk, lw * 0.75f);
                }
                for (float x = 0; x <= w; x += step) ds.DrawLine(x, 0, x, h, ink, lw);
                for (float y = 0; y <= h; y += step) ds.DrawLine(0, y, w, y, ink, lw);
                break;

            // 12.11: the angle is the inclination of the DIAGONALS, and the
            // straight family - isometric's verticals, the triangle grid's
            // horizontals - does not move with it. So sweeping the slider
            // restretches every cell instead of turning the field; the rigid
            // turn is Orientation's job and stays independent of this. The
            // arithmetic is GridLattice's so this strip and the canvas cannot
            // promise each other different grids.
            case GridKind.Isometric:
            case GridKind.Triangle:
                {
                    // The generic guard above sizes the STRAIGHT family; at the
                    // ends of the angle range the diagonals are several times
                    // finer than that, so the frame is re-counted against
                    // whichever family actually gets densest.
                    float fine = (float)GridLattice.FinestPerp(s.Kind, s.Angle, step);
                    while (fine >= 0.01f && w / fine * (h / fine) > 6000) { step *= 2; fine *= 2; }

                    var tl = Vector2.Zero;
                    var br = new Vector2(w, h);
                    foreach (var fam in GridLattice.Families(s.Kind, s.Angle, step, portrait))
                    {
                        // An 86 DIP thumbnail can be handed a family finer than
                        // its own pixels; below this it is a grey wash either
                        // way, and the loop is what has to be protected.
                        var f = fam.Perp < 1.5f ? fam with { Perp = 1.5f } : fam;
                        foreach (var ln in GridLattice.Lines(f, tl, br))
                            ds.DrawLine(ln.A, ln.B, ink, lw);
                    }
                }
                break;
        }
    }

    // ---- perspective -----------------------------------------------------
    /// <summary>§12.5: a horizon, the vanishing points on it, a fan of fine lines
    /// radiating from each, and the true line families the configuration leaves
    /// standing. Drawn fine and low-contrast; the fan is the subject.</summary>
    private static void DrawPerspective(CanvasDrawingSession ds, GridSpec s, float w, float h,
                                        Color ink, float lw)
    {
        var shape = s.Shape();
        if (shape == null) return;

        // Portrait recomputes the shape against a portrait frame: the fractions
        // are the same, the frame they are measured in is not, so the points land
        // differently — which is exactly why the perspective kinds carry the
        // control and the square grids do not.
        float fw = s.Portrait ? h : w, fh = s.Portrait ? w : h;
        float sx = w / fw, sy = h / fh;

        float horizon = (float)shape.HorizonF * fh * sy;
        // Presets are all level (12.4 describes positions, never a tilt), so the
        // preview and the thumbnails draw the level case. The stored tilt lives
        // on the page and is the canvas renderer's business.
        var pts = new List<Vector2>();
        for (int i = 0; i < shape.VpXF.Length; i++)
            pts.Add(new Vector2((float)shape.VpXF[i] * fw * sx, horizon));
        if (shape.ThirdYF is double ty && pts.Count == 3)
            pts[2] = new Vector2(w * 0.5f, (float)ty * fh * sy);

        int rays = Math.Clamp(s.Density, 3, 96);
        float rayW = Math.Max(0.5f, lw * 0.75f);

        // The true families each configuration leaves: a 1-point keeps both axes,
        // a 2-point keeps the verticals, a 3-point keeps nothing straight.
        // 12.5: "very fine and low-contrast - a wash of blue-grey guides, never
        // heavy black lines". Two vanishing points whose rays are nearly parallel
        // crowd a 300 DIP strip, so the true families and the fan both sit well
        // under the horizon and only the horizon reads as a line.
        float step = MathF.Max(10f, h / 9f);
        if (pts.Count == 1)
        {
            for (float y = 0; y <= h; y += step) ds.DrawLine(0, y, w, y, Fade(ink, 0.34), rayW);
            for (float x = 0; x <= w; x += step) ds.DrawLine(x, 0, x, h, Fade(ink, 0.34), rayW);
        }
        else if (pts.Count == 2)
        {
            for (float x = 0; x <= w; x += step) ds.DrawLine(x, 0, x, h, Fade(ink, 0.34), rayW);
        }

        foreach (var vp in pts) Fan(ds, vp, w, h, rays, Fade(ink, 0.55), rayW);

        // The horizon last and a shade stronger, so it reads through the fan.
        if (horizon >= -1 && horizon <= h + 1)
            ds.DrawLine(0, horizon, w, horizon, Fade(ink, 1.5), lw);

        // The points themselves, where they fall inside the frame.
        foreach (var vp in pts)
            if (vp.X >= 0 && vp.X <= w && vp.Y >= 0 && vp.Y <= h)
                ds.FillCircle(vp, Math.Max(1.6f, lw * 1.8f), Fade(ink, 2.2));
    }

    /// <summary>The fan from one point: rays spread evenly across the angular
    /// window the frame subtends from that point, which is what makes them dense
    /// near the point and spread out across the frame.</summary>
    private static void Fan(CanvasDrawingSession ds, Vector2 vp, float w, float h,
                            int rays, Color ink, float lw)
    {
        bool inside = vp.X >= 0 && vp.X <= w && vp.Y >= 0 && vp.Y <= h;
        float a0 = 0f, a1 = MathF.PI * 2f;
        if (!inside)
        {
            Span<Vector2> corners = stackalloc Vector2[4] { new(0, 0), new(w, 0), new(w, h), new(0, h) };
            float baseA = MathF.Atan2(h * 0.5f - vp.Y, w * 0.5f - vp.X);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var c in corners)
            {
                float rel = MathF.Atan2(c.Y - vp.Y, c.X - vp.X) - baseA;
                while (rel > MathF.PI) rel -= MathF.PI * 2f;
                while (rel < -MathF.PI) rel += MathF.PI * 2f;
                lo = MathF.Min(lo, rel); hi = MathF.Max(hi, rel);
            }
            a0 = baseA + lo; a1 = baseA + hi;
        }

        for (int r = 0; r < rays; r++)
        {
            float ang = a0 + (a1 - a0) * (rays == 1 ? 0.5f : r / (float)(rays - 1));
            var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            if (ClipRay(vp, d, w, h, out var p0, out var p1)) ds.DrawLine(p0, p1, ink, lw);
        }
    }

    /// <summary>Clips the ray from <paramref name="o"/> along <paramref name="d"/>
    /// to the frame. A slab test, so a ray parallel to an edge is handled by the
    /// zero-direction branch rather than by a division by zero.</summary>
    private static bool ClipRay(Vector2 o, Vector2 d, float w, float h,
                                out Vector2 p0, out Vector2 p1)
    {
        float tMin = 0f, tMax = float.MaxValue;
        p0 = p1 = default;

        if (!Slab(o.X, d.X, 0, w, ref tMin, ref tMax)) return false;
        if (!Slab(o.Y, d.Y, 0, h, ref tMin, ref tMax)) return false;
        if (tMax <= tMin) return false;

        p0 = o + d * tMin;
        p1 = o + d * tMax;
        return true;
    }

    private static bool Slab(float o, float d, float lo, float hi, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(d) < 1e-6f) return o >= lo && o <= hi;
        float t0 = (lo - o) / d, t1 = (hi - o) / d;
        if (t0 > t1) (t0, t1) = (t1, t0);
        tMin = MathF.Max(tMin, t0);
        tMax = MathF.Min(tMax, t1);
        return tMax > tMin;
    }
}
