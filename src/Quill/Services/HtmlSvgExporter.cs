using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Quill.Services;

/// <summary>
/// Converts the vector page model (the same PdfVectorPage data the vector PDF
/// export uses) into true-vector SVG, and wraps pages into a standalone HTML
/// document. Ink and shapes stay crisp at any zoom; text stays selectable.
/// </summary>
public static class HtmlSvgExporter
{
    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string PageToSvg(PdfVectorPage pg)
    {
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" ")
          .Append("viewBox=\"0 0 ").Append(N(pg.Width)).Append(' ').Append(N(pg.Height)).Append("\" ")
          .Append("width=\"").Append(N(pg.Width)).Append("\" height=\"").Append(N(pg.Height)).Append("\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"").Append(Esc(pg.Background)).Append("\"/>");

        string X(double wx) => N(wx - pg.OffsetX);
        string Y(double wy) => N(wy - pg.OffsetY);

        foreach (var im in pg.Images)
        {
            try
            {
                var png = MiniPng.FromBgra(im.Bgra8, im.PixW, im.PixH);
                sb.Append("<image x=\"").Append(X(im.X)).Append("\" y=\"").Append(Y(im.Y))
                  .Append("\" width=\"").Append(N(im.W)).Append("\" height=\"").Append(N(im.H))
                  .Append("\" href=\"data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("\"/>");
            }
            catch { /* an unencodable image must not sink the whole export */ }
        }

        foreach (var d in pg.Dots)
            sb.Append("<circle cx=\"").Append(X(d.X)).Append("\" cy=\"").Append(Y(d.Y))
              .Append("\" r=\"").Append(N(d.R)).Append("\" fill=\"").Append(Esc(d.Color)).Append("\"/>");

        foreach (var p in pg.Paths)
        {
            if (p.Points.Count < 2) continue;
            var path = new StringBuilder();
            path.Append('M').Append(X(p.Points[0].X)).Append(' ').Append(Y(p.Points[0].Y));
            for (int i = 1; i < p.Points.Count; i++)
                path.Append('L').Append(X(p.Points[i].X)).Append(' ').Append(Y(p.Points[i].Y));
            if (p.Closed) path.Append('Z');
            sb.Append("<path d=\"").Append(path).Append("\" fill=\"none\" stroke=\"").Append(Esc(p.Color))
              .Append("\" stroke-width=\"").Append(N(Math.Max(0.35, p.Width)))
              .Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\"");
            if (p.Alpha < 0.99f) sb.Append(" stroke-opacity=\"").Append(N(p.Alpha)).Append('"');
            sb.Append("/>");
        }

        foreach (var t in pg.Texts)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            sb.Append("<text x=\"").Append(X(t.X)).Append("\" y=\"").Append(Y(t.Y))
              .Append("\" font-size=\"").Append(N(t.Size))
              .Append("\" fill=\"").Append(Esc(t.Color))
              .Append("\" font-family=\"").Append(Esc(string.IsNullOrEmpty(t.Font) ? "Segoe UI" : t.Font))
              .Append(", sans-serif\">").Append(Esc(t.Text)).Append("</text>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string BuildHtml(string title, IReadOnlyList<PdfVectorPage> pages)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>")
          .Append(Esc(title)).Append("</title><style>")
          .Append("body{margin:0;background:#55534f;display:flex;flex-direction:column;align-items:center;gap:24px;padding:24px;}")
          .Append("svg{box-shadow:0 2px 14px rgba(0,0,0,.35);max-width:100%;height:auto;border-radius:4px;}")
          .Append("</style></head><body>");
        foreach (var pg in pages) sb.Append(PageToSvg(pg));
        sb.Append("</body></html>");
        return sb.ToString();
    }
}

/// <summary>Minimal dependency-free PNG encoder (8-bit RGBA, filter 0) used to
/// embed pasted images as data URIs inside SVG/HTML exports.</summary>
internal static class MiniPng
{
    public static byte[] FromBgra(byte[] bgra, int w, int h)
    {
        var raw = new byte[(w * 4 + 1) * h];
        int di = 0;
        for (int y = 0; y < h; y++)
        {
            raw[di++] = 0;   // filter: none
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                raw[di++] = bgra[si + 2];   // R
                raw[di++] = bgra[si + 1];   // G
                raw[di++] = bgra[si];       // B
                raw[di++] = bgra[si + 3];   // A
            }
        }

        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zl = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
                zl.Write(raw, 0, raw.Length);
            idat = z.ToArray();
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w);
        WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteBE(byte[] buf, int at, uint v)
    {
        buf[at] = (byte)(v >> 24); buf[at + 1] = (byte)(v >> 16);
        buf[at + 2] = (byte)(v >> 8); buf[at + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        var crcB = new byte[4];
        WriteBE(crcB, 0, crc);
        s.Write(crcB, 0, 4);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
