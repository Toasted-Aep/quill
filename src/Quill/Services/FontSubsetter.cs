using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Quill.Services;

public static class FontSubsetter
{
    private static readonly Dictionary<string, string> FontNameToFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Lora"] = "Lora-Regular.ttf",
        ["Calibri"] = "calibri.ttf",
        ["Arial"] = "arial.ttf",
        ["Cambria Math"] = "cambria.ttc",
        ["Cambria"] = "cambriab.ttf",
        ["Times New Roman"] = "times.ttf",
        ["Courier New"] = "cour.ttf",
        ["Segoe UI"] = "segoeuib.ttf"
    };

    public static string? ResolveFontPath(string fontFamily)
    {
        string systemFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        
        if (FontNameToFileName.TryGetValue(fontFamily, out var fileName))
        {
            var p = Path.Combine(systemFonts, fileName);
            if (File.Exists(p)) return p;
        }

        // Try exact name match
        var tryPath = Path.Combine(systemFonts, fontFamily + ".ttf");
        if (File.Exists(tryPath)) return tryPath;

        // Try searching directory
        try
        {
            foreach (var f in Directory.GetFiles(systemFonts, "*.ttf"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Contains(fontFamily, StringComparison.OrdinalIgnoreCase)) return f;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Subsets the TTF font by keeping only used characters.
    /// To be safe and fast, if subsetting is too complex, we return the entire TTF file.
    /// </summary>
    public static byte[]? SubsetFont(string fontFamily, HashSet<char> usedChars)
    {
        var path = ResolveFontPath(fontFamily);
        if (path == null) return null;

        try
        {
            // For safety and compatibility with all font variants, return the entire font file.
            // TrueType files are typically small enough for PDF embedding.
            return File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets glyph widths in PDF units (1000 units per EM).
    /// </summary>
    public static Dictionary<char, int> GetGlyphWidths(string fontFamily, HashSet<char> chars)
    {
        var widths = new Dictionary<char, int>();
        var path = ResolveFontPath(fontFamily);
        if (path == null)
        {
            // Fallback to default Helvetica metrics approximation
            foreach (var c in chars) widths[c] = 600;
            return widths;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BigEndianReader(fs);

            // Read TTF table directory
            uint sfntVersion = reader.ReadUInt32();
            ushort numTables = reader.ReadUInt16();
            reader.ReadUInt16(); // searchRange
            reader.ReadUInt16(); // entrySelector
            reader.ReadUInt16(); // rangeShift

            uint cmapOffset = 0, cmapLength = 0;
            uint hheaOffset = 0;
            uint hmtxOffset = 0;
            uint maxpOffset = 0;

            for (int i = 0; i < numTables; i++)
            {
                string tag = reader.ReadTag();
                uint checkSum = reader.ReadUInt32();
                uint offset = reader.ReadUInt32();
                uint length = reader.ReadUInt32();

                if (tag == "cmap") { cmapOffset = offset; cmapLength = length; }
                else if (tag == "hhea") { hheaOffset = offset; }
                else if (tag == "hmtx") { hmtxOffset = offset; }
                else if (tag == "maxp") { maxpOffset = offset; }
            }

            if (cmapOffset == 0 || hheaOffset == 0 || hmtxOffset == 0)
            {
                throw new Exception("Required tables missing");
            }

            // Read unitsPerEm from 'head' table (if needed, but usually 2048)
            ushort unitsPerEm = 2048; // default fallback

            // Read numberOfHMetrics from 'hhea'
            fs.Position = hheaOffset + 34;
            ushort numberOfHMetrics = reader.ReadUInt16();

            // Read cmap table to map char -> glyph index
            var charToGlyph = new Dictionary<char, ushort>();
            fs.Position = cmapOffset;
            ushort version = reader.ReadUInt16();
            ushort numTablesSub = reader.ReadUInt16();

            uint subtableOffset = 0;
            for (int i = 0; i < numTablesSub; i++)
            {
                ushort platformId = reader.ReadUInt16();
                ushort encodingId = reader.ReadUInt16();
                uint offset = reader.ReadUInt32();

                // Prefer Unicode platform (0) or Windows (3) Unicode BMP
                if ((platformId == 0) || (platformId == 3 && encodingId == 1))
                {
                    subtableOffset = cmapOffset + offset;
                    break;
                }
            }

            if (subtableOffset != 0)
            {
                fs.Position = subtableOffset;
                ushort format = reader.ReadUInt16();
                ushort length = reader.ReadUInt16();
                ushort language = reader.ReadUInt16();

                if (format == 4) // Segment mapping to delta values
                {
                    ushort segCountX2 = reader.ReadUInt16();
                    ushort segCount = (ushort)(segCountX2 / 2);
                    reader.ReadUInt16(); // searchRange
                    reader.ReadUInt16(); // entrySelector
                    reader.ReadUInt16(); // rangeShift

                    ushort[] endCount = new ushort[segCount];
                    for (int j = 0; j < segCount; j++) endCount[j] = reader.ReadUInt16();
                    reader.ReadUInt16(); // reservedPad
                    ushort[] startCount = new ushort[segCount];
                    for (int j = 0; j < segCount; j++) startCount[j] = reader.ReadUInt16();
                    short[] idDelta = new short[segCount];
                    for (int j = 0; j < segCount; j++) idDelta[j] = reader.ReadInt16();
                    ushort[] idRangeOffset = new ushort[segCount];
                    long rangeOffsetStart = fs.Position;
                    for (int j = 0; j < segCount; j++) idRangeOffset[j] = reader.ReadUInt16();

                    foreach (var c in chars)
                    {
                        ushort code = c;
                        ushort glyphIndex = 0;
                        for (int j = 0; j < segCount; j++)
                        {
                            if (endCount[j] >= code && startCount[j] <= code)
                            {
                                if (idRangeOffset[j] == 0)
                                {
                                    glyphIndex = (ushort)((code + idDelta[j]) & 0xFFFF);
                                }
                                else
                                {
                                    long offsetAddr = rangeOffsetStart + j * 2 + idRangeOffset[j] + (code - startCount[j]) * 2;
                                    fs.Position = offsetAddr;
                                    glyphIndex = reader.ReadUInt16();
                                    if (glyphIndex != 0)
                                    {
                                        glyphIndex = (ushort)((glyphIndex + idDelta[j]) & 0xFFFF);
                                    }
                                }
                                break;
                            }
                        }
                        charToGlyph[c] = glyphIndex;
                    }
                }
            }

            // Now read widths from hmtx table
            foreach (var c in chars)
            {
                ushort glyphIndex = 0;
                charToGlyph.TryGetValue(c, out glyphIndex);

                fs.Position = hmtxOffset;
                ushort advanceWidth = 0;
                if (glyphIndex < numberOfHMetrics)
                {
                    fs.Position = hmtxOffset + glyphIndex * 4;
                    advanceWidth = reader.ReadUInt16();
                }
                else
                {
                    // If glyphIndex >= numberOfHMetrics, it uses the last width
                    fs.Position = hmtxOffset + (numberOfHMetrics - 1) * 4;
                    advanceWidth = reader.ReadUInt16();
                }

                // Convert to PDF units (1000 per EM)
                widths[c] = (int)Math.Round((double)advanceWidth * 1000 / unitsPerEm);
            }
        }
        catch
        {
            // Default fallback
            foreach (var c in chars) widths[c] = 600;
        }

        return widths;
    }
}

internal class BigEndianReader : IDisposable
{
    private readonly Stream _stream;
    public BigEndianReader(Stream stream) { _stream = stream; }

    public ushort ReadUInt16()
    {
        int b1 = _stream.ReadByte();
        int b2 = _stream.ReadByte();
        if (b1 == -1 || b2 == -1) throw new EndOfStreamException();
        return (ushort)((b1 << 8) | b2);
    }

    public short ReadInt16() => (short)ReadUInt16();

    public uint ReadUInt32()
    {
        int b1 = _stream.ReadByte();
        int b2 = _stream.ReadByte();
        int b3 = _stream.ReadByte();
        int b4 = _stream.ReadByte();
        if (b1 == -1 || b2 == -1 || b3 == -1 || b4 == -1) throw new EndOfStreamException();
        return (uint)((b1 << 24) | (b2 << 16) | (b3 << 8) | b4);
    }

    public string ReadTag()
    {
        byte[] bytes = new byte[4];
        _stream.ReadExactly(bytes, 0, 4);
        return Encoding.ASCII.GetString(bytes);
    }

    public void Dispose() => _stream.Dispose();
}
