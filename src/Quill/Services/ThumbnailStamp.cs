using Quill.Models;

namespace Quill.Services;

/// <summary>
/// The gallery thumbnail's content stamp (<see cref="ThumbnailCache"/>'s cache
/// key), in a file of its own so it is Win2D-free: <c>ThumbnailCache</c> renders
/// through <c>InkSurface</c> and cannot be linked into a console harness, and a
/// stamp that is only reasoned about is exactly how 49.6's stale-PNG defect got
/// in. <c>tools/LayerRoundTrip</c> links this file and runs it (58.10, check 3).
/// </summary>
public static class ThumbnailStamp
{
    private static ulong MixNum(ulong h, long v)
    {
        for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; }
        return h;
    }

    // Deliberately NOT string.GetHashCode(): that is randomised per process, so
    // a disk key built from it would miss on every launch.
    private static ulong MixStr(ulong h, string? s)
    {
        if (s == null) return MixNum(h, -1);
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>Cheap stamp over everything that changes the picture.
    ///
    /// <para><b>§49.6: the layer list is part of "the picture".</b> Hiding a
    /// layer changes what the page looks like without touching one stroke, so a
    /// stamp that did not mix the layers left the key identical, and the gallery
    /// served the STALE PNG of a page whose ink had gone. That is §49's ink-cache
    /// hazard one cache further out, and <c>InkSurface.LayersChanged</c> does not
    /// reach this one - the key has to carry the fact instead.</para>
    ///
    /// <para>Mixed ONLY when the page actually carries a layer list. A page that
    /// has never been near a layers UI keeps the stamp it has always had, so this
    /// invalidates nothing that exists - the same migration promise the model
    /// itself is built on.</para>
    ///
    /// <para><b>§58.10 check 3: an element's LAYER is part of the picture too.</b>
    /// Since 58.4 the canvas and this thumbnail draw in layer order across types,
    /// so moving a stroke, shape or text box to another layer changes what is on
    /// top without changing any geometry or colour - and the old stamp served the
    /// stale PNG. Each element's <c>LayerKey</c> is mixed, but only when the page
    /// has MORE THAN ONE layer: with one bucket every key paints in the same
    /// place (18.7), so a page with no layer list, or one layer, keeps exactly
    /// the stamp it had (tools/LayerRoundTrip holds the goldens).</para></summary>
    public static string Of(NotePage page)
    {
        ulong h = 14695981039346656037UL;
        h = MixStr(h, page.Background);
        if (page.Layers is { Count: > 0 } layers)
            foreach (var l in layers)
            {
                h = MixNum(h, l.Key);
                h = MixNum(h, l.Hidden ? 1 : 0);
                h = MixNum(h, (long)Math.Round(l.Opacity * 1000f));
            }
        bool keys = page.Layers is { Count: > 1 };
        if (keys)
        {
            foreach (var s in page.Strokes) h = MixNum(h, s.LayerKey);
            foreach (var sh in page.Shapes) h = MixNum(h, sh.LayerKey);
            foreach (var t in page.Texts) h = MixNum(h, t.LayerKey);
        }
        h = MixNum(h, page.Strokes.Count);
        h = MixNum(h, page.Shapes.Count);
        h = MixNum(h, page.Texts.Count);
        foreach (var s in page.Strokes)
        {
            h = MixNum(h, s.Points.Count);
            h = MixNum(h, (long)(s.Size * 16));
            h = MixStr(h, s.Color);
            if (s.Points.Count == 0) continue;
            var a = s.Points[0];
            var b = s.Points[^1];
            h = MixNum(h, (long)(a.X * 8)); h = MixNum(h, (long)(a.Y * 8));
            h = MixNum(h, (long)(b.X * 8)); h = MixNum(h, (long)(b.Y * 8));
        }
        foreach (var sh in page.Shapes)
        {
            h = MixNum(h, (int)sh.Kind);
            h = MixNum(h, (long)(sh.X * 8)); h = MixNum(h, (long)(sh.Y * 8));
            h = MixNum(h, (long)(sh.W * 8)); h = MixNum(h, (long)(sh.H * 8));
            h = MixStr(h, sh.Color);
        }
        foreach (var t in page.Texts)
        {
            h = MixNum(h, (long)(t.X * 8)); h = MixNum(h, (long)(t.Y * 8));
            h = MixNum(h, (long)(t.Width * 8));
            h = MixStr(h, t.Rtf);
        }
        return h.ToString("x16");
    }
}
