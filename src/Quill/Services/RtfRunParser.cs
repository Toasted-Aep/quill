using System.Text;
using Quill.Helpers;
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
        /// <summary>CONCEPTS-REF 43: the run's own colour, "#RRGGBB", or null for
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
                        // 43: the run's colour. \cf0 is RTF's "auto" and is not in
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

    /// <summary>The colour every non-empty run names, in document order. Null is
    /// RTF's "auto".</summary>
    public static List<string?> RunColours(string? rtf)
    {
        var list = new List<string?>();
        if (string.IsNullOrEmpty(rtf)) return list;
        foreach (var line in Parse(rtf, 16f, ""))
            foreach (var run in line)
                if (run.Text.Length > 0)
                    list.Add(run.Colour);
        return list;
    }

    /// <summary>CONCEPTS-REF 43.2: the inks that reach a stored document from the
    /// MACHINERY rather than from a person. A run wearing one of these named a
    /// colour, but nobody chose it.
    ///
    /// <para><b>Measured, not assumed.</b> 43.2 read all 106 stored notes in the
    /// library and every one of them carries an explicit run colour that nobody
    /// picked - 68 of them <c>#FAF9F5</c> and 38 <c>#FFFFFF</c>. Both arrive on
    /// their own: <c>BuildTextUi</c> writes the page's ink into the document's
    /// default character format, so RichEdit stores it as a <c>\colortbl</c>
    /// entry with <c>\cf1</c> on the runs; and <c>#FFFFFF</c> is the value
    /// 2.2/33 caught <c>TextControlForegroundFocused</c> resolving to under the
    /// dark theme, written into files before <c>PinEditorBrushes</c> closed
    /// it.</para>
    ///
    /// <para><b>Why this list and not a longer one.</b> Only values actually
    /// observed in stored documents are here. Reading a colour as machine-set
    /// when a person did choose it costs them their colour, so the list stays at
    /// what was measured. The cost of the three that ARE here is small and
    /// bounded: a run deliberately painted <c>#FFFFFF</c> comes out
    /// <c>#FAF9F5</c> on a dark page - five levels on one channel - and
    /// <c>#141413</c> on a light one, where the white it asked for would have
    /// been invisible.</para></summary>
    public static bool IsMachineInk(string? hex) =>
        hex is { Length: > 0 } &&
        (Same(hex, ColorUtil.ToHex(PageTheme.TextInkOnLight)) ||
         Same(hex, ColorUtil.ToHex(PageTheme.TextInkOnDark)) ||
         Same(hex, "#FFFFFF"));

    private static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>CONCEPTS-REF 43.2: true when at least one run of this box names a
    /// colour a person chose - non-null, and not one of
    /// <see cref="IsMachineInk"/>'s.
    ///
    /// <para>A box that fails this behaves in every respect exactly as it did
    /// before per-run colour existed, which is the whole of the compatibility
    /// guarantee. 106 of the 106 notes 43.2 sampled fail it.</para></summary>
    public static bool HasChosenRunColour(string? rtf)
    {
        foreach (var c in RunColours(rtf))
            if (c is { Length: > 0 } && !IsMachineInk(c)) return true;
        return false;
    }

    /// <summary>CONCEPTS-REF 43.2: the runs of a parsed box with every colour
    /// nobody chose set back to null, so a run that named none and a run that
    /// wore a machine ink are the same thing to an emitter - <i>take the box's
    /// answer</i>.
    ///
    /// <para>Applied ONCE, where the model meets the export records, so the four
    /// emitters need no opinion about Quill's ink policy and cannot arrive at
    /// four different ones. <paramref name="boxHasOwnColour"/> is 25.2's
    /// field-wins-over-the-RTF rule reaching the exporters: a box carrying a
    /// whole-box colour has already had every run stamped on screen, so honouring
    /// a stale run colour here would put a different answer in the file from the
    /// one on the canvas - the exact split 25 closed.</para></summary>
    public static void ResolveChosenColours(List<List<PdfVectorTextRun>> lines, bool boxHasOwnColour)
    {
        for (int li = 0; li < lines.Count; li++)
            for (int ri = 0; ri < lines[li].Count; ri++)
            {
                var run = lines[li][ri];
                if (run.Colour is null) continue;
                if (boxHasOwnColour || IsMachineInk(run.Colour))
                    lines[li][ri] = run with { Colour = null };
            }
    }

    /// <summary>CONCEPTS-REF 43.3: each coloured run's character span inside a
    /// plain-text rendering of the same box, or <b>null</b> if any run cannot be
    /// placed.
    ///
    /// <para><c>DrawTextElement</c> lays its glyphs out from
    /// <c>RtfToPlainText</c>, which is a different walker from <see cref="Parse"/>
    /// and normalises whitespace differently. Colouring by run index would drop
    /// colour on the wrong characters wherever the two disagree. So the runs are
    /// matched INTO the text actually being drawn, left to right, and a run that
    /// cannot be found abandons the whole mapping.</para>
    ///
    /// <para>Returning null rather than a partial answer is the point: the caller
    /// then draws exactly what it drew before per-run colour existed. The failure
    /// mode of this function is "no change", which is the only failure mode a
    /// rendering path should be allowed to have.</para></summary>
    public static List<(int Start, int Length, string Colour)>? MapColourSpans(
        string? plain, List<List<PdfVectorTextRun>> lines)
    {
        var spans = new List<(int, int, string)>();
        if (string.IsNullOrEmpty(plain)) return null;
        int cursor = 0;
        foreach (var line in lines)
            foreach (var run in line)
            {
                if (run.Text.Length == 0) continue;
                int at = plain.IndexOf(run.Text, cursor, StringComparison.Ordinal);
                if (at < 0) return null;
                if (run.Colour is { Length: > 0 }) spans.Add((at, run.Text.Length, run.Colour));
                cursor = at + run.Text.Length;
            }
        return spans;
    }

    /// <summary>Every character of a box paired with the colour its run names,
    /// with paragraph breaks as one uncoloured '\n' each, so two copies of the
    /// same words line up index for index.</summary>
    private static (string Text, List<string?> Colours) ColourPerChar(string? rtf)
    {
        var text = new StringBuilder();
        var cols = new List<string?>();
        if (string.IsNullOrEmpty(rtf)) return ("", cols);

        var lines = Parse(rtf, 16f, "");
        for (int li = 0; li < lines.Count; li++)
        {
            if (li > 0) { text.Append('\n'); cols.Add(null); }
            foreach (var run in lines[li])
                foreach (char ch in run.Text) { text.Append(ch); cols.Add(run.Colour); }
        }
        return (text.ToString(), cols);
    }

    /// <summary>CONCEPTS-REF 43.1: true when <paramref name="live"/> is
    /// <paramref name="stored"/> with a CHOSEN run colour destroyed - the same
    /// characters in the same order, and at least one of them no longer carrying
    /// the colour the stored copy gave it.
    ///
    /// <para>43.1 measures what this is for: <c>RichEditBox.Foreground</c> is
    /// pushed into the RichEdit document when the control's template applies and
    /// flattens every run colour <c>SetText</c> has just put back. This is the
    /// question <c>BuildTextUi</c> asks on <c>Loaded</c> before restoring the
    /// document it was built from.</para>
    ///
    /// <para><b>Three refusals, each closing a way a restore could do harm.</b>
    /// A box with no CHOSEN colour answers false, so no existing note is touched
    /// and the flatten goes on doing the useful half of its job - repainting a
    /// machine-inked box in the CURRENT page's ink, which is what lets a note
    /// survive its page being recoloured. A box whose CHARACTERS have moved
    /// answers false, so a restore can never overwrite a keystroke that beat
    /// <c>Loaded</c>. And a document that has lost nothing answers false, so the
    /// common path does no work at all.</para></summary>
    /// <summary>CONCEPTS-REF 46.3: RichEdit's own trailing paragraph.
    /// <c>GetText(FormatRtf)</c> on a live box returns the document the box was
    /// built from plus ONE more paragraph break, so a straight character
    /// comparison against the stored copy disagreed every time and
    /// <see cref="RunColoursLost"/>'s "the characters have moved" refusal fired
    /// on every chosen-colour box, on every load. Trimming the tail off BOTH
    /// sides makes that guard answer the question it was written to ask — has
    /// the user typed — rather than the question the control's own formatting
    /// accidentally asked. A real keystroke still moves a non-trailing
    /// character and is still refused.</summary>
    private static void TrimTrailingBreaks(ref string text, List<string?> cols)
    {
        int n = text.Length;
        while (n > 0 && (text[n - 1] == '\n' || text[n - 1] == '\r' || text[n - 1] == '\0')) n--;
        if (n == text.Length) return;
        text = text[..n];
        if (cols.Count > n) cols.RemoveRange(n, cols.Count - n);
    }

    public static bool RunColoursLost(string? stored, string? live)
    {
        if (!HasChosenRunColour(stored)) return false;
        var (wantText, want) = ColourPerChar(stored);
        var (haveText, have) = ColourPerChar(live);
        TrimTrailingBreaks(ref wantText, want);
        TrimTrailingBreaks(ref haveText, have);
        if (want.Count != have.Count || wantText != haveText) return false;
        for (int i = 0; i < want.Count; i++)
            if (want[i] is { } w && !IsMachineInk(w) && !Same(w, have[i]))
                return true;
        return false;
    }

    /// <summary>§46.2 PROBE ONLY: which of <see cref="RunColoursLost"/>'s
    /// refusals a given pair took. Same order, same tests, no side effects — it
    /// exists because "returned false" is not a diagnosis, and every call site
    /// is behind <c>GeometryProbe.On</c>.</summary>
    public static string RunColoursLostWhy(string? stored, string? live)
    {
        static string Q(string s) => "\"" +
            s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0") + "\"";

        if (!HasChosenRunColour(stored)) return "REFUSED: stored has no chosen run colour";
        var (wantText, want) = ColourPerChar(stored);
        var (haveText, have) = ColourPerChar(live);
        TrimTrailingBreaks(ref wantText, want);
        TrimTrailingBreaks(ref haveText, have);
        if (want.Count != have.Count)
            return $"REFUSED: colour-slot count differs, want={want.Count} have={have.Count} " +
                   $"wantText={Q(wantText)} haveText={Q(haveText)}";
        if (wantText != haveText)
            return $"REFUSED: characters differ, wantText={Q(wantText)} haveText={Q(haveText)}";
        for (int i = 0; i < want.Count; i++)
            if (want[i] is { } w && !IsMachineInk(w) && !Same(w, have[i]))
                return $"LOST at index {i}: {w} -> {have[i] ?? "(null)"}";
        return "REFUSED: every chosen colour is still present";
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

    /// <summary>CONCEPTS-REF 43: \cfN index -> "#RRGGBB", from
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
