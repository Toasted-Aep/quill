using System.IO.Compression;
using System.Text;
using Quill.Controls;

namespace PaperProof;

/// <summary>
/// Renders every paper and measures it. This is the §8 acceptance test.
///
/// <para>A blank page measures sigma ~0.0. The previous paper implementation
/// measured 0.72 textured and 0.72 blank — mathematically indistinguishable from
/// nothing — and that went unnoticed because the only way to see a tile was to
/// launch the app and squint. So the numbers now come from a headless run over
/// the linked shipping source, and the thresholds are asserted, not eyeballed.</para>
/// </summary>
internal static class Program
{
    // §8. Papers with no entry here are rendered and measured but not gated:
    // Rippled and Darkprint have no published floor, and Plain White and
    // Transparent are not grain textures at all.
    //
    // The published floors describe grain at FULL amplitude. §10.6 turned the
    // whole set down because the user found it too noticeable, so the gate
    // scales with PaperGrain.GrainScale rather than being rewritten - the point
    // of these floors is to catch a texture that has become INVISIBLE, and that
    // question is still asked correctly at any amplitude. Lower the dial again
    // and the gate follows; delete the dial and the original numbers return.
    private static readonly Dictionary<PaperKind, double> Thresholds =
        new Dictionary<PaperKind, double>
        {
            [PaperKind.Lightweight] = 4.0,
            [PaperKind.Heavyweight] = 7.0,
            [PaperKind.Crumpled] = 7.0,
            [PaperKind.Blueprint] = 3.0,
            [PaperKind.BrownPaper] = 3.0,
        }.ToDictionary(e => e.Key, e => e.Value * PaperGrain.GrainScale);

    private static int Main(string[] args)
    {
        // Only a bare path counts as the output directory; anything that looks
        // like a switch is ignored, because "dotnet run --nologo" hands its own
        // flags straight through to us and the first run wrote a folder called
        // "--nologo".
        string? arg = args.FirstOrDefault(a => !a.StartsWith('-'));
        string outDir = Path.GetFullPath(arg ?? Path.Combine(RepoRoot(), "docs", "paper-proof"));
        Directory.CreateDirectory(outDir);

        int size = PaperGrain.Tile;
        var kinds = Enum.GetValues<PaperKind>();
        var rows = new List<Row>();

        // The control. A flat fill of Lightweight's own ground, measured through
        // exactly the same code path as every texture below. If this does not
        // come out at 0.00 the measurement itself is broken and no other number
        // on the table means anything.
        {
            var (r, g, b) = PaperGrain.GroundRgb(PaperKind.Lightweight);
            var flat = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                flat[i * 4] = b; flat[i * 4 + 1] = g; flat[i * 4 + 2] = r; flat[i * 4 + 3] = 255;
            }
            var stats = Measure(flat, size);
            rows.Add(new Row("(control: blank page)", stats, null, "-"));
        }

        foreach (var kind in kinds)
        {
            var (gr, gg, gb) = PaperGrain.GroundRgb(kind);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bgra = PaperGrain.Bake(kind, gr, gg, gb);
            sw.Stop();

            var stats = Measure(bgra, size);
            Thresholds.TryGetValue(kind, out double floor);
            rows.Add(new Row(
                kind.ToString(),
                stats,
                Thresholds.ContainsKey(kind) ? floor : null,
                $"#{gr:X2}{gg:X2}{gb:X2}"));

            string png = Path.Combine(outDir, $"{(int)kind:00}-{kind}.png");
            File.WriteAllBytes(png, EncodePng(bgra, size, size));

            // Seam check: the tile is drawn with CanvasEdgeBehavior.Wrap, so
            // column 0 must continue column 511 and row 0 must continue row 511.
            // Compare the wrap step against the average neighbour step inside
            // the tile; a ratio near 1 means the join is invisible.
            // Only a ratio well ABOVE 1 is a defect: it means the join is a
            // harder edge than any boundary inside the tile. Below 1 just means
            // the two edges happen to be more alike than average, which is
            // invisible. Transparent reads high by construction — the seam lands
            // on a checker boundary, which is the pattern, not a flaw.
            var seam = SeamRatio(bgra, size);
            string seamFlag = kind != PaperKind.Transparent && (seam.X > 1.6 || seam.Y > 1.6) ? "  <-- SEAM" : "";
            var decay = ScaleDecay(bgra, size);

            Console.WriteLine(
                $"  baked {kind,-12} {sw.ElapsedMilliseconds,4} ms   seam x{seam.X:F2} y{seam.Y:F2}   " +
                $"scales {string.Join(" ", decay.Select(d => d.ToString("F2")))}{seamFlag}");
        }

        // One contact sheet at 1:1 so the whole set can be judged in a single
        // look, which is how the swatch row will actually be seen.
        WriteContactSheet(outDir, size);

        Console.WriteLine();
        Console.WriteLine("  paper           ground     mean      sigma    min   max   floor   verdict");
        Console.WriteLine("  " + new string('-', 76));
        bool ok = true;
        foreach (var row in rows)
        {
            string floor = row.Floor is null ? "  -  " : row.Floor.Value.ToString("F1").PadLeft(5);
            string verdict;
            if (row.Floor is null) verdict = "";
            else if (row.Stats.Sigma > row.Floor.Value) verdict = "PASS";
            else { verdict = "FAIL"; ok = false; }

            Console.WriteLine(
                $"  {row.Name,-15} {row.Ground,-9} {row.Stats.Mean,7:F2} {row.Stats.Sigma,8:F2} " +
                $"{row.Stats.Min,5:F0} {row.Stats.Max,5:F0}   {floor}   {verdict}");
        }
        Console.WriteLine();
        Console.WriteLine(ok ? "  ALL THRESHOLDS MET" : "  THRESHOLDS NOT MET");
        Console.WriteLine($"  PNGs in {outDir}");
        return ok ? 0 : 1;
    }

    /// <summary>Walks up from the binary to the checkout, so the PNGs land in the
    /// repo whatever working directory dotnet run was invoked from.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Quill", "Controls", "PaperGrain.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private readonly record struct Row(string Name, Stats Stats, double? Floor, string Ground);

    private readonly record struct Stats(double Mean, double Sigma, double Min, double Max);

    /// <summary>Per-pixel luminance mean and standard deviation, on the 0..255
    /// scale, using Rec.709 luma over the sRGB bytes — the same weights §6 uses
    /// and the same scale the §8 thresholds are quoted in.</summary>
    private static Stats Measure(byte[] bgra, int size)
    {
        int n = size * size;
        double sum = 0, sum2 = 0, min = 255, max = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            double y = 0.2126 * bgra[o + 2] + 0.7152 * bgra[o + 1] + 0.0722 * bgra[o];
            sum += y; sum2 += y * y;
            if (y < min) min = y;
            if (y > max) max = y;
        }
        double mean = sum / n;
        double var = Math.Max(0, sum2 / n - mean * mean);
        return new Stats(mean, Math.Sqrt(var), min, max);
    }

    /// <summary>
    /// Sigma measured again after repeated 2x2 box downsampling — the objective
    /// form of "grain must have structure at more than one scale".
    ///
    /// <para>Averaging 2x2 independent samples divides the standard deviation by
    /// exactly 2, so pure salt-and-pepper noise decays 1.00 / 0.50 / 0.25 / 0.12
    /// and vanishes into nothing by the fourth step. Grain with real structure
    /// keeps far more, because the low-frequency octaves survive the averaging.
    /// A texture can hit its sigma floor on single-pixel hash alone and still
    /// look like television static; this is the column that catches it.</para>
    /// </summary>
    private static double[] ScaleDecay(byte[] bgra, int size, int levels = 4)
    {
        var lum = new double[size * size];
        for (int i = 0; i < lum.Length; i++)
            lum[i] = 0.2126 * bgra[i * 4 + 2] + 0.7152 * bgra[i * 4 + 1] + 0.0722 * bgra[i * 4];

        var outp = new double[levels + 1];
        outp[0] = Sigma(lum);
        int n = size;
        for (int l = 1; l <= levels; l++)
        {
            int m = n / 2;
            var next = new double[m * m];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                    next[y * m + x] = 0.25 * (lum[(2 * y) * n + 2 * x] + lum[(2 * y) * n + 2 * x + 1]
                                            + lum[(2 * y + 1) * n + 2 * x] + lum[(2 * y + 1) * n + 2 * x + 1]);
            lum = next; n = m;
            outp[l] = Sigma(lum);
        }
        // report each level as a fraction of the full-resolution sigma
        for (int l = levels; l >= 0; l--) outp[l] = outp[0] <= 0 ? 0 : outp[l] / outp[0];
        return outp;
    }

    private static double Sigma(double[] v)
    {
        double s = 0, s2 = 0;
        foreach (double x in v) { s += x; s2 += x * x; }
        double mean = s / v.Length;
        return Math.Sqrt(Math.Max(0, s2 / v.Length - mean * mean));
    }

    /// <summary>Mean absolute luma step across the wrap seam, divided by the mean
    /// absolute step between neighbours inside the tile. 1.0 means the seam is
    /// statistically identical to any other column boundary, i.e. invisible.</summary>
    private static (double X, double Y) SeamRatio(byte[] bgra, int size)
    {
        static double Luma(byte[] p, int i) =>
            0.2126 * p[i * 4 + 2] + 0.7152 * p[i * 4 + 1] + 0.0722 * p[i * 4];

        double seamX = 0, seamY = 0, innerX = 0, innerY = 0;
        for (int y = 0; y < size; y++) seamX += Math.Abs(Luma(bgra, y * size) - Luma(bgra, y * size + size - 1));
        for (int x = 0; x < size; x++) seamY += Math.Abs(Luma(bgra, x) - Luma(bgra, (size - 1) * size + x));
        for (int y = 0; y < size; y++)
            for (int x = 1; x < size; x++)
                innerX += Math.Abs(Luma(bgra, y * size + x) - Luma(bgra, y * size + x - 1));
        for (int y = 1; y < size; y++)
            for (int x = 0; x < size; x++)
                innerY += Math.Abs(Luma(bgra, y * size + x) - Luma(bgra, (y - 1) * size + x));

        double avgInnerX = innerX / (size * (size - 1.0));
        double avgInnerY = innerY / (size * (size - 1.0));
        return (avgInnerX <= 0 ? 1 : (seamX / size) / avgInnerX,
                avgInnerY <= 0 ? 1 : (seamY / size) / avgInnerY);
    }

    /// <summary>A 3x3 contact sheet of 220px crops at 1:1, captioned by position,
    /// so the nine papers can be compared at true scale in one image.</summary>
    private static void WriteContactSheet(string outDir, int size)
    {
        const int cell = 220, gap = 8, cols = 3;
        var kinds = Enum.GetValues<PaperKind>();
        int rows = (kinds.Length + cols - 1) / cols;
        int w = cols * cell + (cols + 1) * gap;
        int h = rows * cell + (rows + 1) * gap;
        var sheet = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            sheet[i * 4] = 0x30; sheet[i * 4 + 1] = 0x30; sheet[i * 4 + 2] = 0x30; sheet[i * 4 + 3] = 255;
        }

        for (int k = 0; k < kinds.Length; k++)
        {
            var kind = kinds[k];
            var (gr, gg, gb) = PaperGrain.GroundRgb(kind);
            var tile = PaperGrain.Bake(kind, gr, gg, gb);
            int cx = gap + (k % cols) * (cell + gap);
            int cy = gap + (k / cols) * (cell + gap);
            // crop from the tile centre so a crumple lands on folds rather than
            // on whatever happens to sit at the origin
            int ox = (size - cell) / 2, oy = (size - cell) / 2;
            for (int y = 0; y < cell; y++)
            {
                for (int x = 0; x < cell; x++)
                {
                    int s = ((oy + y) * size + ox + x) * 4;
                    int d = ((cy + y) * w + cx + x) * 4;
                    sheet[d] = tile[s]; sheet[d + 1] = tile[s + 1];
                    sheet[d + 2] = tile[s + 2]; sheet[d + 3] = 255;
                }
            }
        }
        File.WriteAllBytes(Path.Combine(outDir, "00-contact-sheet.png"), EncodePng(sheet, w, h));
    }

    // =====================================================================
    // Minimal PNG encoder
    // =====================================================================
    // Written out rather than pulled from a package: the harness has to run with
    // no external dependency so it stays trivially runnable in CI or by hand.

    private static byte[] EncodePng(byte[] bgra, int w, int h)
    {
        // filter byte 0 (None) + RGB triplets, per scanline
        var raw = new byte[h * (1 + w * 3)];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            raw[p++] = 0;
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                raw[p++] = bgra[o + 2];   // R
                raw[p++] = bgra[o + 1];   // G
                raw[p++] = bgra[o];       // B
            }
        }

        using var zlib = new MemoryStream();
        zlib.WriteByte(0x78);             // CM=8, CINFO=7
        zlib.WriteByte(0x9C);             // default compression, no dict, FCHECK ok
        using (var deflate = new DeflateStream(zlib, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        WriteBE(zlib, Adler32(raw));

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new MemoryStream();
        WriteBE(ihdr, (uint)w);
        WriteBE(ihdr, (uint)h);
        ihdr.WriteByte(8);    // bit depth
        ihdr.WriteByte(2);    // colour type: truecolour
        ihdr.WriteByte(0);    // deflate
        ihdr.WriteByte(0);    // adaptive filtering
        ihdr.WriteByte(0);    // no interlace
        Chunk(png, "IHDR", ihdr.ToArray());
        Chunk(png, "IDAT", zlib.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        WriteBE(s, (uint)data.Length);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crcBuf = new byte[t.Length + data.Length];
        Buffer.BlockCopy(t, 0, crcBuf, 0, t.Length);
        Buffer.BlockCopy(data, 0, crcBuf, t.Length, data.Length);
        WriteBE(s, Crc32(crcBuf));
    }

    private static void WriteBE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static uint Adler32(byte[] d)
    {
        uint a = 1, b = 0;
        foreach (byte x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] d)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in d) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
