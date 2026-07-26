using System.IO.Compression;
using System.Text;

namespace Quill.Services;

/// <summary>
/// The `.qtile` on-disk format and its crash-safe write protocol
/// (OILPAINT-SPEC §4.1, §4.2). Deliberately free of any Win2D or WinUI
/// dependency: this is the one part of the paint feature that can destroy user
/// content, so it stays testable in isolation.
/// </summary>
public static class PaintTileCodec
{
    public const int TileSize = 512;
    public const int ColourBytes = TileSize * TileSize * 4;   // BGRA8, as the GPU holds it
    public const int HeightGpuBytes = TileSize * TileSize * 8; // RGBA16F
    public const int HeightDiskBytes = TileSize * TileSize * 2; // U16, alpha channel only
    private const int HeaderBytes = 14;

    /// <summary>Pixels read back from one tile, ready to compress and write.</summary>
    public sealed record TileBlob(int Tx, int Ty, byte[] Colour, byte[] HeightU16);

    // =======================================================================
    // Height packing. The GPU tile is RGBA16F carrying height in ALPHA; disk
    // keeps only that channel, quantised to U16 — a quarter of the bytes and
    // still far past the point where the specular derivative would terrace.
    // =======================================================================

    public static byte[] PackHeight(byte[] gpuRgba16F)
    {
        if (gpuRgba16F.Length != HeightGpuBytes)
            throw new ArgumentException($"height readback is {gpuRgba16F.Length}, expected {HeightGpuBytes}");
        var outp = new byte[HeightDiskBytes];
        for (int i = 0, o = 0; i < gpuRgba16F.Length; i += 8, o += 2)
        {
            ushort half = (ushort)(gpuRgba16F[i + 6] | (gpuRgba16F[i + 7] << 8));
            float h = (float)BitConverter.UInt16BitsToHalf(half);
            ushort q = (ushort)Math.Clamp(MathF.Round(h * 65535f), 0f, 65535f);
            outp[o] = (byte)(q & 0xFF);
            outp[o + 1] = (byte)(q >> 8);
        }
        return outp;
    }

    public static byte[] UnpackHeight(byte[] disk)
    {
        if (disk.Length != HeightDiskBytes)
            throw new ArgumentException($"height blob is {disk.Length}, expected {HeightDiskBytes}");
        var outp = new byte[HeightGpuBytes];
        for (int o = 0, i = 0; i < disk.Length; i += 2, o += 8)
        {
            ushort q = (ushort)(disk[i] | (disk[i + 1] << 8));
            ushort half = BitConverter.HalfToUInt16Bits((Half)(q / 65535f));
            byte lo = (byte)(half & 0xFF), hi = (byte)(half >> 8);
            // grey (h, h, h, h) — a legal premultiplied value, and the lighting
            // effects read height from ALPHA
            outp[o] = lo; outp[o + 1] = hi;
            outp[o + 2] = lo; outp[o + 3] = hi;
            outp[o + 4] = lo; outp[o + 5] = hi;
            outp[o + 6] = lo; outp[o + 7] = hi;
        }
        return outp;
    }

    // =======================================================================
    // "QTIL" | version | codec | int colourLen | int heightLen | colour | height
    // No PNG anywhere: PNG is straight alpha, and premultiply round-tripping
    // every session compounds precision loss at low alpha (at a = 8/255 only
    // ~3 bits of colour survive). Raw + Deflate is lossless and tags its codec
    // per block, so a future codec change costs no format version bump.
    // =======================================================================

    public static byte[] Encode(TileBlob b)
    {
        var colour = Deflate(b.Colour);
        var height = Deflate(b.HeightU16);
        using var ms = new MemoryStream(colour.Length + height.Length + HeaderBytes);
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write((byte)'Q'); w.Write((byte)'T'); w.Write((byte)'I'); w.Write((byte)'L');
            w.Write((byte)1);   // version
            w.Write((byte)0);   // codec 0 = deflate
            w.Write(colour.Length);
            w.Write(height.Length);
            w.Write(colour);
            w.Write(height);
        }
        return ms.ToArray();
    }

    /// <summary>Throws on anything that is not a complete, self-consistent tile —
    /// a partially written file must be REJECTED, never half-loaded.</summary>
    public static (byte[] Colour, byte[] HeightU16) Decode(byte[] file)
    {
        if (file.Length < HeaderBytes || file[0] != 'Q' || file[1] != 'T' || file[2] != 'I' || file[3] != 'L')
            throw new InvalidDataException("bad magic");
        if (file[4] != 1) throw new InvalidDataException($"unsupported version {file[4]}");
        if (file[5] != 0) throw new InvalidDataException($"unsupported codec {file[5]}");
        int cLen = BitConverter.ToInt32(file, 6);
        int hLen = BitConverter.ToInt32(file, 10);
        if (cLen < 0 || hLen < 0 || (long)HeaderBytes + cLen + hLen != file.Length)
            throw new InvalidDataException("truncated");
        var colour = Inflate(file, HeaderBytes, cLen, ColourBytes);
        var height = Inflate(file, HeaderBytes + cLen, hLen, HeightDiskBytes);
        return (colour, height);
    }

    // =======================================================================
    // Undo blobs (OILPAINT-SPEC §6.1). The paint undo action keeps the RAW GPU
    // bytes of a touched tile — BGRA8 colour and RGBA16F height — deflated. Going
    // through the disk codec's U16 height quantisation would make undo->redo
    // lossy, so undo compresses the exact GPU bytes instead: lossless and
    // byte-exact both ways, and the grey (h,h,h,h) height packs down hard because
    // three of its four channels are duplicates.
    // =======================================================================

    public static byte[] CompressRaw(byte[] raw) => Deflate(raw);

    public static byte[] DecompressRaw(byte[] compressed, int expected)
        => Inflate(compressed, 0, compressed.Length, expected);

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var dz = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            dz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] src, int offset, int count, int expected)
    {
        using var ms = new MemoryStream(src, offset, count, writable: false);
        using var dz = new DeflateStream(ms, CompressionMode.Decompress);
        var outp = new byte[expected];
        int got = 0;
        while (got < expected)
        {
            int n = dz.Read(outp, got, expected - got);
            if (n <= 0) break;
            got += n;
        }
        if (got != expected) throw new InvalidDataException($"inflate short: {got}/{expected}");
        return outp;
    }

    // =======================================================================
    // Crash-safe write protocol (§4.2) — NEVER in place
    // =======================================================================

    /// <summary>
    /// Temp file, flushed to the platter, then swapped in atomically with a
    /// rotating .bak. File.WriteAllBytes over a live tile is FORBIDDEN: a crash
    /// mid-write truncates it, and unlike ink there is no vector fallback to
    /// reconstruct raster from. Three of this user's paintings died to that.
    /// </summary>
    public static void WriteAtomic(string final, byte[] payload)
    {
        var tmp = final + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                       64 * 1024, FileOptions.WriteThrough))
        {
            fs.Write(payload, 0, payload.Length);
            fs.Flush(flushToDisk: true);     // to the PLATTER, not the OS cache
        }
        if (File.Exists(final)) File.Replace(tmp, final, final + ".bak");
        else File.Move(tmp, final);
    }

    /// <summary>Reads a tile, falling back to the .bak that File.Replace rotated
    /// out. A kill mid-write therefore costs at most the last flush, never the
    /// tile.</summary>
    public static (byte[] Colour, byte[] HeightU16) ReadTile(string dir, int tx, int ty, out bool usedBak)
    {
        var final = Path.Combine(dir, $"{tx}_{ty}.qtile");
        usedBak = false;
        try { return Decode(File.ReadAllBytes(final)); }
        catch
        {
            usedBak = true;
            return Decode(File.ReadAllBytes(final + ".bak"));
        }
    }
}
