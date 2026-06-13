using System.IO.Compression;
using System.Text;

namespace LectureInk.Services;

public record PdfPageImage(int PixelWidth, int PixelHeight, byte[] Bgra8Pixels);

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
