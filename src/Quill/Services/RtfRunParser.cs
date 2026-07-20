using System.Text;
using System.Text.RegularExpressions;

namespace Quill.Services;

/// <summary>
/// Walks RichEditBox RTF into formatting runs, one list of runs per paragraph.
/// RtfToPlainText only reports the first \fs it meets, which is why vector export
/// used to flatten a mixed-size box to a single size; this keeps \fs, \b, \i and
/// \f per run so the SVG/PDF emitters can honour each one.
/// </summary>
public static class RtfRunParser
{
    private struct Fmt
    {
        public float Size;
        public int FontIdx;
        public bool Bold;
        public bool Italic;
    }

    /// <summary>Paragraphs of runs. An empty inner list is a blank line: it still
    /// occupies a baseline, matching how the RichEditBox lays the box out.</summary>
    public static List<List<PdfVectorTextRun>> Parse(string rtf, float defaultSize, string defaultFont)
    {
        var lines = new List<List<PdfVectorTextRun>>();
        var cur = new List<PdfVectorTextRun>();
        lines.Add(cur);
        if (string.IsNullOrEmpty(rtf)) return lines;

        var fonts = ParseFontTable(rtf);
        var fmt = new Fmt { Size = defaultSize, FontIdx = -1 };
        var stack = new Stack<Fmt>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            var name = fonts.TryGetValue(fmt.FontIdx, out var n) && n.Length > 0 ? n : defaultFont;
            cur.Add(new PdfVectorTextRun(sb.ToString(), fmt.Size, name, fmt.Bold, fmt.Italic));
            sb.Clear();
        }

        void NewLine()
        {
            Flush();
            cur = new List<PdfVectorTextRun>();
            lines.Add(cur);
        }

        for (int i = 0; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '{')
            {
                // header groups carry no body text: skip them whole
                bool skipped = false;
                foreach (var head in new[] { "{\\fonttbl", "{\\colortbl", "{\\stylesheet", "{\\*" })
                {
                    if (i + head.Length <= rtf.Length && rtf.AsSpan(i, head.Length).SequenceEqual(head))
                    {
                        int depth = 0;
                        for (; i < rtf.Length; i++)
                        {
                            if (rtf[i] == '{') depth++;
                            else if (rtf[i] == '}' && --depth == 0) break;
                        }
                        skipped = true;
                        break;
                    }
                }
                if (!skipped) { Flush(); stack.Push(fmt); }
                continue;
            }
            if (c == '}')
            {
                Flush();
                if (stack.Count > 0) fmt = stack.Pop();
                continue;
            }
            if (c == '\\')
            {
                if (i + 1 < rtf.Length && (rtf[i + 1] is '{' or '}' or '\\'))
                {
                    sb.Append(rtf[i + 1]);
                    i++;
                    continue;
                }
                if (i + 3 < rtf.Length && rtf[i + 1] == '\'' &&
                    byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte bv))
                {
                    sb.Append((char)bv);
                    i += 3;
                    continue;
                }
                i++;
                int ws = i;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                string word = rtf[ws..i];
                int ns = i;
                while (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i]))) i++;
                string num = rtf[ns..i];
                if (i >= rtf.Length || rtf[i] != ' ') i--;

                // a format switch ends the current run, never the current line
                switch (word)
                {
                    case "par":
                    case "line":
                        NewLine();
                        break;
                    case "tab":
                        sb.Append(' ');
                        break;
                    case "fs":
                        if (int.TryParse(num, out int hp) && hp > 4) { Flush(); fmt.Size = hp / 2f; }
                        break;
                    case "b":
                        Flush(); fmt.Bold = num != "0";
                        break;
                    case "i":
                        Flush(); fmt.Italic = num != "0";
                        break;
                    case "f":
                        if (int.TryParse(num, out int fi)) { Flush(); fmt.FontIdx = fi; }
                        break;
                    case "plain":
                        Flush(); fmt.Bold = false; fmt.Italic = false;
                        break;
                    case "u":
                        if (int.TryParse(num, out int uc))
                        {
                            sb.Append((char)Math.Abs(uc));
                            if (i + 1 < rtf.Length) i++;   // skip the '?' substitute
                        }
                        break;
                }
                continue;
            }
            if (c is '\r' or '\n') continue;
            sb.Append(c);
        }
        Flush();

        Normalise(lines);
        return lines;
    }

    /// <summary>Collapses runs of spaces and trims each line's outer edges — the
    /// same tidy-up RtfToPlainText does, but applied across run boundaries.</summary>
    private static void Normalise(List<List<PdfVectorTextRun>> lines)
    {
        foreach (var line in lines)
        {
            for (int i = 0; i < line.Count; i++)
                line[i] = line[i] with { Text = Regex.Replace(line[i].Text, " {2,}", " ") };
            if (line.Count > 0)
            {
                line[0] = line[0] with { Text = line[0].Text.TrimStart() };
                int last = line.Count - 1;
                line[last] = line[last] with { Text = line[last].Text.TrimEnd() };
            }
            line.RemoveAll(r => r.Text.Length == 0);
        }
        while (lines.Count > 0 && lines[0].Count == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Count == 0) lines.RemoveAt(lines.Count - 1);
    }

    /// <summary>\fN index -> family name, e.g. {\fonttbl{\f0\fnil\fcharset0 Lora;}}.</summary>
    private static Dictionary<int, string> ParseFontTable(string rtf)
    {
        var map = new Dictionary<int, string>();
        int at = rtf.IndexOf("{\\fonttbl", StringComparison.Ordinal);
        if (at < 0) return map;

        int depth = 0, end = -1;
        for (int i = at; i < rtf.Length; i++)
        {
            if (rtf[i] == '{') depth++;
            else if (rtf[i] == '}' && --depth == 0) { end = i; break; }
        }
        if (end < 0) return map;

        foreach (Match m in Regex.Matches(rtf[at..end], @"\\f(\d+)[^;]*?\s([^;\\{}]+);"))
            if (int.TryParse(m.Groups[1].Value, out int idx))
                map[idx] = m.Groups[2].Value.Trim();
        return map;
    }
}
