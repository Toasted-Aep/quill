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
        /// <summary>CONCEPTS-REF 41: the run's own colour, "#RRGGBB", or null for
        /// RTF's "auto" (<c>\cf0</c>, or no <c>\cf</c> at all) which means "take
        /// the box's answer". Null is the whole of the pre-41 behaviour, so a run
        /// that names no colour still comes out exactly as it did.</summary>
        public string? Colour;
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
        var colours = ParseColorTable(rtf);
        var fmt = new Fmt { Size = defaultSize, FontIdx = -1 };
        var stack = new Stack<Fmt>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            var name = fonts.TryGetValue(fmt.FontIdx, out var n) && n.Length > 0 ? n : defaultFont;
            cur.Add(new PdfVectorTextRun(sb.ToString(), fmt.Size, name, fmt.Bold, fmt.Italic, fmt.Colour));
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
                    case "cf":
                        // 41: the run's colour. \cf0 is RTF's "auto" and is not in
                        // the table - it means "whatever the control's default
                        // is", which for Quill is the box's own answer, so null.
                        Flush();
                        fmt.Colour = int.TryParse(num, out int ci) && colours.TryGetValue(ci, out var ch)
                            ? ch : null;
                        break;
                    case "plain":
                        // \plain resets CHARACTER formatting, colour included.
                        Flush(); fmt.Bold = false; fmt.Italic = false; fmt.Colour = null;
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

    /// <summary>CONCEPTS-REF 41: \cfN index -> "#RRGGBB", from
    /// <c>{\colortbl ;\red0\green128\blue0;…}</c>.
    ///
    /// <para>Entries are semicolon-terminated and <b>index 0 is the blank one
    /// before the first semicolon</b> — RTF's "auto", which is why it is left out
    /// of the map rather than given a value. Any entry that is blank or that does
    /// not carry all three channels is skipped for the same reason: an index that
    /// is absent means "the box's own answer", which is exactly the behaviour
    /// every run had before this method existed.</para></summary>
    private static Dictionary<int, string> ParseColorTable(string rtf)
    {
        var map = new Dictionary<int, string>();
        int at = rtf.IndexOf("{\\colortbl", StringComparison.Ordinal);
        if (at < 0) return map;

        int depth = 0, end = -1;
        for (int i = at; i < rtf.Length; i++)
        {
            if (rtf[i] == '{') depth++;
            else if (rtf[i] == '}' && --depth == 0) { end = i; break; }
        }
        if (end < 0) return map;

        string body = rtf[(at + "{\\colortbl".Length)..end];
        var entries = body.Split(';');
        // The trailing split piece after the last ';' is not an entry.
        for (int idx = 0; idx < entries.Length - 1; idx++)
        {
            var m = Regex.Match(entries[idx], @"\\red(\d+)\\green(\d+)\\blue(\d+)");
            if (!m.Success) continue;                       // blank entry = auto
            if (byte.TryParse(m.Groups[1].Value, out byte r) &&
                byte.TryParse(m.Groups[2].Value, out byte g) &&
                byte.TryParse(m.Groups[3].Value, out byte b))
                map[idx] = $"#{r:X2}{g:X2}{b:X2}";
        }
        return map;
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
