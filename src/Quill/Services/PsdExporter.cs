using System.Numerics;
using System.Text;
using Quill.Helpers;
using Windows.UI;

namespace Quill.Services;

// ===========================================================================
// PSD EXPORT (CONCEPTS-REF 50; the writer 18.9 seam 1 named and 49 left
// missing).
//
// WHY THIS DOES NOT TAKE A PdfVectorPage. HtmlSvgExporter's header records
// that SVG takes "the same PdfVectorPage data the vector PDF export uses", and
// the obvious move was for PSD to take it too. It cannot, and the reason is
// structural rather than a matter of taste: PdfVectorPage has FOUR flat lists
// keyed by primitive TYPE - Paths, Dots, Images, Texts - and
// BuildVectorPageAsync deliberately pours every layer's content into them
// together. Once a stroke has become a PdfVectorPath there is nothing on the
// record that says which Layer produced it, and no amount of reading it back
// can recover that. One PdfVectorPage per page IS the flattened thing a PSD
// must not be.
//
// So PSD keeps the PRIMITIVES and changes the CONTAINER: a PsdVectorPage is a
// list of PsdLayerArt buckets, each holding the same PdfVectorImage /
// PdfVectorDot / PdfVectorPath records the PDF and SVG emitters already read,
// plus the three facts the layer model owns - name, Hidden, Opacity. The
// records are shared verbatim, so a change to how a stroke is flattened reaches
// all three exporters at once; only the grouping is new.
//
// THREE MODEL PROMISES ARE KEPT HERE AND EACH IS AN INVERSION OF THE OBVIOUS
// CODE. They are listed together because getting any one of them backwards
// produces a file that opens fine and is quietly wrong:
//
//   1. Hidden, not Visible. PSD's layer flags carry the opposite polarity to
//      the field: flag BIT 1 SET MEANS HIDDEN. The two agree on the word
//      "visible" and disagree on which value it is, so the mapping is written
//      out explicitly below rather than left to look obvious.
//
//   2. Opacity is written to the layer's OPACITY BYTE and is never multiplied
//      into a pixel. 18.8 and 16.7 before it: the multiplier is a render-time
//      effect. A writer that baked it into the channel data AND wrote the byte
//      would apply it twice, and the user would find a 40% layer at 16%.
//      The ONE place EffectiveOpacity is applied is the merged composite, which
//      is a picture of the flattened page and is supposed to look faded.
//
//   3. The derived name goes in the file and NEVER back into the model.
//      Layer.Name is "" until the user chooses one; PageLayers.DisplayName
//      derives "Layer N" from position. A PSD layer must be called something,
//      so the derived name is what is written - but nothing here holds a
//      NotePage and nothing here can assign to Layer.Name.
//
// WHAT IS RASTER AND WHY. PSD layers are pixels. A PSD "shape layer" is a
// vector mask plus a solid-colour fill plus a path resource, and a PSD type
// layer is an Engine Data descriptor; neither is written here. Every layer this
// writes is an ordinary raster layer, so a PSD out of Quill is EDITABLE BY
// LAYER and not by stroke. That is the honest trade and it is the one PSD was
// asked for: "PSD's whole point here is that layers survive."
//
// BIG-ENDIAN. Every multi-byte field in a PSD is big-endian, including the
// ones inside the image resources. The helpers at the bottom are the only
// place bytes are ordered, so there is one place to be wrong.
//
// SECTION LENGTHS ARE PADDED. The layer info block is padded to a multiple of
// 2 and the layer NAME's Pascal string to a multiple of 4 - a different
// multiple from every other Pascal string in the format, which is the trap the
// spec buries in one clause.
// ===========================================================================

/// <summary>One PSD layer's worth of art, in the same primitive records the PDF
/// and SVG emitters read. Bottom-first within <see cref="PsdVectorPage.Layers"/>.
///
/// <para>Order inside a bucket is the order it rasterises: images, then dots,
/// then paths, then <see cref="TextImages"/> last - and <see cref="Paths"/>
/// carries a layer's shapes before its strokes. That is 18.5's within-a-layer
/// type order (shapes, strokes, texts) preserved exactly, with text on top the
/// way the PDF emitter puts it on top.</para>
///
/// <para><b><see cref="TextImages"/> holds PICTURES of text, not text</b>, and
/// the name says so because the limitation is real. This writer has no font
/// engine and writes no PSD type layer, so a text box arrives already
/// rasterised by whoever had a text engine to hand. "A PSD out of Quill is
/// editable by layer, not by stroke or by word" is therefore a fact about the
/// shape of this record rather than something the writer drops quietly.</para></summary>
public sealed record PsdLayerArt(
    string Name,
    bool Hidden,
    float Opacity,
    List<PdfVectorImage> Images,
    List<PdfVectorDot> Dots,
    List<PdfVectorPath> Paths,
    List<PdfVectorImage> TextImages)
{
    /// <summary>A solid fill of the WHOLE page, under this bucket's marks, as
    /// "#RRGGBB". Null for every ordinary layer.
    ///
    /// <para>It exists for one bucket: the paper. Without it a PSD opens with
    /// the ink floating on a transparency checkerboard, because a
    /// <see cref="PdfVectorPath"/> is stroked geometry and there is nothing in
    /// the vector page model that fills. An init property rather than an eighth
    /// positional parameter, so every construction site that has no ground
    /// stays unchanged.</para></summary>
    public string? Ground { get; init; }

    public static PsdLayerArt Empty(string name, bool hidden = false, float opacity = 1f)
        => new(name, hidden, opacity, new List<PdfVectorImage>(), new List<PdfVectorDot>(),
               new List<PdfVectorPath>(), new List<PdfVectorImage>());

    public int MarkCount => Images.Count + Dots.Count + Paths.Count + TextImages.Count;
}

/// <summary>A page as a STACK, which is the one thing PdfVectorPage is not.
/// Width/Height/OffsetX/OffsetY and Background mean exactly what they mean on
/// <see cref="PdfVectorPage"/>, so a caller that can build one can build this.
///
/// <para><see cref="Layers"/> is BOTTOM FIRST - the order
/// <see cref="Quill.Models.PageLayers.InOrder"/> yields and the order a PSD
/// stores its layer records in, so the two need no reversal between them. Take
/// that order; do not re-derive one.</para>
///
/// <para><see cref="Background"/> is the ground the MERGED COMPOSITE is painted
/// on. Nothing synthesises a layer for it: if the paper is to be a layer, the
/// caller puts one in <see cref="Layers"/>, and then the writer writes exactly
/// the layers it was handed.</para></summary>
public sealed record PsdVectorPage(
    double Width, double Height, double OffsetX, double OffsetY,
    string Background,
    List<PsdLayerArt> Layers);

/// <summary>Dependency-free PSD (Photoshop document) writer: one raster layer
/// per Quill layer, carrying its name, its visibility and its opacity, plus a
/// flattened composite for readers that do not parse the layer stack.</summary>
public static class PsdExporter
{
    /// <summary>PSD's own ceiling is 30,000 on a side. The tighter cap here is
    /// this writer's: it rasterises through full-page 32-bit buffers, so the
    /// working set is the limit long before the format is.</summary>
    public const int MaxSide = 16384;
    public const int MaxPixels = 1 << 24;   // 16.7 M, e.g. 4096 x 4096

    // Layer record flags. Bit 3 (0x08) says "bit 4 is meaningful"; bit 4 left
    // clear says the pixel data IS relevant to the document's appearance.
    private const byte FlagPhotoshop5Plus = 0x08;
    /// <summary>THE POLARITY TRAP. The spec labels bit 1 "visible"; what it
    /// actually stores is the opposite - a SET bit 1 is a HIDDEN layer, which is
    /// how Photoshop writes it and how every reader interprets it. Quill's field
    /// is <c>Hidden</c> for its own reasons (false is the zero value and costs
    /// nothing on disk), so the two happen to agree - but they agree by
    /// accident, and this constant exists so the mapping is asserted rather than
    /// assumed.</summary>
    private const byte FlagHidden = 0x02;

    /// <summary>Writes the page as a layered PSD.</summary>
    /// <exception cref="InvalidOperationException">The page is larger than this
    /// writer's raster budget. Thrown rather than silently downscaled: a PSD
    /// that is not the size the export promised is worse than an error.</exception>
    public static byte[] Write(PsdVectorPage page)
    {
        int w = Math.Max(1, (int)Math.Round(page.Width));
        int h = Math.Max(1, (int)Math.Round(page.Height));
        if (w > MaxSide || h > MaxSide || (long)w * h > MaxPixels)
            throw new InvalidOperationException(
                $"PSD export is capped at {MaxSide} px a side and {MaxPixels:N0} pixels; " +
                $"this page is {w} x {h}.");

        // The merged composite, premultiplied and OPAQUE from the first byte:
        // the paper is a colour, not a layer, so a reader that shows only the
        // composite still gets paper behind the ink.
        var merged = new byte[w * h * 4];
        var bg = ColorUtil.Parse(page.Background);
        for (int i = 0; i < merged.Length; i += 4)
        {
            merged[i] = bg.B; merged[i + 1] = bg.G; merged[i + 2] = bg.R; merged[i + 3] = 255;
        }

        var built = new List<Built>(page.Layers.Count);
        foreach (var art in page.Layers)
        {
            // Rasterised at FULL STRENGTH. The layer's own opacity is not in
            // these pixels and must not be - see promise 2 in the header.
            var buf = PsdRaster.RasterLayer(art, w, h, page.OffsetX, page.OffsetY);
            built.Add(Extract(art, buf, w, h));

            // ...and applied HERE, once, exactly as the renderer applies it:
            // hidden is the zero of the same number, so a hidden layer simply
            // contributes nothing to the flattened picture.
            float mul = art.Hidden ? 0f : Math.Clamp(art.Opacity, 0f, 1f);
            if (mul > 0f) PsdRaster.Over(merged, buf, mul);
        }

        var ms = new MemoryStream();
        WriteHeader(ms, w, h);
        WriteColourModeData(ms);
        WriteImageResources(ms);
        WriteLayerAndMaskInfo(ms, built);
        WriteMergedImage(ms, merged, w, h);
        return ms.ToArray();
    }

    // ---- one layer, reduced to what the file needs ------------------------

    private sealed class Built
    {
        public required string Name;
        public required bool Hidden;
        public required byte Opacity;
        public int Left, Top, Right, Bottom;
        /// <summary>Straight (un-premultiplied) A, R, G, B planes over the
        /// layer's own rect. Empty when the layer drew nothing.</summary>
        public byte[][] Planes = Array.Empty<byte[]>();
    }

    /// <summary>Tight bounds plus straight-alpha planes. A layer that drew
    /// nothing keeps an empty rect and writes zero-length channels, which is
    /// what Photoshop itself does and what 18.5 asks for: "empty layers are
    /// yielded: the user made them, and PSD export should write them."</summary>
    private static Built Extract(PsdLayerArt art, byte[] buf, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (buf[row + x * 4 + 3] == 0) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }

        var b = new Built
        {
            Name = art.Name,
            Hidden = art.Hidden,
            // FROM Layer.Opacity, NOT from EffectiveOpacity. A hidden layer at
            // 40% must come back at 40% when it is shown again, and folding the
            // two facts into one byte here would lose the second for good.
            Opacity = (byte)Math.Round(Math.Clamp(art.Opacity, 0f, 1f) * 255f),
        };
        if (x1 < x0 || y1 < y0) return b;   // nothing drawn

        b.Left = x0; b.Top = y0; b.Right = x1 + 1; b.Bottom = y1 + 1;
        int lw = b.Right - b.Left, lh = b.Bottom - b.Top;

        var a = new byte[lw * lh];
        var r = new byte[lw * lh];
        var g = new byte[lw * lh];
        var bl = new byte[lw * lh];
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < lw; x++)
            {
                int si = ((y + b.Top) * w + (x + b.Left)) * 4;
                int di = y * lw + x;
                byte av = buf[si + 3];
                a[di] = av;
                if (av == 0) continue;
                // PSD layer channels are STRAIGHT; the raster works
                // premultiplied because source-over only composes that way.
                bl[di] = (byte)Math.Min(255, buf[si] * 255 / av);
                g[di] = (byte)Math.Min(255, buf[si + 1] * 255 / av);
                r[di] = (byte)Math.Min(255, buf[si + 2] * 255 / av);
            }
        b.Planes = new[] { a, r, g, bl };
        return b;
    }

    // ---- 1. file header ---------------------------------------------------

    private static void WriteHeader(Stream s, int w, int h)
    {
        Ascii(s, "8BPS");
        U16(s, 1);                       // version 1 = PSD (2 would be PSB)
        s.Write(new byte[6], 0, 6);      // reserved, must be zero
        // THREE channels, not four. The composite is painted on the page's own
        // paper colour and is therefore opaque, so a document alpha channel
        // would carry nothing and would make every reader ask what the
        // transparency of a sheet of paper is.
        U16(s, 3);
        I32(s, h);                       // rows BEFORE columns
        I32(s, w);
        U16(s, 8);                       // bits per channel
        U16(s, 3);                       // colour mode 3 = RGB
    }

    // ---- 2. colour mode data ----------------------------------------------

    private static void WriteColourModeData(Stream s) => I32(s, 0);   // empty for RGB

    // ---- 3. image resources -----------------------------------------------

    /// <summary>One resource: 1005, ResolutionInfo, at Quill's own 96 ppi - the
    /// same number PdfExporter divides by to reach points. Without it a reader
    /// assumes 72 and the document opens a third too large in inches.</summary>
    private static void WriteImageResources(Stream s)
    {
        var res = new MemoryStream();
        Ascii(res, "8BIM");
        U16(res, 1005);
        res.WriteByte(0); res.WriteByte(0);      // empty Pascal name, padded to even
        I32(res, 16);                            // data length
        I32(res, 96 << 16);                      // hRes, 16.16 fixed
        U16(res, 1); U16(res, 1);                // hRes unit / width unit: px per inch
        I32(res, 96 << 16);                      // vRes
        U16(res, 1); U16(res, 1);

        var bytes = res.ToArray();
        I32(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    // ---- 4. layer and mask information ------------------------------------

    private static void WriteLayerAndMaskInfo(Stream s, List<Built> layers)
    {
        // Channel data first: a layer record has to state each channel's byte
        // length BEFORE the bytes exist in the file, so they are built up here
        // and appended after every record has been written.
        var channelBlobs = new List<byte[][]>(layers.Count);
        foreach (var l in layers)
        {
            var blobs = new byte[4][];
            for (int c = 0; c < 4; c++)
            {
                var m = new MemoryStream();
                U16(m, 0);                       // compression 0 = raw
                if (l.Planes.Length == 4) m.Write(l.Planes[c], 0, l.Planes[c].Length);
                blobs[c] = m.ToArray();
            }
            channelBlobs.Add(blobs);
        }

        var info = new MemoryStream();
        // Positive count. A NEGATIVE count would additionally promise that the
        // merged image's first alpha channel is the document transparency, and
        // this writer's composite is opaque.
        U16(info, layers.Count);

        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            I32(info, l.Top); I32(info, l.Left); I32(info, l.Bottom); I32(info, l.Right);

            U16(info, 4);                        // channel count
            short[] ids = { -1, 0, 1, 2 };       // alpha, R, G, B - ascending, as Photoshop writes them
            for (int c = 0; c < 4; c++)
            {
                U16(info, (ushort)ids[c]);
                I32(info, channelBlobs[i][c].Length);
            }

            Ascii(info, "8BIM");
            // 18.12 item 1: Quill's renderer has no blend modes, so every layer
            // is 'norm'. A file that claimed otherwise would be a field nothing
            // implements, which is the thing the model refused to store.
            Ascii(info, "norm");
            info.WriteByte(l.Opacity);
            info.WriteByte(0);                   // clipping: 0 = base
            info.WriteByte((byte)(FlagPhotoshop5Plus | (l.Hidden ? FlagHidden : 0)));
            info.WriteByte(0);                   // filler

            var extra = new MemoryStream();
            I32(extra, 0);                       // layer mask data: none
            I32(extra, 0);                       // blending ranges: none
            WritePascalPadded4(extra, l.Name);
            WriteLuni(extra, l.Name);
            var extraBytes = extra.ToArray();
            I32(info, extraBytes.Length);
            info.Write(extraBytes, 0, extraBytes.Length);
        }

        foreach (var blobs in channelBlobs)
            foreach (var blob in blobs)
                info.Write(blob, 0, blob.Length);

        var infoBytes = info.ToArray();
        int pad = infoBytes.Length % 2;          // layer info is padded to a multiple of 2

        // section = [u32 layerInfoLen][layerInfo][u32 globalLayerMaskLen]
        I32(s, 4 + infoBytes.Length + pad + 4);
        I32(s, infoBytes.Length + pad);
        s.Write(infoBytes, 0, infoBytes.Length);
        for (int i = 0; i < pad; i++) s.WriteByte(0);
        I32(s, 0);                               // global layer mask info: none
    }

    /// <summary>The legacy layer name: a Pascal string padded to a multiple of
    /// FOUR. Every other Pascal string in a PSD pads to two; this one does not,
    /// and a writer that pads it to two produces a file whose next field is read
    /// two bytes early.</summary>
    private static void WritePascalPadded4(Stream s, string name)
    {
        var raw = Encoding.Latin1.GetBytes(Sanitise(name));
        if (raw.Length > 255) raw = raw[..255];
        s.WriteByte((byte)raw.Length);
        s.Write(raw, 0, raw.Length);
        int total = 1 + raw.Length;
        int padding = (4 - total % 4) % 4;
        for (int i = 0; i < padding; i++) s.WriteByte(0);
    }

    /// <summary>The real name, in Unicode, as additional layer information.
    /// The Pascal string above is a Latin-1 fallback that a non-Latin layer name
    /// cannot survive; 'luni' is what Photoshop actually reads.</summary>
    private static void WriteLuni(Stream s, string name)
    {
        var chars = name.Length > 255 ? name[..255] : name;
        var data = new MemoryStream();
        I32(data, chars.Length);
        foreach (char c in chars) U16(data, c);   // UTF-16, big-endian
        var bytes = data.ToArray();               // 4 + 2n: always even, no padding needed
        Ascii(s, "8BIM");
        Ascii(s, "luni");
        I32(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string Sanitise(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Layer";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(c is >= ' ' and <= '~' ? c : '?');
        return sb.ToString();
    }

    // ---- 5. the merged composite ------------------------------------------

    /// <summary>The flattened picture, planar: every R, then every G, then every
    /// B. This is the one place a layer's opacity has been applied, and it is
    /// applied to a copy - see <see cref="Write"/>.</summary>
    private static void WriteMergedImage(Stream s, byte[] merged, int w, int h)
    {
        U16(s, 0);                               // compression 0 = raw
        // Source is premultiplied over an opaque ground, so alpha is 255
        // everywhere and premultiplied already equals straight.
        for (int c = 0; c < 3; c++)
        {
            int off = c == 0 ? 2 : c == 1 ? 1 : 0;   // BGRA in memory -> R, G, B out
            var plane = new byte[w * h];
            for (int i = 0; i < w * h; i++) plane[i] = merged[i * 4 + off];
            s.Write(plane, 0, plane.Length);
        }
    }

    // ---- big-endian primitives: the only place bytes are ordered -----------

    private static void Ascii(Stream s, string four)
    {
        var b = Encoding.ASCII.GetBytes(four);
        s.Write(b, 0, b.Length);
    }

    private static void U16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void I32(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }
}

// ===========================================================================
// THE RASTERISER
//
// PSD layers are pixels and Quill's page is geometry, so something has to turn
// one into the other. This is deliberately NOT Win2D: it runs in a console
// harness, which is the only way the bytes this produces can be read back and
// checked without a window. It is small on purpose - a polyline with round
// joins, a disc, and a blit - because those are the three marks the vector page
// model actually carries.
//
// Everything composites PREMULTIPLIED, because source-over is only associative
// that way and a layer is dozens of overlapping marks. PsdExporter
// un-premultiplies once, at the end, because PSD channels are straight.
// ===========================================================================
internal static class PsdRaster
{
    /// <summary>One layer's marks on a transparent full-page buffer, BGRA
    /// premultiplied, at FULL STRENGTH. The layer's own opacity is not applied
    /// here and must not be.</summary>
    public static byte[] RasterLayer(PsdLayerArt art, int w, int h, double offX, double offY)
    {
        var buf = new byte[w * h * 4];
        if (art.Ground is { Length: > 0 } ground)
        {
            var gc = ColorUtil.Parse(ground);
            for (int i = 0; i < buf.Length; i += 4)
            {
                buf[i] = gc.B; buf[i + 1] = gc.G; buf[i + 2] = gc.R; buf[i + 3] = 255;
            }
        }
        foreach (var im in art.Images) Blit(buf, w, h, im, offX, offY);
        foreach (var d in art.Dots)
            Disc(buf, w, h, d.X - (float)offX, d.Y - (float)offY, d.R, ColorUtil.Parse(d.Color), 1f);
        foreach (var p in art.Paths) Polyline(buf, w, h, p, offX, offY);
        // Text last, on top of the layer's own ink - the order the PDF content
        // stream writes its BT blocks in, so the two files agree about what
        // covers what.
        foreach (var im in art.TextImages) Blit(buf, w, h, im, offX, offY);
        return buf;
    }

    /// <summary>src over dst, both BGRA premultiplied, src scaled by
    /// <paramref name="mul"/>. Scaling all four channels is what makes a
    /// premultiplied buffer fade correctly.</summary>
    public static void Over(byte[] dst, byte[] src, float mul)
    {
        for (int i = 0; i < dst.Length; i += 4)
        {
            int sa = (int)Math.Round(src[i + 3] * mul);
            if (sa <= 0) continue;
            int inv = 255 - sa;
            for (int c = 0; c < 3; c++)
            {
                int sv = (int)Math.Round(src[i + c] * mul);
                dst[i + c] = (byte)Math.Min(255, sv + dst[i + c] * inv / 255);
            }
            dst[i + 3] = (byte)Math.Min(255, sa + dst[i + 3] * inv / 255);
        }
    }

    // ---- marks ------------------------------------------------------------

    /// <summary>A stroked polyline with round caps and joins. Coverage is
    /// accumulated with MAX across the segments and composited once, so a join
    /// does not darken where two segments overlap - which is what drawing each
    /// segment separately would do to every translucent highlighter stroke.</summary>
    private static void Polyline(byte[] buf, int w, int h, PdfVectorPath p, double offX, double offY)
    {
        if (p.Points.Count < 2) return;
        // The same floor the PDF and SVG emitters apply, so a hairline is the
        // same hairline in all three files.
        float width = Math.Max(0.35f, p.Width);
        float hw = width * 0.5f;

        var pts = new Vector2[p.Points.Count];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new Vector2(p.Points[i].X - (float)offX, p.Points[i].Y - (float)offY);

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var v in pts)
        {
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }
        int x0 = Math.Max(0, (int)Math.Floor(minX - hw - 1));
        int y0 = Math.Max(0, (int)Math.Floor(minY - hw - 1));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(maxX + hw + 1));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(maxY + hw + 1));
        if (x1 < x0 || y1 < y0) return;

        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        var cov = new float[bw * bh];

        int last = p.Closed ? pts.Length : pts.Length - 1;
        for (int i = 0; i < last; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            int sx0 = Math.Max(x0, (int)Math.Floor(Math.Min(a.X, b.X) - hw - 1));
            int sy0 = Math.Max(y0, (int)Math.Floor(Math.Min(a.Y, b.Y) - hw - 1));
            int sx1 = Math.Min(x1, (int)Math.Ceiling(Math.Max(a.X, b.X) + hw + 1));
            int sy1 = Math.Min(y1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + hw + 1));
            for (int y = sy0; y <= sy1; y++)
                for (int x = sx0; x <= sx1; x++)
                {
                    float d = GeometryUtil.DistToSegment(new Vector2(x + 0.5f, y + 0.5f), a, b);
                    float c = Math.Clamp(hw + 0.5f - d, 0f, 1f);
                    if (c <= 0f) continue;
                    int idx = (y - y0) * bw + (x - x0);
                    if (c > cov[idx]) cov[idx] = c;
                }
        }

        var col = ColorUtil.Parse(p.Color);
        float alpha = Math.Clamp(p.Alpha, 0f, 1f) * (col.A / 255f);
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                float c = cov[y * bw + x];
                if (c <= 0f) continue;
                Plot(buf, w, x + x0, y + y0, col, alpha * c);
            }
    }

    private static void Disc(byte[] buf, int w, int h, float cx, float cy, float r, Color col, float alpha)
    {
        int x0 = Math.Max(0, (int)Math.Floor(cx - r - 1));
        int y0 = Math.Max(0, (int)Math.Floor(cy - r - 1));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + r + 1));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + r + 1));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float c = Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                if (c > 0f) Plot(buf, w, x, y, col, alpha * c * (col.A / 255f));
            }
    }

    /// <summary>An embedded bitmap, turned about its shape centre the way the
    /// PDF matrix and the SVG rotate() turn it. Destination-driven and
    /// inverse-mapped, so a turned image has no holes; nearest-neighbour,
    /// because an export is 1:1 with the page and resampling it would be
    /// inventing detail.
    ///
    /// <para>Source bytes are taken as PREMULTIPLIED BGRA, which is what
    /// CanvasBitmap.GetPixelBytes hands back for a bitmap loaded the way
    /// BuildVectorPageAsync loads one. An opaque image - which is nearly all of
    /// them - is the same either way.</para></summary>
    private static void Blit(byte[] buf, int w, int h, PdfVectorImage im, double offX, double offY)
    {
        if (im.PixW <= 0 || im.PixH <= 0 || im.Bgra8.Length < im.PixW * im.PixH * 4) return;
        double iw = Math.Max(1e-6, im.W), ih = Math.Max(1e-6, im.H);
        double rad = -im.Angle * Math.PI / 180.0;    // inverse turn
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        // The four turned corners bound the destination.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (px, py) in new[] { (im.X, im.Y), (im.X + iw, im.Y), (im.X, im.Y + ih), (im.X + iw, im.Y + ih) })
        {
            var t = Turn(px, py, im.CentreX, im.CentreY, im.Angle);
            minX = Math.Min(minX, t.X); maxX = Math.Max(maxX, t.X);
            minY = Math.Min(minY, t.Y); maxY = Math.Max(maxY, t.Y);
        }
        int x0 = Math.Max(0, (int)Math.Floor(minX - offX));
        int y0 = Math.Max(0, (int)Math.Floor(minY - offY));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(maxX - offX));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(maxY - offY));

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double wx = x + 0.5 + offX, wy = y + 0.5 + offY;
                double dx = wx - im.CentreX, dy = wy - im.CentreY;
                double ux = im.CentreX + dx * cos - dy * sin;
                double uy = im.CentreY + dx * sin + dy * cos;
                int sx = (int)((ux - im.X) / iw * im.PixW);
                int sy = (int)((uy - im.Y) / ih * im.PixH);
                if (sx < 0 || sy < 0 || sx >= im.PixW || sy >= im.PixH) continue;
                int si = (sy * im.PixW + sx) * 4;
                byte sa = im.Bgra8[si + 3];
                if (sa == 0) continue;
                int di = (y * w + x) * 4;
                int inv = 255 - sa;
                buf[di] = (byte)Math.Min(255, im.Bgra8[si] + buf[di] * inv / 255);
                buf[di + 1] = (byte)Math.Min(255, im.Bgra8[si + 1] + buf[di + 1] * inv / 255);
                buf[di + 2] = (byte)Math.Min(255, im.Bgra8[si + 2] + buf[di + 2] * inv / 255);
                buf[di + 3] = (byte)Math.Min(255, sa + buf[di + 3] * inv / 255);
            }
    }

    /// <summary>The same arithmetic as PdfExporter.TurnWorld and
    /// InkSurface.RotatePoint - clockwise, degrees, about a world centre.</summary>
    private static (double X, double Y) Turn(double px, double py, double cx, double cy, double deg)
    {
        if (Math.Abs(deg) < 0.01) return (px, py);
        double r = deg * Math.PI / 180.0;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        double dx = px - cx, dy = py - cy;
        return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    private static void Plot(byte[] buf, int w, int x, int y, Color col, float alpha)
    {
        int sa = (int)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f);
        if (sa <= 0) return;
        int i = (y * w + x) * 4;
        int inv = 255 - sa;
        buf[i] = (byte)Math.Min(255, col.B * sa / 255 + buf[i] * inv / 255);
        buf[i + 1] = (byte)Math.Min(255, col.G * sa / 255 + buf[i + 1] * inv / 255);
        buf[i + 2] = (byte)Math.Min(255, col.R * sa / 255 + buf[i + 2] * inv / 255);
        buf[i + 3] = (byte)Math.Min(255, sa + buf[i + 3] * inv / 255);
    }
}
