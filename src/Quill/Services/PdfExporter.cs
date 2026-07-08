using System.IO.Compression;
using System.Text;

namespace Quill.Services;

public record PdfPageImage(int PixelWidth, int PixelHeight, byte[] Bgra8Pixels);

// ---- vector export primitives (phase 2/3) ----
public record PdfVectorPath(List<(float X, float Y)> Points, string Color, float Width, bool Closed, float Alpha);
public record PdfVectorDot(float X, float Y, float R, string Color);
public record PdfVectorImage(double X, double Y, double W, double H, int PixW, int PixH, byte[] Bgra8);
public record PdfVectorText(float X, float Y, float Size, string Color, string Text, string Font);
public record PdfVectorPage(double Width, double Height, double OffsetX, double OffsetY, string Background,
                            List<PdfVectorPath> Paths, List<PdfVectorDot> Dots,
                            List<PdfVectorImage> Images, List<PdfVectorText> Texts);

/// <summary>
/// Minimal dependency-free PDF writer: each page is a full-bleed RGB image
/// (FlateDecode). Good enough for lecture-note export; vector PDF is a
/// roadmap item (see README).
/// </summary>
public static class PdfExporter
{
    public static byte[] Create(IReadOnlyList<PdfPageImage> pages)
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void WriteAscii(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void BeginObj(int _)
        {
            offsets.Add(ms.Position);
        }

        WriteAscii("%PDF-1.4\n");
        ms.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

        int total = 2 + pages.Count * 3;

        // 1: catalog
        BeginObj(1);
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: pages tree
        BeginObj(2);
        var kids = new StringBuilder();
        for (int i = 0; i < pages.Count; i++) kids.Append($"{3 + i * 3} 0 R ");
        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [ {kids}] /Count {pages.Count} >>\nendobj\n");

        for (int i = 0; i < pages.Count; i++)
        {
            var pg = pages[i];
            double wPt = pg.PixelWidth * 72.0 / 96.0;
            double hPt = pg.PixelHeight * 72.0 / 96.0;
            int pageObj = 3 + i * 3, contentObj = pageObj + 1, imgObj = pageObj + 2;

            BeginObj(pageObj);
            WriteAscii(
                $"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {Num(wPt)} {Num(hPt)}] " +
                $"/Resources << /XObject << /Im{i} {imgObj} 0 R >> >> " +
                $"/Contents {contentObj} 0 R >>\nendobj\n");

            string content = $"q {Num(wPt)} 0 0 {Num(hPt)} 0 0 cm /Im{i} Do Q";
            var contentBytes = Encoding.ASCII.GetBytes(content);
            BeginObj(contentObj);
            WriteAscii($"{contentObj} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            ms.Write(contentBytes, 0, contentBytes.Length);
            WriteAscii("\nendstream\nendobj\n");

            byte[] rgb = BgraToRgb(pg.Bgra8Pixels);
            byte[] compressed = Deflate(rgb);
            BeginObj(imgObj);
            WriteAscii(
                $"{imgObj} 0 obj\n<< /Type /XObject /Subtype /Image " +
                $"/Width {pg.PixelWidth} /Height {pg.PixelHeight} " +
                "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
                $"/Length {compressed.Length} >>\nstream\n");
            ms.Write(compressed, 0, compressed.Length);
            WriteAscii("\nendstream\nendobj\n");
        }

        long xrefPos = ms.Position;
        WriteAscii($"xref\n0 {total + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            WriteAscii($"{off:0000000000} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {total + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>True vector PDF: strokes, shapes and grid as scalable paths,
    /// with embedded images and text boxes as selectable Helvetica text.</summary>
    private class PdfFontResource
    {
        public string Name { get; set; } = "";
        public string ResName { get; set; } = "";
        public int FontId { get; set; }
        public int DescriptorId { get; set; }
        public int WidthsId { get; set; }
        public int TtfId { get; set; }
        public byte[] TtfBytes { get; set; } = Array.Empty<byte>();
        public Dictionary<char, int> Widths { get; set; } = new();
    }

    public static byte[] CreateVector(IReadOnlyList<PdfVectorPage> pages)
    {
        const double k = 72.0 / 96.0;   // world px -> PDF points
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void WriteAscii(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void BeginObj() => offsets.Add(ms.Position);

        // 1. Find all unique fonts used across all pages
        var fontFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pg in pages)
            foreach (var t in pg.Texts)
                if (!string.IsNullOrEmpty(t.Font))
                    fontFamilies.Add(t.Font);

        var fontMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fontObjectsList = new List<PdfFontResource>();

        // We will assign object IDs sequentially:
        // Catalog: 1
        // Pages: 2
        // ExtGState: 3
        // Helvetica fallback: 4
        int nextObjId = 5;

        foreach (var fontName in fontFamilies)
        {
            var chars = new HashSet<char>();
            foreach (var pg in pages)
                foreach (var t in pg.Texts)
                    if (string.Equals(t.Font, fontName, StringComparison.OrdinalIgnoreCase))
                        foreach (var c in t.Text)
                            chars.Add(c);

            var ttfBytes = FontSubsetter.SubsetFont(fontName, chars);
            if (ttfBytes != null)
            {
                var widths = FontSubsetter.GetGlyphWidths(fontName, chars);
                var res = new PdfFontResource
                {
                    Name = fontName,
                    ResName = $"F{fontObjectsList.Count + 1}",
                    TtfId = nextObjId++,
                    DescriptorId = nextObjId++,
                    WidthsId = nextObjId++,
                    FontId = nextObjId++,
                    TtfBytes = ttfBytes,
                    Widths = widths
                };
                fontObjectsList.Add(res);
                fontMap[fontName] = res.FontId;
            }
            else
            {
                // Fallback to Helvetica
                fontMap[fontName] = 4;
            }
        }

        // ---- object numbering: Catalog 1, Pages 2, ExtGState 3, Helvetica 4,
        //      then font objects, then per page: page, content, one object per image ----
        var pageIds = new int[pages.Count];
        var contentIds = new int[pages.Count];
        var imageIds = new int[pages.Count][];
        for (int i = 0; i < pages.Count; i++)
        {
            pageIds[i] = nextObjId++;
            contentIds[i] = nextObjId++;
            imageIds[i] = new int[pages[i].Images.Count];
            for (int j = 0; j < imageIds[i].Length; j++) imageIds[i][j] = nextObjId++;
        }
        int total = nextObjId - 1;

        WriteAscii("%PDF-1.4\n");
        ms.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

        // Catalog
        BeginObj();
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // Pages
        BeginObj();
        var kids = new StringBuilder();
        foreach (var id in pageIds) kids.Append($"{id} 0 R ");
        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [ {kids}] /Count {pages.Count} >>\nendobj\n");

        // ExtGState
        BeginObj();
        WriteAscii("3 0 obj\n<< /Type /ExtGState /CA 0.35 /ca 0.35 >>\nendobj\n");

        // Helvetica fallback
        BeginObj();
        WriteAscii("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        // Write custom TTF font objects
        foreach (var f in fontObjectsList)
        {
            byte[] compTtf = Deflate(f.TtfBytes);
            BeginObj();
            WriteAscii($"{f.TtfId} 0 obj\n<< /Length {compTtf.Length} /Length1 {f.TtfBytes.Length} /Filter /FlateDecode >>\nstream\n");
            ms.Write(compTtf, 0, compTtf.Length);
            WriteAscii("\nendstream\nendobj\n");

            BeginObj();
            WriteAscii($"{f.DescriptorId} 0 obj\n<< /Type /FontDescriptor /FontName /{f.Name.Replace(" ", "")} /Flags 32 /FontBBox [-1000 -1000 2000 2000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 800 /StemV 80 /FontFile2 {f.TtfId} 0 R >>\nendobj\n");

            var wsb = new StringBuilder();
            wsb.Append("[ ");
            for (int charCode = 32; charCode <= 255; charCode++)
            {
                char c = (char)charCode;
                int wVal = f.Widths.TryGetValue(c, out int width) ? width : 600;
                wsb.Append(wVal).Append(' ');
            }
            wsb.Append("]");
            BeginObj();
            WriteAscii($"{f.WidthsId} 0 obj\n{wsb}\nendobj\n");

            BeginObj();
            WriteAscii($"{f.FontId} 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /{f.Name.Replace(" ", "")} /FirstChar 32 /LastChar 255 /Widths {f.WidthsId} 0 R /FontDescriptor {f.DescriptorId} 0 R /Encoding /WinAnsiEncoding >>\nendobj\n");
        }

        // Write pages
        for (int i = 0; i < pages.Count; i++)
        {
            var pg = pages[i];
            double wPt = pg.Width * k, hPt = pg.Height * k;

            var xobjects = new StringBuilder();
            for (int j = 0; j < pg.Images.Count; j++)
                xobjects.Append($"/Im{j} {imageIds[i][j]} 0 R ");

            var fontResList = new StringBuilder();
            fontResList.Append("/F0 4 0 R ");
            foreach (var f in fontObjectsList)
            {
                fontResList.Append($"/{f.ResName} {f.FontId} 0 R ");
            }

            BeginObj();
            WriteAscii(
                $"{pageIds[i]} 0 obj\n<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {Num(wPt)} {Num(hPt)}] " +
                $"/Resources << /ExtGState << /GHl 3 0 R >> /Font << {fontResList}>> " +
                $"/XObject << {xobjects}>> >> " +
                $"/Contents {contentIds[i]} 0 R >>\nendobj\n");

            string X(double wx) => Num((wx - pg.OffsetX) * k);
            string Y(double wy) => Num(hPt - (wy - pg.OffsetY) * k);

            var sb = new StringBuilder();
            sb.Append(Rgb(pg.Background, "rg")).Append('\n');
            sb.Append($"0 0 {Num(wPt)} {Num(hPt)} re f\n");
            sb.Append("1 J 1 j\n");

            foreach (var d in pg.Dots)
            {
                sb.Append(Rgb(d.Color, "rg")).Append('\n');
                double r = d.R * k;
                sb.Append($"{Num((d.X - pg.OffsetX) * k - r)} {Num(hPt - (d.Y - pg.OffsetY) * k - r)} {Num(r * 2)} {Num(r * 2)} re f\n");
            }

            for (int j = 0; j < pg.Images.Count; j++)
            {
                var im = pg.Images[j];
                sb.Append($"q {Num(im.W * k)} 0 0 {Num(im.H * k)} {X(im.X)} {Y(im.Y + im.H)} cm /Im{j} Do Q\n");
            }

            foreach (var p in pg.Paths)
            {
                if (p.Points.Count < 2) continue;
                bool translucent = p.Alpha < 0.99f;
                if (translucent) sb.Append("q /GHl gs\n");
                sb.Append(Rgb(p.Color, "RG")).Append('\n');
                sb.Append(Num(Math.Max(0.35, p.Width * k))).Append(" w\n");
                sb.Append($"{X(p.Points[0].X)} {Y(p.Points[0].Y)} m\n");
                for (int j = 1; j < p.Points.Count; j++)
                    sb.Append($"{X(p.Points[j].X)} {Y(p.Points[j].Y)} l\n");
                if (p.Closed) sb.Append("h\n");
                sb.Append("S\n");
                if (translucent) sb.Append("Q\n");
            }

            foreach (var t in pg.Texts)
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                string fRes = "F0";
                if (!string.IsNullOrEmpty(t.Font) && fontMap.TryGetValue(t.Font, out int fId))
                {
                    var matching = fontObjectsList.FirstOrDefault(x => x.FontId == fId);
                    if (matching != null) fRes = matching.ResName;
                }

                sb.Append($"BT /{fRes} ").Append(Num(t.Size * k)).Append(" Tf ")
                  .Append(Rgb(t.Color, "rg")).Append(' ')
                  .Append($"1 0 0 1 {X(t.X)} {Y(t.Y)} Tm (")
                  .Append(EscapePdfText(t.Text)).Append(") Tj ET\n");
            }

            byte[] raw = Encoding.ASCII.GetBytes(sb.ToString());
            byte[] compressed = Deflate(raw);
            BeginObj();
            WriteAscii($"{contentIds[i]} 0 obj\n<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            ms.Write(compressed, 0, compressed.Length);
            WriteAscii("\nendstream\nendobj\n");

            for (int j = 0; j < pg.Images.Count; j++)
            {
                var im = pg.Images[j];
                byte[] rgb = BgraToRgb(im.Bgra8);
                byte[] comp = Deflate(rgb);
                BeginObj();
                WriteAscii(
                    $"{imageIds[i][j]} 0 obj\n<< /Type /XObject /Subtype /Image " +
                    $"/Width {im.PixW} /Height {im.PixH} " +
                    "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
                    $"/Length {comp.Length} >>\nstream\n");
                ms.Write(comp, 0, comp.Length);
                WriteAscii("\nendstream\nendobj\n");
            }
        }

        long xrefPos = ms.Position;
        WriteAscii($"xref\n0 {total + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            WriteAscii($"{off:0000000000} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {total + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string EscapePdfText(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c is '\r' or '\n') sb.Append(' ');
            else if (c < 32) { }
            else if (c > 255) sb.Append('?');   // Helvetica/WinAnsi can't encode it
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // "#RRGGBB" -> "r g b rg" / "r g b RG" PDF colour operator
    private static string Rgb(string hex, string op)
    {
        byte r = 0, g = 0, b = 0;
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length == 8) h = h[2..];   // drop alpha
            r = Convert.ToByte(h[..2], 16);
            g = Convert.ToByte(h.Substring(2, 2), 16);
            b = Convert.ToByte(h.Substring(4, 2), 16);
        }
        catch { }
        return $"{Num(r / 255.0)} {Num(g / 255.0)} {Num(b / 255.0)} {op}";
    }

    private static string Num(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] BgraToRgb(byte[] bgra)
    {
        int n = bgra.Length / 4;
        var rgb = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            rgb[i * 3] = bgra[i * 4 + 2];
            rgb[i * 3 + 1] = bgra[i * 4 + 1];
            rgb[i * 3 + 2] = bgra[i * 4];
        }
        return rgb;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var outMs = new MemoryStream();
        using (var z = new ZLibStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return outMs.ToArray();
    }
}
