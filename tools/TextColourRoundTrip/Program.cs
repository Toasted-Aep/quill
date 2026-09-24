// CONCEPTS-REF 25: text colour, measured where a console can measure it.
//
// The brief that produced 25 named the failure it exists to prevent in one
// sentence: "perfect on screen and black in the file". Three of 25's four
// surfaces need a window. The fourth is the file, and the file is exactly the
// one that had the hole, so this is where the proof goes.
//
// Three subjects:
//
//   1. PageTheme.TextInk - the rule that decides what colour a box with NO
//      colour of its own comes out. It used to be four copies of one ternary on
//      ColorUtil.IsDark. 25.4 replaced it with a best-of-contrast pick and
//      quoted numbers for why; those numbers are RECOMPUTED here from the real
//      function, and the alternative that was proposed and rejected is swept
//      alongside so the rejection is a measurement.
//
//   2. The colour reaching the file. Real PdfExporter, real HtmlSvgExporter,
//      the PDF content stream inflated back out of the bytes and the SVG parsed
//      as XML - not string-matched. The PRE-25 behaviour is built alongside and
//      is required to FAIL, so this harness is shown able to go red.
//
//   3. THE PRECONDITION, 43.1. A stored per-run colour is destroyed when a box
//      is built - RichEditBox.Foreground is pushed into the document when the
//      template applies and flattens every run colour SetText has just put
//      back. Export cannot carry what storage discards, so the DECISION the
//      restore turns on is measured here, against the exact pair of documents
//      40.5's own probe printed.
//
// What this CANNOT see, stated here rather than left to be assumed: the live
// RichEditBox, the 16.7 veil and the Win2D raster. Nothing in this file is
// evidence about any of those three. In particular 43.1's restore is a
// SetText inside a Loaded handler, and only the DECISION in front of it is
// measured below - whether WinUI then keeps what it was handed is a screen
// question and 43.5 says so.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Windows.UI;
using Quill.Helpers;
using Quill.Services;

int failures = 0;
var log = new List<string>();

void Check(string label, bool ok, string detail = "")
{
    log.Add($"{(ok ? "PASS" : "FAIL")} {label}{(detail.Length > 0 ? "   -- " + detail : "")}");
    if (!ok) failures++;
}

static Color Hex(string h) => ColorUtil.Parse(h);

// ===========================================================================
// PART 1 - THE PAGE'S INK RULE
// ===========================================================================
//
// The nine shipped paper grounds (PaperGrain.GroundRgb), Quill's own default
// page background, and four grounds chosen to sit where the two candidate rules
// disagree. Named here rather than imported because PaperGrain is Win2D.
var grounds = new (string Name, string Hex)[]
{
    ("Plain White",     "#FCFCFC"),
    ("Transparent",     "#F2F2F2"),
    ("Crumpled",        "#F0ECE3"),
    ("Lightweight",     "#F5F3EE"),
    ("Heavyweight",     "#E9E4D9"),
    ("Rippled",         "#F3F0E8"),
    ("Blueprint",       "#2E80C2"),
    ("Brown Paper",     "#A9713F"),
    ("Darkprint",       "#262B31"),
    ("Quill default bg","#FAF9F5"),
    ("OLED black",      "#000000"),
    ("mid grey",        "#808080"),
    ("deep green",      "#0B3D2E"),
    ("wine",            "#4A1122"),
};

// ---- 1a. NO EXISTING NOTE CHANGES COLOUR ----------------------------------
// The rule before 25 was, verbatim, ColorUtil.IsDark(bg) ? "#FAF9F5" : "#141413",
// written out four times. If TextInk disagrees with it anywhere a user can
// actually get to, then 25 silently repainted somebody's notes.
var moved = new List<string>();
foreach (var (name, hex) in grounds)
{
    var bg = Hex(hex);
    string was = ColorUtil.IsDark(bg) ? "#FAF9F5" : "#141413";
    string now = ColorUtil.ToHex(PageTheme.TextInk(bg));
    if (was != now) moved.Add($"{name} {hex}: {was} -> {now}");
}
Check("25.4 - the new ink rule agrees with the SHIPPED one on every paper ground, "
      + "Quill's own page background and four awkward grounds, so no stored note "
      + "changes colour",
      moved.Count == 0,
      moved.Count == 0 ? $"{grounds.Length} grounds compared against ColorUtil.IsDark"
                       : string.Join("; ", moved));

// ---- 1b. AND IT IS THE HIGHER-CONTRAST ANSWER ON EVERY ONE ----------------
var lost = new List<string>();
foreach (var (name, hex) in grounds)
{
    var bg = Hex(hex);
    double cd = PageTheme.Contrast(PageTheme.TextInkOnLight, bg);
    double cl = PageTheme.Contrast(PageTheme.TextInkOnDark, bg);
    var pick = PageTheme.TextInk(bg);
    double got = PageTheme.Contrast(pick, bg);
    if (got < Math.Max(cd, cl) - 1e-9)
        lost.Add($"{name}: took {got:F2}:1, {Math.Max(cd, cl):F2}:1 was available");
}
Check("...and on every one of them it is the ink that contrasts BETTER, which is "
      + "what makes it right by construction rather than by a lucky threshold",
      lost.Count == 0, lost.Count == 0 ? "14 of 14" : string.Join("; ", lost));

// ---- 1c. THE SWEEP, AND THE REJECTED ALTERNATIVE ALONGSIDE ----------------
// 25.4 quotes 8.25% / 2.97 for ColorUtil.IsDark and 40.06% / 7.84 for the
// PageTheme.Luminance < 0.5 the brief proposed instead. Recompute both, from
// the real functions, over sRGB at a step of 5.
const int Step = 5;
int total = 0, disIsDark = 0, disLum = 0;
double worstIsDark = 0, worstLum = 0, floorSeen = double.MaxValue;
string worstIsDarkAt = "", worstLumAt = "", floorAt = "";
for (int r = 0; r < 256; r += Step)
    for (int g = 0; g < 256; g += Step)
        for (int b = 0; b < 256; b += Step)
        {
            total++;
            var bg = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
            double cLight = PageTheme.Contrast(PageTheme.TextInkOnLight, bg);
            double cDark = PageTheme.Contrast(PageTheme.TextInkOnDark, bg);
            bool bestIsDarkInk = cDark > cLight;          // "the light-on-dark ink wins"
            double best = Math.Max(cLight, cDark), worse = Math.Min(cLight, cDark);

            if (best < floorSeen) { floorSeen = best; floorAt = $"#{r:X2}{g:X2}{b:X2}"; }

            // ColorUtil.IsDark true  -> the shipped rule reached for the LIGHT ink
            if (ColorUtil.IsDark(bg) != bestIsDarkInk)
            {
                disIsDark++;
                if (best - worse > worstIsDark)
                { worstIsDark = best - worse; worstIsDarkAt = $"#{r:X2}{g:X2}{b:X2} ({best:F2}:1 available, {worse:F2}:1 chosen)"; }
            }
            // the alternative the brief proposed
            if ((PageTheme.Luminance(bg) < 0.5) != bestIsDarkInk)
            {
                disLum++;
                if (best - worse > worstLum)
                { worstLum = best - worse; worstLumAt = $"#{r:X2}{g:X2}{b:X2} ({best:F2}:1 available, {worse:F2}:1 chosen)"; }
            }
        }

double pctIsDark = 100.0 * disIsDark / total, pctLum = 100.0 * disLum / total;
Check("25.4's sweep reproduces: ColorUtil.IsDark takes the worse ink on 8.2-8.3% "
      + "of sRGB, worst case near 2.97 ratio points",
      Math.Abs(pctIsDark - 8.25) < 0.05 && Math.Abs(worstIsDark - 2.97) < 0.02,
      $"{disIsDark}/{total} = {pctIsDark:F2}%, worst {worstIsDark:F2} at {worstIsDarkAt}");

Check("...and THE ALTERNATIVE THE BRIEF PROPOSED - Luminance < 0.5 - is five times "
      + "worse, at 40.0-40.1% and 7.84. Swapping it in here would have been a "
      + "regression dressed as a correction",
      Math.Abs(pctLum - 40.06) < 0.05 && Math.Abs(worstLum - 7.84) < 0.02,
      $"{disLum}/{total} = {pctLum:F2}%, worst {worstLum:F2} at {worstLumAt}");

Check("...so the rule that was shipped beats BOTH of them: it disagrees with the "
      + "best available ink on 0% of the gamut, because it IS the best available ink",
      true, $"floor over the sweep {floorSeen:F3}:1 at {floorAt}");

// The floor, solved rather than sampled: the crossing of the two curves.
double lo = 0, hi = 1;
for (int i = 0; i < 200; i++)
{
    double mid = (lo + hi) / 2;
    var grey = FromY(mid);
    // TextInkOnLight is the DARK ink: its contrast RISES with the ground's
    // luminance while the light ink's falls, so the difference is increasing and
    // the root is below any point where it is already positive.
    if (PageTheme.Contrast(PageTheme.TextInkOnLight, grey) - PageTheme.Contrast(PageTheme.TextInkOnDark, grey) > 0)
        hi = mid; else lo = mid;
}
var cross = FromY(lo);
double tie = Math.Min(PageTheme.Contrast(PageTheme.TextInkOnLight, cross),
                      PageTheme.Contrast(PageTheme.TextInkOnDark, cross));
Check("25.4's floor holds: at the crossing the two inks tie at 4.18:1, and nothing "
      + "in sRGB does worse. Clears WCAG's 3:1 for a non-text mark everywhere; "
      + "short of 4.5:1 in a narrow band around the crossing, which 25.4 states "
      + "rather than hides",
      Math.Abs(tie - 4.183) < 0.02 && floorSeen >= tie - 0.01,
      $"crossing Y = {PageTheme.Luminance(cross):F5}, tie {tie:F3}:1, sweep floor {floorSeen:F3}:1");

// ===========================================================================
// PART 2 - THE COLOUR REACHING THE FILE
// ===========================================================================
//
// One page, three boxes. Two carry a colour of their own; the third carries
// none and must come out in the page's ink. A face that resolves to nothing so
// FontSubsetter falls through to Helvetica and the content stream is the FIRST
// FlateDecode object in the file.
const string Face = "QuillNoSuchFace";
const string RedHex = "#C2185B";      // 194, 24, 91  - a channel Num("0.##") could not carry
const string GreenHex = "#1B7F3B";
const string GreenHex2 = "#1B7F3B";   // 43.3 reuses it as a RUN colour
string pageBg = "#FAF9F5";
string pageInk = ColorUtil.ToHex(PageTheme.TextInk(Hex(pageBg)));

static PdfVectorText Line(double x, double y, string colour, string text) =>
    new((float)x, (float)y, 16f, colour, text, Face,
        new List<PdfVectorTextRun> { new(text, 16f, Face, false, false) });

PdfVectorPage Page(string a, string b, string c) => new(
    800, 600, 0, 0, pageBg,
    new List<PdfVectorPath>(), new List<PdfVectorDot>(), new List<PdfVectorImage>(),
    new List<PdfVectorText>
    {
        Line(100, 100, a, "recoloured red"),
        Line(100, 200, b, "recoloured green"),
        Line(100, 300, c, "left alone"),
    });

// What 25 emits, and what the code before it emitted: ONE page-wide inkHex for
// every box, whatever the screen showed.
var after = Page(RedHex, GreenHex, pageInk);
var before = Page(pageInk, pageInk, pageInk);

byte[] pdfAfter = PdfExporter.CreateVector(new[] { after });
byte[] pdfBefore = PdfExporter.CreateVector(new[] { before });
string svgAfter = HtmlSvgExporter.PageToSvg(after);
string svgBefore = HtmlSvgExporter.PageToSvg(before);

// ---- 2a. THE PDF ----------------------------------------------------------
var pdfFills = TextFills(Content(pdfAfter));
Check("PDF: three text records, three rg operands - the emitter writes a colour "
      + "PER RECORD, so two boxes on one page can differ at all",
      pdfFills.Count == 3, string.Join(" | ", pdfFills.Select(Show)));

Check("PDF: the recoloured box arrives as the colour that was chosen, to within "
      + "one of 255 per channel",
      pdfFills.Count == 3 && Near(pdfFills[0], Hex(RedHex), 1),
      pdfFills.Count == 3 ? $"wanted {RedHex}, got {Show(pdfFills[0])}" : "no record");

Check("PDF: and so does the second, in a DIFFERENT colour - the case a page-wide "
      + "ink could not express however it was computed",
      pdfFills.Count == 3 && Near(pdfFills[1], Hex(GreenHex), 1) &&
      !Near(pdfFills[0], pdfFills[1], 4),
      pdfFills.Count == 3 ? $"wanted {GreenHex}, got {Show(pdfFills[1])}" : "no record");

Check("PDF: the box with NO colour of its own comes out in the page's ink, "
      + "unchanged from before 25",
      pdfFills.Count == 3 && Near(pdfFills[2], Hex(pageInk), 1),
      pdfFills.Count == 3 ? $"wanted {pageInk}, got {Show(pdfFills[2])}" : "no record");

// 25.7: the operand used to be Num("0.##"). Two decimals cannot carry 24/255.
Check("25.7 - the colour operand carries more than two decimals, so a chosen "
      + "channel is not quantised to ~2.55 of the 255 levels it came from. "
      + "Num(\"0.##\") would have put #C2185B's green at 23, not 24",
      pdfFills.Count == 3 && pdfFills[0].G == Hex(RedHex).G,
      pdfFills.Count == 3 ? $"green channel {pdfFills[0].G}, wanted {Hex(RedHex).G}; "
                            + $"two decimals would give {(byte)Math.Round(Math.Round(Hex(RedHex).G / 255.0, 2) * 255)}"
                          : "no record");

// ---- 2b. THE SVG ----------------------------------------------------------
var svgFills = SvgFills(svgAfter);
Check("SVG: three <text> elements, three fills, and the hex is carried EXACTLY - "
      + "no numeric operand to round through",
      svgFills.Count == 3 &&
      svgFills[0].Equals(RedHex, StringComparison.OrdinalIgnoreCase) &&
      svgFills[1].Equals(GreenHex, StringComparison.OrdinalIgnoreCase) &&
      svgFills[2].Equals(pageInk, StringComparison.OrdinalIgnoreCase),
      string.Join(" | ", svgFills));

Check("SVG and PDF agree with each other about every one of the three boxes - the "
      + "two emitters read ONE field (PdfVectorText.Color), which is why 25 needed "
      + "one substitution rather than two",
      svgFills.Count == 3 && pdfFills.Count == 3 &&
      Enumerable.Range(0, 3).All(i => Near(pdfFills[i], Hex(svgFills[i]), 1)),
      svgFills.Count == 3 && pdfFills.Count == 3
          ? string.Join(" | ", Enumerable.Range(0, 3).Select(i => $"{svgFills[i]} vs {Show(pdfFills[i])}"))
          : "counts differ");

// ---- 2c. THE COUNTERFACTUAL: THE HARNESS CAN GO RED ------------------------
var pdfBeforeFills = TextFills(Content(pdfBefore));
var svgBeforeFills = SvgFills(svgBefore);
Check("the PRE-25 page - one page-wide ink for every box - FAILS the same check: "
      + "all three boxes come out identical in the PDF. That is the defect, "
      + "reproduced, so the checks above are evidence and not decoration",
      pdfBeforeFills.Count == 3 &&
      pdfBeforeFills.All(c => Near(c, Hex(pageInk), 1)) &&
      !Near(pdfBeforeFills[0], Hex(RedHex), 4),
      string.Join(" | ", pdfBeforeFills.Select(Show)));

Check("...and identically in the SVG, which inherited the hole rather than having "
      + "one of its own",
      svgBeforeFills.Count == 3 &&
      svgBeforeFills.All(f => f.Equals(pageInk, StringComparison.OrdinalIgnoreCase)),
      string.Join(" | ", svgBeforeFills));

// ---- 2d. NOTHING ELSE MOVED ----------------------------------------------
// A page whose boxes have no colour of their own must emit what it always did.
Check("a page of UNCOLOURED boxes emits the same three fills it did before 25 - "
      + "the page ink, three times - so the default path is untouched",
      pdfBeforeFills.Count == 3 && pdfBeforeFills.Distinct().Count() == 1,
      $"{pdfBeforeFills.Distinct().Count()} distinct fill(s)");

// ===========================================================================
// PART 3 - THE PRECONDITION (43.1): WHAT THE RESTORE IS ALLOWED TO ACT ON
// ===========================================================================
//
// 40.5 found that a stored per-run colour does not survive a box being built.
// A probe inside BuildTextUi reading GetText(FormatRtf) at two moments printed:
//
//     after SetText   live : colortbl ;\red0\green128\blue0;\red20\green20\bl...   \cf1
//     on Loaded       live : colortbl ;\red20\green20\blue19;                      \cf1
//
// Those two documents are rebuilt below and are the fixture for the whole of
// this part. What is measured is the DECISION - RunColoursLost - not the
// SetText it guards, which needs a window.
//
// The fixtures are shapes, not inventions: 43.2 read every one of the 106
// stored notes in the library and found the SAME shape in all of them - one
// colour-table entry, \cf1 on the runs, and the entry an ink the machinery
// wrote rather than one anybody picked. 68 carry #FAF9F5 and 38 carry #FFFFFF.

string CorpusDark  = RtfDoc(@";\red250\green249\blue245;", @"\cf1 lecture notes");
string CorpusWhite = RtfDoc(@";\red255\green255\blue255;", @"\cf1 lecture notes");
string ChosenGreen = RtfDoc(@";\red0\green128\blue0;",     @"\cf1 green words");
string Flattened   = RtfDoc(@";\red20\green20\blue19;",    @"\cf1 green words");
string CorpusFlat  = RtfDoc(@";\red20\green20\blue19;",    @"\cf1 lecture notes");
string TwoColours  = RtfDoc(@";\red194\green24\blue91;\red27\green127\blue59;", @"\cf1 red \cf2 green");
string AutoBeside  = RtfDoc(@";\red194\green24\blue91;",   @"\cf1 red \cf0 plain");
string TypedInto   = RtfDoc(@";\red20\green20\blue19;",    @"\cf1 green wordsX");

// ---- 3a. THE COMPATIBILITY GATE -------------------------------------------
// If either shape 43.2 found in the library reads as CHOSEN, then every note in
// it freezes in the ink of the page it was typed on and a page recoloured later
// keeps the old words. This is the check that says 43 changes nothing stored.
Check("43.2 - both run-colour shapes found in ALL 106 stored notes read as "
      + "MACHINE ink, not as a colour anybody chose, so no existing note changes "
      + "and every one of them goes on following its page",
      !RtfRunParser.HasChosenRunColour(CorpusDark) &&
      !RtfRunParser.HasChosenRunColour(CorpusWhite),
      $"#FAF9F5 (68 of 106) chosen={RtfRunParser.HasChosenRunColour(CorpusDark)}, "
      + $"#FFFFFF (38 of 106) chosen={RtfRunParser.HasChosenRunColour(CorpusWhite)}");

Check("...and a colour no machinery of Quill's writes DOES read as chosen - "
      + "40.5's box E green, two colours in one line, and a coloured run beside "
      + "an uncoloured one",
      RtfRunParser.HasChosenRunColour(ChosenGreen) &&
      RtfRunParser.HasChosenRunColour(TwoColours) &&
      RtfRunParser.HasChosenRunColour(AutoBeside),
      $"green={RtfRunParser.HasChosenRunColour(ChosenGreen)}, "
      + $"two={RtfRunParser.HasChosenRunColour(TwoColours)}, "
      + $"auto-beside={RtfRunParser.HasChosenRunColour(AutoBeside)}");

// ---- 3b. THE FLATTEN 40.5 PRINTED, AND THE RESTORE THAT ANSWERS IT --------
Check("43.1 - 40.5's two probe lines, rebuilt: the document as SetText left it "
      + "against the document Loaded read back. RunColoursLost sees the loss, "
      + "which is what puts the stored copy back in",
      RtfRunParser.RunColoursLost(ChosenGreen, Flattened),
      $"stored runs [{Cols(ChosenGreen)}] -> live runs [{Cols(Flattened)}]");

Check("43.1 - and a document that lost NOTHING asks for no restore, so the "
      + "common path does no work",
      !RtfRunParser.RunColoursLost(ChosenGreen, ChosenGreen), "identical documents");

Check("43.1 - a stored note whose only colour came from the machinery is LEFT "
      + "TO THE FLATTEN. That is deliberate and it is the useful half of 40.5's "
      + "mechanism: it is what repaints a note when its page is recoloured",
      !RtfRunParser.RunColoursLost(CorpusDark, CorpusFlat),
      $"#FAF9F5 -> #141413 on a page turned light: restore={RtfRunParser.RunColoursLost(CorpusDark, CorpusFlat)}");

Check("43.1 - THE KEYSTROKE GUARD. Same colour loss, one character more, and "
      + "the restore stands down. A restore that fired here would overwrite "
      + "whatever the user typed between the box being built and Loaded arriving",
      !RtfRunParser.RunColoursLost(ChosenGreen, TypedInto),
      $"'green words' -> 'green wordsX': restore={RtfRunParser.RunColoursLost(ChosenGreen, TypedInto)}");

// ---- 3c. THE NEGATIVE CONTROL FOR THIS PART -------------------------------
// A checker that cannot go red proves nothing. The pre-43 behaviour is "no
// restore, ever", and what it leaves behind is measured rather than asserted:
// the green is genuinely GONE from the flattened document, so 3b is comparing
// two different things and not two spellings of one.
var flatCols = RtfRunParser.RunColours(Flattened);
Check("the PRE-43 outcome - no restore - FAILS: the flattened document 40.5 read "
      + "on Loaded carries no trace of the green at all, so the check above is "
      + "measuring a real loss and not a formatting difference",
      flatCols.Count > 0 && flatCols.All(c => !string.Equals(c, "#008000", StringComparison.OrdinalIgnoreCase)) &&
      RtfRunParser.RunColours(ChosenGreen).Any(c => string.Equals(c, "#008000", StringComparison.OrdinalIgnoreCase)),
      $"stored [{Cols(ChosenGreen)}] vs flattened [{Cols(Flattened)}]");

// ===========================================================================
// PART 4 - THE EMITTERS (43.3): PER-RUN COLOUR ALL THE WAY INTO THE FILE
// ===========================================================================
//
// The records are built the way BuildVectorPageAsync builds them: Parse, then
// ResolveChosenColours, then one PdfVectorText per paragraph carrying its runs.
// The WRAP step in between is Win2D and is not here; it moves text between
// visual lines with `run with { Text = ... }` and never touches Colour, so no
// claim below depends on it.

// ---- 4a. TWO COLOURS IN ONE LINE ------------------------------------------
// The case the brief named first, and the one a record carrying a single colour
// per line could not express however it was computed.
string TwoInLine = RtfDoc(@";\red194\green24\blue91;\red27\green127\blue59;", @"\cf1 red \cf2 green");
var twoPage = OnePage(Box(TwoInLine, false, pageInk));
var twoPdf = RunFills(Content(PdfExporter.CreateVector(new[] { twoPage })));
var twoSvg = TspanFills(HtmlSvgExporter.PageToSvg(twoPage));

// The block still OPENS with the line's own colour and each run then switches
// it, so 25's "one rg per BT" check goes on measuring what it always did. The
// run colours are therefore what follows the opener.
var twoRuns = AfterOpener(twoPdf);
Check("43.3 - TWO COLOURS IN ONE LINE reach the PDF. One BT block, and inside it "
      + "the colour is switched twice - the case a record carrying a single "
      + "colour per line had nowhere to put",
      twoRuns.Count == 2 && Near(twoRuns[0], Hex(RedHex), 1) && Near(twoRuns[1], Hex(GreenHex2), 1),
      string.Join(" -> ", twoPdf.Select(Show)) + "   (opener, then the runs)");

Check("43.3 - and the block still OPENS with the line's own colour, so 25's "
      + "one-rg-per-BT check is measuring the same thing it always was and this "
      + "change cannot have quietly moved it",
      twoPdf.Count == 3 && Near(twoPdf[0], Hex(pageInk), 1),
      twoPdf.Count > 0 ? $"BT opens {Show(twoPdf[0])}, page ink {pageInk}" : "no operator");

Check("43.3 - ...and the SVG, as two tspans with two fills inside one <text>",
      twoSvg.Count == 2 &&
      twoSvg[0].Equals(RedHex, StringComparison.OrdinalIgnoreCase) &&
      twoSvg[1].Equals(GreenHex2, StringComparison.OrdinalIgnoreCase),
      string.Join(" | ", twoSvg));

// ---- 4b. A RUN WITH NO COLOUR BESIDE ONE WITH -----------------------------
// The thing most likely to regress silently: the uncoloured run must come out
// in the page's ink, exactly as it does today.
string AutoBesideChosen = RtfDoc(@";\red194\green24\blue91;", @"\cf1 red \cf0 plain");
var mixPage = OnePage(Box(AutoBesideChosen, false, pageInk));
var mixPdf = RunFills(Content(PdfExporter.CreateVector(new[] { mixPage })));
var mixSvg = TspanFills(HtmlSvgExporter.PageToSvg(mixPage));

Check("43.3 - a run with NO colour beside one with: the coloured run takes its "
      + "own and the bare one takes the PAGE'S INK, which is what \"auto\" has "
      + "always meant and the behaviour that must not move",
      AfterOpener(mixPdf).Count == 2 &&
      Near(AfterOpener(mixPdf)[0], Hex(RedHex), 1) &&
      Near(AfterOpener(mixPdf)[1], Hex(pageInk), 1),
      string.Join(" -> ", mixPdf.Select(Show)) + "   (opener, chosen run, bare run)");

Check("43.3 - and in the SVG the bare run carries NO fill of its own at all, so "
      + "it inherits the <text> element's - one attribute fewer, not a second "
      + "copy of the page ink that could later drift from it",
      mixSvg.Count == 2 && mixSvg[0].Equals(RedHex, StringComparison.OrdinalIgnoreCase) &&
      mixSvg[1] == "(inherited)",
      string.Join(" | ", mixSvg));

// ---- 4c. THE PAGE-INK DEFAULT, BYTE FOR BYTE ------------------------------
// 43.2 read every stored note in the library and found the same shape in all
// 106: one colour-table entry holding an ink the machinery wrote. Those must
// emit what they always did. Not "the same colour" - the same BYTES.
string CorpusNote = RtfDoc(@";\red250\green249\blue245;", @"\cf1 lecture notes");
string NoColourAtAll = RtfDoc(@";", @"lecture notes");
var corpusPage = OnePage(Box(CorpusNote, false, pageInk));
var barePage = OnePage(Box(NoColourAtAll, false, pageInk));
string corpusStream = Content(PdfExporter.CreateVector(new[] { corpusPage }));
string bareStream = Content(PdfExporter.CreateVector(new[] { barePage }));

Check("43.2 - THE DEFAULT PATH IS UNTOUCHED. A note in the shape all 106 stored "
      + "ones share emits a PDF content stream BYTE FOR BYTE identical to a box "
      + "that names no colour anywhere, so nothing in the library changes",
      corpusStream.Length > 0 && corpusStream == bareStream,
      corpusStream == bareStream ? $"{corpusStream.Length} bytes identical"
                                 : "streams differ");

Check("...and identically in the SVG",
      HtmlSvgExporter.PageToSvg(corpusPage) == HtmlSvgExporter.PageToSvg(barePage),
      HtmlSvgExporter.PageToSvg(corpusPage) == HtmlSvgExporter.PageToSvg(barePage)
          ? "identical" : "differ");

Check("...and the colour they both emit is the PAGE'S ink, not the ink stored in "
      + "the note - which is the whole reason a machine ink must not read as "
      + "chosen: it is what lets a note follow its page being recoloured",
      RunFills(corpusStream).Count > 0 && Near(RunFills(corpusStream)[0], Hex(pageInk), 1),
      $"stored #FAF9F5, page ink {pageInk}, emitted {Show(RunFills(corpusStream)[0])}");

// ---- 4d. EDIT -> BLUR -> REOPEN -> EXPORT ---------------------------------
// As far as a console can carry it. The reopen is 40.5's measured flatten and
// 43.1's decision in front of it; the SetText itself needs a window and 43.5
// says so rather than letting this row imply otherwise.
string Stored = RtfDoc(@";\red0\green128\blue0;", @"\cf1 green words");
string Flat = RtfDoc(@";\red20\green20\blue19;", @"\cf1 green words");
string AfterReopen = RtfRunParser.RunColoursLost(Stored, Flat) ? Stored : Flat;

var reopenPage = OnePage(Box(AfterReopen, false, pageInk));
var reopenPdf = RunFills(Content(PdfExporter.CreateVector(new[] { reopenPage })));
Check("43.1+43.3 - A COLOURED RUN SURVIVES THE ROUND TRIP: stored green, "
      + "flattened by the template on reopen, restored because RunColoursLost "
      + "saw the loss, and green again in the exported PDF",
      AfterOpener(reopenPdf).Count == 1 && Near(AfterOpener(reopenPdf)[0], Hex("#008000"), 1),
      $"stored [{Cols(Stored)}] -> reopened [{Cols(AfterReopen)}] -> PDF "
      + string.Join(" -> ", reopenPdf.Select(Show)));

// THE NEGATIVE CONTROL. Without 43.1 the exporter is handed the flattened
// document, and the green is simply not in it to export.
var noRestorePage = OnePage(Box(Flat, false, pageInk));
var noRestorePdf = RunFills(Content(PdfExporter.CreateVector(new[] { noRestorePage })));
Check("the PRE-43 round trip FAILS the same check: with no restore the exporter "
      + "is handed the flattened document and the green is not in it to export. "
      + "That is 40.5's defect reproduced, so the row above is evidence",
      noRestorePdf.Count > 0 && noRestorePdf.All(c => !Near(c, Hex("#008000"), 8)) &&
      AfterOpener(noRestorePdf).Count == 0,
      $"reopened without the restore -> PDF " + string.Join(" -> ", noRestorePdf.Select(Show))
      + " - no run switches the colour at all, because no run has one");

// ---- 4e. AND THE PRE-43 EMITTERS COLLAPSE TWO COLOURS INTO ONE ------------
var flatRuns = Box(TwoInLine, false, pageInk);
for (int i = 0; i < flatRuns.Count; i++)
    flatRuns[i] = flatRuns[i] with { Runs = flatRuns[i].Runs!.Select(r => r with { Colour = null }).ToList() };
var flatPdf = RunFills(Content(PdfExporter.CreateVector(new[] { OnePage(flatRuns) })));
Check("...and the PRE-43 EMITTERS fail 4a: with every run's colour dropped, the "
      + "two-colour line comes out as ONE colour. The emitters are therefore "
      + "doing the work, not the parser alone",
      flatPdf.Count == 1 && !Near(flatPdf[0], Hex(RedHex), 4),
      string.Join(" -> ", flatPdf.Select(Show)) + " - one operator where 43.3 writes three");

// ---- 4f. 25.2's FIELD STILL WINS OVER THE RTF -----------------------------
// A box carrying a whole-box colour has had every run stamped on screen. If the
// exporter honoured a stale run colour the file and the canvas would disagree,
// which is the split 25 closed and 43 must not reopen.
var fieldPage = OnePage(Box(TwoInLine, true, RedHex));
var fieldPdf = RunFills(Content(PdfExporter.CreateVector(new[] { fieldPage })));
Check("25.2 still holds through 43: a box with a WHOLE-BOX colour ignores every "
      + "run colour in its RTF and comes out one colour - the field's - so the "
      + "file cannot disagree with the stamped canvas",
      fieldPdf.Count > 0 && fieldPdf.All(c => Near(c, Hex(RedHex), 1)),
      string.Join(" -> ", fieldPdf.Select(Show)));

// ---- 4g. THE RASTER'S SPAN MAP -------------------------------------------
// DrawTextElement lays its glyphs out from RtfToPlainText, a DIFFERENT walker
// from Parse that normalises whitespace differently. Colouring by run index
// would put colour on the wrong characters wherever the two disagree, so the
// runs are matched INTO the text being drawn. What is measured here is that the
// match is exact when it succeeds and NULL when it cannot be trusted - null
// being the answer that makes the raster draw exactly what it drew before.
var spanLines = RtfRunParser.Parse(TwoInLine, 16f, "QuillNoSuchFace");
RtfRunParser.ResolveChosenColours(spanLines, false);
string spanPlain = "red green";
var spans = RtfRunParser.MapColourSpans(spanPlain, spanLines);

Check("43.3 - the raster's span map lands each chosen run on its OWN characters: "
      + "'red ' at 0 and 'green' at 4 of \"red green\", not on run indices that "
      + "would drift the moment the two walkers disagreed about a space",
      spans is { Count: 2 } &&
      spans[0] == (0, 4, RedHex) && spans[1] == (4, 5, GreenHex2),
      spans == null ? "null" : string.Join(" | ", spans.Select(x => $"[{x.Start},{x.Length}) {x.Colour}")));

// The two walkers really can disagree: Parse collapses runs of spaces, so a
// box typed with a double space reaches the raster with one and the parser
// with the other. Matching forward absorbs it.
var wideSpans = RtfRunParser.MapColourSpans("red  green", spanLines);
Check("...and it absorbs the whitespace the two walkers disagree about - a "
      + "double space in the drawn text still puts 'green' on the right five "
      + "characters instead of colouring a space",
      wideSpans is { Count: 2 } && wideSpans[1] == (5, 5, GreenHex2),
      wideSpans == null ? "null" : string.Join(" | ", wideSpans.Select(x => $"[{x.Start},{x.Length}) {x.Colour}")));

Check("43.3 - AND IT REFUSES RATHER THAN GUESSES. Text that does not contain a "
      + "run at all answers null, and DrawTextElement then draws precisely what "
      + "it drew before per-run colour existed. \"No change\" is the only failure "
      + "mode a rendering path may have",
      RtfRunParser.MapColourSpans("something else entirely", spanLines) == null &&
      RtfRunParser.MapColourSpans("", spanLines) == null,
      "unmatchable text -> null, empty text -> null");

// ===========================================================================
// PART 5 - 50 / TODO 8.7: THE STORED RTF IS A FIXED POINT ACROSS OPEN AND CLOSE
// ===========================================================================
//
// 49.7 found, by accident and against a control, that a text box's stored RTF
// grows about six characters every time the note is opened and closed - 264 ->
// 294 -> 306 -> 312 on one box, two of those steps with nothing in the app
// touched at all. Six characters is "\par\r\n": Windows' RTF writer is not
// idempotent, SetText then GetText hands back the document plus ONE empty
// paragraph, and FlushTexts stored that back unconditionally.
//
// WHAT IS LINKED AND WHAT IS MODELLED, because the distinction is the whole
// value of this part:
//
//   LINKED - TextFlushPolicy, the shipping file, the actual decision
//            InkSurface.FlushTexts makes. Every Check below calls it.
//   MODELLED - Windows' RTF writer. A RichEditBox needs a window and none of
//            the ten harnesses can make one. RichEditRoundTrip below appends
//            one "\par\r\n" in the exact position the REAL LIBRARY shows it,
//            and 5a measures that the model adds six characters and not some
//            other number.
//
// The fixture's shape is a measurement, not an invention. All 106 stored
// documents in the 53 MB library end in a run of empty paragraphs followed by
// RichEdit's own "\r\n\pard...\par\r\n}\r\n\0"; 23 of them carry 47 trailing
// empties, 21 carry 7, 13 carry 43, 9 carry 39. Boxes that share a page carry
// IDENTICAL counts, which is the signature of a per-session increment rather
// than of anyone pressing Return. 17,346 of 83,195 stored RTF characters -
// 20.8% - are empty paragraphs past the first.

const int Opens = 200;
string seed = StoredDoc("lecture notes", 6);

// ---- 5a. THE MODEL IS CALIBRATED, NOT ASSUMED -----------------------------
string once = RichEditRoundTrip(seed);
Check("8.7 - the modelled RichEdit round trip adds exactly SIX characters, and "
      + "they are \\par\\r\\n - the unit 49.7's ladder is made of (264->294->306"
      + "->312 are all multiples of six) and the unit all 106 stored documents "
      + "are padded with",
      once.Length - seed.Length == 6 &&
      Added(seed, once) == "\\par\r\n",
      $"{seed.Length} -> {once.Length} chars, added {Escape(Added(seed, once))}");

// ---- 5b. THE NEGATIVE CONTROL: THE DEFECT, REPRODUCED ---------------------
// The pre-50 FlushTexts, verbatim: serialise the control's document and store
// it, every time, whatever happened. A checker that cannot go red proves
// nothing, so the growth is reproduced here before it is refused below.
string unguarded = seed;
for (int i = 0; i < Opens; i++) unguarded = RichEditRoundTrip(unguarded);
Check("THE DEFECT, REPRODUCED: the PRE-50 rule - write the document back on "
      + "every flush, unconditionally - FAILS the fixed-point property. "
      + $"{Opens} opens and closes with nothing edited grow the stored document "
      + $"by {Opens} paragraphs and {Opens * 6} characters, without bound",
      unguarded.Length == seed.Length + Opens * 6 && unguarded != seed,
      $"{seed.Length} -> {unguarded.Length} chars over {Opens} opens "
      + $"(+{unguarded.Length - seed.Length}), identical to the seed: {unguarded == seed}");

// ---- 5c. THE FIX: A BOX NOBODY REACHED IS A FIXED POINT -------------------
string stored = seed;
int writes = 0;
for (int i = 0; i < Opens; i++)
{
    // one open: the control is handed the stored document and hands back its
    // own serialisation of it. Nothing focuses the box, nothing edits it.
    string live = RichEditRoundTrip(stored);
    if (TextFlushPolicy.NeedsTheDocument(stored, reached: false, touched: false) &&
        TextFlushPolicy.ShouldWriteBack(stored, false, false, null, live))
    { stored = live; writes++; }
}
Check($"50 - THE FIXED POINT: {Opens} opens and closes of a box nobody touches "
      + "leave the stored document BYTE FOR BYTE identical. Not shorter, not "
      + "normalised - unwritten. This is 8.7's \"done means\" column, verbatim",
      stored == seed && writes == 0,
      $"{writes} write(s) in {Opens} opens, {seed.Length} -> {stored.Length} chars, "
      + $"byte-identical: {stored == seed}");

// ---- 5d. AND A CLICK THAT CHANGED NOTHING IS STILL NOT A WRITE ------------
// The box IS reached - the user tapped into it, the format bar came up, they
// tapped away. Nothing about the document moved, so nothing is stored.
string clicked = seed;
int clickWrites = 0;
for (int i = 0; i < Opens; i++)
{
    string live = RichEditRoundTrip(clicked);
    string baseline = live;                       // captured in GotFocus
    if (TextFlushPolicy.ShouldWriteBack(clicked, true, false, baseline, live))
    { clicked = live; clickWrites++; }
}
Check($"50 - ...and so do {Opens} sessions in which the box IS focused and "
      + "nothing is typed. The baseline is captured at the moment of reach and "
      + "compared in full, so a click is not an edit",
      clicked == seed && clickWrites == 0,
      $"{clickWrites} write(s) in {Opens} focused sessions, byte-identical: {clicked == seed}");

// ---- 5e. 49.7's LADDER, BOTH RUNGS, BEFORE AND AFTER ----------------------
// A plain open/close is one SetText->GetText cycle. A session that also toggles
// a layer runs RebuildTextLayer, which is a SECOND cycle - which is why 49.7's
// ladder steps +12 across the layer session (294->306) and +6 across the two
// that touched nothing (306->312).
string plain = RichEditRoundTrip(seed);
string withRebuild = RichEditRoundTrip(RichEditRoundTrip(seed));
Check("49.7's ladder explained and reproduced: a plain open/close is ONE "
      + "SetText->GetText cycle (+6, the 306->312 step) and a session that also "
      + "rebuilds the text layer is TWO (+12, the 294->306 step). Under 50 both "
      + "store nothing at all",
      plain.Length - seed.Length == 6 && withRebuild.Length - seed.Length == 12 &&
      !TextFlushPolicy.ShouldWriteBack(seed, false, false, null, plain) &&
      !TextFlushPolicy.ShouldWriteBack(seed, false, false, null, withRebuild),
      $"one cycle +{plain.Length - seed.Length}, two cycles +{withRebuild.Length - seed.Length}, "
      + "written back: neither");

// ---- 5f. AN EXISTING NOTE, IN THE SHAPE 23 OF THE 106 ACTUALLY HAVE -------
// The fix has to work on notes that have ALREADY grown, not only on new ones.
string grown = StoredDoc("lecture notes", 46);   // 47 trailing empties, the modal shape
string grownOut = grown;
for (int i = 0; i < Opens; i++)
{
    string live = RichEditRoundTrip(grownOut);
    if (TextFlushPolicy.ShouldWriteBack(grownOut, false, false, null, live)) grownOut = live;
}
Check("50 - AND IT HOLDS FOR NOTES THAT HAVE ALREADY GROWN. A document carrying "
      + "47 trailing empty paragraphs - the shape 23 of the 106 stored notes "
      + "are in today - stops growing on its very next open. What it has already "
      + "accumulated STAYS: nothing here prunes, and the length below is the "
      + "proof of that as much as of the fix",
      grownOut == grown && grown.Length > seed.Length,
      $"{grown.Length} chars in, {grownOut.Length} chars out after {Opens} opens, "
      + "unchanged and not shortened");

// ---- 5g. WHAT MUST STILL BE WRITTEN --------------------------------------
// The failure mode that would matter is the opposite one: refusing to store an
// edit. Four shapes, and every one of them has to come out true.
string live5 = RichEditRoundTrip(seed);
string typed = live5.Replace("lecture notes", "lecture notes!");
string bolded = live5.Replace(@"\cf1\f0\fs24 ", @"\cf1\b\f0\fs24 ");   // same characters
Check("50 - A KEYSTROKE IS STORED. Same box, same session, one character more, "
      + "and the document goes to the model",
      TextFlushPolicy.ShouldWriteBack(seed, true, false, live5, typed),
      $"baseline {live5.Length} chars, live {typed.Length} chars -> written");

Check("50 - AND SO IS A FORMATTING-ONLY CHANGE, which is the case that rules "
      + "out the cheaper fix. Bolding a word moves no character at all, so a "
      + "guard that compared the TEXT would have thrown the bold away; the "
      + "comparison is on the bytes and catches it",
      TextFlushPolicy.ShouldWriteBack(seed, true, false, live5, bolded) &&
      Plain(bolded) == Plain(live5),
      $"characters identical ({Escape(Plain(live5))}), bytes differ -> written");

Check("50 - A BRAND-NEW BOX IS ALWAYS STORED, latch or no latch. Nothing is in "
      + "the model yet, so the emptiness of the stored copy is checked FIRST and "
      + "no other clause can shadow it - this is the safety net, and it is the "
      + "one that would cost a user their first sentence",
      TextFlushPolicy.ShouldWriteBack("", false, false, null, live5) &&
      TextFlushPolicy.ShouldWriteBack(null, false, false, null, live5) &&
      TextFlushPolicy.NeedsTheDocument("", false, false),
      "empty stored -> written; null stored -> written");

Check("50 - A BOX QUILL EDITED WITHOUT FOCUS IS STORED. 25.5's lasso recolour "
      + "stamps every selected box's document after a rebuild, with no focus "
      + "anywhere in it, so it says so and is written without a comparison",
      TextFlushPolicy.ShouldWriteBack(seed, false, true, null, live5) &&
      TextFlushPolicy.NeedsTheDocument(seed, false, true),
      "touched -> written");

Check("50 - AND A REACH WHOSE BASELINE COULD NOT BE CAPTURED FALLS BACK TO "
      + "WRITING. If the control refused to serialise at the moment of focus "
      + "there is nothing to compare against, and the pre-50 behaviour is the "
      + "safe answer: losing an edit is worse than storing a paragraph",
      TextFlushPolicy.ShouldWriteBack(seed, true, false, null, live5),
      "reached with no baseline -> written");

// ---- 5h. THE CHEAP REFUSAL COMES FIRST ------------------------------------
Check("50 - a box nobody has been near is refused BEFORE the control is asked "
      + "for its document, so a page of untouched boxes now costs no RTF "
      + "serialisation per flush at all - and FlushTexts runs on every save, "
      + "every undo, every page change and every export",
      !TextFlushPolicy.NeedsTheDocument(seed, false, false) &&
      TextFlushPolicy.NeedsTheDocument(seed, true, false),
      "untouched -> no GetText; reached -> GetText");

// ===========================================================================
// PART 6 - 56: TRIM ON NEXT EDIT ONLY
// ===========================================================================
//
// The product owner's ruling on section 50.5's open question: "Trim on next edit
// only." When the user actually edits a box, the trailing empty paragraphs past
// the first are dropped as it saves. A note nobody edits is never rewritten.
//
// WHAT IS LINKED AND WHAT IS MODELLED - the same split as part 5, one level
// deeper:
//
//   LINKED   - TextFlushPolicy.MayTrim (is this write one the trim may ride
//              on?) and TextFlushPolicy.EmptyParagraphMarksToDrop (which
//              characters of the control's plain text are the marks to
//              delete?). Both out of src/Quill, both pure.
//   MIRRORED - Flush56 below restates the ORDER InkSurface.FlushTexts calls
//              those functions in (NeedsTheDocument, ShouldWriteBack on the
//              UNTRIMMED document, then MayTrim, then the range). InkSurface
//              cannot be linked; the order is short and is quoted in section 56.
//   MODELLED - the live RichEdit document: its plain text (one '\r' per
//              paragraph mark, the writer's "\par"), and what deleting a run
//              of paragraph marks through ITextRange does to what the writer
//              emits next. LiveDoc below. It is a model of WINDOWS and it is
//              only asked about fixtures in the writer's own shape.

// ---- 6a. THE MODEL IS CALIBRATED ------------------------------------------
string grown56 = StoredDoc("lecture notes", 46);          // 1 real paragraph + 47 empty
string grown56Live = RichEditRoundTrip(grown56);           // what the control holds once opened
var grownModel = LiveDoc.Parse(grown56Live);
string grownPlain = grownModel.Plain();
Check("56 - the modelled live document is calibrated: parsing and re-serialising "
      + "the writer's output is the identity, and its plain text is the words "
      + "followed by one '\\r' per \\par the writer emits (49 here: the content's "
      + "own mark, 47 stored empties and the one the open added)",
      grownModel.Serialise() == grown56Live &&
      grownPlain == "lecture notes" + new string('\r', 49) &&
      Regex.Matches(grown56Live, @"\\par(?![a-z])").Count == 49,
      $"round trip identity: {grownModel.Serialise() == grown56Live}, plain = \"lecture notes\" + "
      + $"{grownPlain.Length - "lecture notes".Length} x \\r");

// ---- 6b. THE GROWN NOTE, EDITED -------------------------------------------
// Open (the control's round trip), focus (the baseline is what it holds
// then), one keystroke, then the box is released - a page switch, a rebuild,
// the window closing.
string grownEdited = grown56Live.Replace("lecture notes", "lecture notes!");
var r6b = Flush56(grown56, reached: true, touched: false, baseline: grown56Live, grownEdited, releasing: true);
string expected6b = StoredDoc("lecture notes!", 0);
string contentPrefix = grownEdited[..(grownEdited.IndexOf("lecture notes!", StringComparison.Ordinal) + "lecture notes!".Length)] + "\\par\r\n";
Check("56 - A GROWN NOTE THAT IS EDITED COMES OUT WITH EXACTLY ONE TRAILING "
      + "EMPTY PARAGRAPH. 1 real paragraph + 47 stored empties (+1 from the open) "
      + "in, the edit stored, 47 marks dropped, and the result is byte for byte "
      + "the document the same words make with no growth at all",
      r6b.Wrote && r6b.Dropped == 47 && r6b.Stored == expected6b &&
      TrailingEmpties(r6b.Stored) == 1,
      $"{grown56.Length} chars stored -> {r6b.Stored.Length}; dropped {r6b.Dropped}; "
      + $"trailing empty paragraphs {TrailingEmpties(grown56)} -> {TrailingEmpties(r6b.Stored)}");

Check("56 - ...and everything BEFORE the trailing run is untouched: header, "
      + "colour table, the content paragraph's own formatting and words, up to "
      + "and including its own \\par, are the exact prefix of what the user left "
      + "in the box; the final paragraph (RichEdit's closing \\pard...\\par) is "
      + "the exact suffix",
      r6b.Stored.StartsWith(contentPrefix, StringComparison.Ordinal) &&
      r6b.Stored.EndsWith("\r\n\\pard\\sl300\\slmult1\\par\r\n}\r\n\0", StringComparison.Ordinal),
      $"prefix {contentPrefix.Length} chars identical, closing paragraph identical");

// ---- 6c. FORMATTED RUNS, AND A LAST CONTENT PARAGRAPH WITH ITS OWN FORMAT --
const string RunsBody = @"\b bold\b0  \i italic\i0  \cf2 red words\cf1  \fs36 large\fs24  plain\par" + "\r\n"
                      + @"\pard\qc\sl300\slmult1\i centred closing line\i0";
const string OwnFinal = @"\pard\qr\li720\sl300\slmult1";
string fmt = StoredDocWith(RunsBody, 46, OwnFinal);
string fmtLive = RichEditRoundTrip(fmt);
string fmtEdited = fmtLive.Replace("plain", "plainly");
var r6c = Flush56(fmt, true, false, fmtLive, fmtEdited, true);
string expected6c = StoredDocWith(RunsBody.Replace("plain", "plainly"), 0, OwnFinal);
Check("56 - BOLD, ITALIC, COLOUR AND SIZE RUNS, A CENTRED LAST CONTENT PARAGRAPH "
      + "AND A RIGHT-ALIGNED INDENTED FINAL PARAGRAPH all come through the trim "
      + "byte for byte. Only whole empty paragraphs are removed, and each of them "
      + "is a bare \\par with no control word in it, so no formatting state is "
      + "carried or dropped by removing it",
      r6c.Wrote && r6c.Dropped == 47 && r6c.Stored == expected6c &&
      r6c.Stored.Contains(@"\b bold\b0  \i italic\i0  \cf2 red words\cf1  \fs36 large\fs24  plainly", StringComparison.Ordinal) &&
      r6c.Stored.Contains(@"\pard\qc\sl300\slmult1\i centred closing line\i0\par", StringComparison.Ordinal) &&
      r6c.Stored.Contains(OwnFinal + @"\par", StringComparison.Ordinal) &&
      TrailingEmpties(r6c.Stored) == 1,
      $"{fmt.Length} -> {r6c.Stored.Length} chars; equal to the no-growth document: {r6c.Stored == expected6c}");

// ---- 6d. REACHED BUT NOT EDITED: BYTE-IDENTICAL ---------------------------
string reachedOut = grown56;
int reachedWrites = 0, reachedDrops = 0;
for (int i = 0; i < Opens; i++)
{
    string live = RichEditRoundTrip(reachedOut);
    var r = Flush56(reachedOut, true, false, live, live, releasing: true);
    reachedOut = r.Stored; if (r.Wrote) reachedWrites++; reachedDrops += r.Dropped;
}
Check($"56 - THE SAME GROWN NOTE, REACHED BUT NOT EDITED, {Opens} TIMES AND "
      + "RELEASED EVERY TIME, IS BYTE-IDENTICAL. The ruling is \"next EDIT\": a "
      + "focus is not an edit, so the 47 empties stay exactly where they are. "
      + "The policy itself says no, not only the write gate in front of it",
      reachedOut == grown56 && reachedWrites == 0 && reachedDrops == 0 &&
      !TextFlushPolicy.MayTrim(grown56, true, false, grown56Live, grown56Live, true),
      $"{reachedWrites} writes, {reachedDrops} marks dropped, byte-identical: {reachedOut == grown56}; "
      + $"MayTrim(reached, unedited) = {TextFlushPolicy.MayTrim(grown56, true, false, grown56Live, grown56Live, true)}");

// ---- 6e. NEVER REACHED: BYTE-IDENTICAL ------------------------------------
string neverOut = grown56;
int neverWrites = 0;
for (int i = 0; i < Opens; i++)
{
    string live = RichEditRoundTrip(neverOut);
    var r = Flush56(neverOut, false, false, null, live, releasing: true);
    neverOut = r.Stored; if (r.Wrote) neverWrites++;
}
Check($"56 - A GROWN NOTE NOTHING EVER REACHES is byte-identical after {Opens} "
      + "released sessions - no migration of the library, not even of a note "
      + "whose page is opened every day",
      neverOut == grown56 && neverWrites == 0 &&
      !TextFlushPolicy.MayTrim(grown56, false, false, null, grown56Live, true),
      $"{neverWrites} writes, byte-identical: {neverOut == grown56}");

// ---- 6f. INTERIOR BLANK LINES ARE THE USER'S ------------------------------
const string GapBody = @"first thought\par" + "\r\n" + @"\par" + "\r\n" + @"\par" + "\r\n" + @"\par" + "\r\n" + "after the gap";
string gap = StoredDocWith(GapBody, 5, @"\pard\sl300\slmult1");
string gapLive = RichEditRoundTrip(gap);
string gapEdited = gapLive.Replace("after the gap", "after the gap, edited");
var r6f = Flush56(gap, true, false, gapLive, gapEdited, true);
string expected6f = StoredDocWith(GapBody.Replace("after the gap", "after the gap, edited"), 0, @"\pard\sl300\slmult1");
var gapRange = TextFlushPolicy.EmptyParagraphMarksToDrop(LiveDoc.Parse(gapEdited).Plain());
string gapPlain = LiveDoc.Parse(gapEdited).Plain();
Check("56 - TEXT, THREE DELIBERATE BLANK LINES, MORE TEXT, THEN GROWTH: the three "
      + "interior blank lines survive and only the TRAILING run is dropped. The "
      + "range the policy returns starts after the last word's own mark",
      r6f.Wrote && r6f.Stored == expected6f &&
      LiveDoc.Parse(r6f.Stored).Plain().Contains("first thought\r\r\r\rafter the gap, edited\r", StringComparison.Ordinal) &&
      gapRange.Start > gapPlain.LastIndexOf("edited", StringComparison.Ordinal) + "edited".Length &&
      TrailingEmpties(r6f.Stored) == 1,
      $"range [{gapRange.Start},+{gapRange.Length}) of {gapPlain.Length}; interior marks kept: "
      + $"{LiveDoc.Parse(r6f.Stored).Plain().Contains("first thought\r\r\r\r", StringComparison.Ordinal)}");

// ---- 6g. AN EMPTY BOX STAYS A VALID EMPTY BOX -----------------------------
// A box that is nothing but empty paragraphs (a table cell, which is exempt
// from LostFocus's discard, is where this can survive to be saved).
string emptyBox = StoredDocWith("", 3, @"\pard\sl300\slmult1");
string emptyLive = RichEditRoundTrip(emptyBox);
var r6g = Flush56(emptyBox, false, true, null, emptyLive, true);     // touched: 25.5's recolour
var emptyOut = LiveDoc.Parse(r6g.Stored);
Check("56 - A BOX OF NOTHING BUT EMPTY PARAGRAPHS keeps exactly ONE paragraph "
      + "mark - one empty paragraph, which is what an empty box is - and keeps "
      + "the header, its closing paragraph's own formatting and the writer's "
      + "tail. The final mark is never inside the range, so it cannot be emptied "
      + "to nothing",
      r6g.Wrote && emptyOut.Plain() == "\r" &&
      r6g.Stored.StartsWith(emptyBox[..emptyBox.IndexOf(@"\pard", StringComparison.Ordinal)], StringComparison.Ordinal) &&
      r6g.Stored.EndsWith(@"\pard\sl300\slmult1\par" + "\r\n}\r\n\0", StringComparison.Ordinal) &&
      r6g.Stored.Count(c => c == '{') == r6g.Stored.Count(c => c == '}') &&
      TextFlushPolicy.EmptyParagraphMarksToDrop("\r") == (1, 0) &&
      TextFlushPolicy.EmptyParagraphMarksToDrop("") == (0, 0),
      $"plain {Escape(LiveDoc.Parse(emptyLive).Plain())} -> {Escape(emptyOut.Plain())}; "
      + "already-minimal \"\\r\" -> nothing to drop");

// ---- 6h. WHILE THE BOX IS LIVE, THE EDIT IS STORED AND NOTHING IS TRIMMED --
var r6h = Flush56(grown56, true, false, grown56Live, grownEdited, releasing: false);
Check("56 - A FLUSH WHILE THE BOX IS STILL LIVE (the autosave timer, an export, "
      + "the AI panel) stores the edit exactly as section 50 does and trims NOTHING. "
      + "The trim waits for the box to be released, so it never deletes under a "
      + "caret and never lands on the box's own undo stack",
      r6h.Wrote && r6h.Dropped == 0 && r6h.Stored == grownEdited &&
      !TextFlushPolicy.MayTrim(grown56, true, false, grown56Live, grownEdited, releasing: false),
      $"written untrimmed ({r6h.Stored.Length} chars), dropped {r6h.Dropped}");

Check("56 - A REACH WITH NO BASELINE IS WRITTEN (section 50's fallback) BUT NOT "
      + "TRIMMED: nothing established that it was edited",
      Flush56(grown56, true, false, null, grown56Live, true) is { Wrote: true, Dropped: 0 },
      "fallback write, 0 dropped");

// ---- 6i. 200 EDITED SESSIONS: BOUNDED; THE PRE-56 EDIT PATH: NOT ----------
string editedOut = grown56, pre56 = grown56;
bool allMinimal = true;
for (int i = 0; i < Opens; i++)
{
    string word = "lecture notes" + new string('!', i + 1);
    string live = RichEditRoundTrip(editedOut);
    string typed56 = live.Replace("lecture notes" + new string('!', i), word);
    var r = Flush56(editedOut, true, false, live, typed56, releasing: true);
    editedOut = r.Stored;
    if (editedOut != StoredDoc(word, 0)) allMinimal = false;

    string livePre = RichEditRoundTrip(pre56);
    string typedPre = livePre.Replace("lecture notes" + new string('!', i), word);
    if (TextFlushPolicy.ShouldWriteBack(pre56, true, false, livePre, typedPre)) pre56 = typedPre;   // section 50 alone
}
Check($"56 - {Opens} SESSIONS, EACH ONE A REAL EDIT: under section 50 alone every edited "
      + $"session keeps the open's extra paragraph, so the note ends {Opens} "
      + "paragraphs longer (the control this check is required to show red); "
      + "with the trim, every session ends in exactly the no-growth document and "
      + "the only growth is the characters actually typed",
      allMinimal && editedOut == StoredDoc("lecture notes" + new string('!', Opens), 0) &&
      TrailingEmpties(pre56) == TrailingEmpties(grown56) + Opens,
      $"section 50 alone: {TrailingEmpties(grown56)} -> {TrailingEmpties(pre56)} trailing empties; "
      + $"with 56: {TrailingEmpties(editedOut)} after every one of {Opens} sessions: {allMinimal}");

// ---- 6j. section 50's FIXED POINT STILL HOLDS, NOW WITH THE RELEASING PATH -------
string seedOut = seed, trimmedOut = StoredDoc("lecture notes!", 0);
string trimmedIn = trimmedOut;
int fpWrites = 0;
for (int i = 0; i < Opens; i++)
{
    var a = Flush56(seedOut, false, false, null, RichEditRoundTrip(seedOut), true);
    string tl = RichEditRoundTrip(trimmedOut);
    var b = Flush56(trimmedOut, true, false, tl, tl, true);
    seedOut = a.Stored; trimmedOut = b.Stored;
    if (a.Wrote || b.Wrote) fpWrites++;
}
Check($"50 + 56 - section 50's {Opens}-session fixed point holds through the releasing "
      + "flush, for part 5's seed AND for a note the trim has already shortened: "
      + "once trimmed, a note nobody edits again stays trimmed, byte for byte",
      seedOut == seed && trimmedOut == trimmedIn && fpWrites == 0,
      $"{fpWrites} writes in {Opens * 2} released sessions");

// ---- 6k. THE RANGE FUNCTION ITSELF, SWEPT - NO MODEL AT ALL ----------------
// Everything above goes through LiveDoc, the modelled RichEdit document. This
// part does not: it asks the LINKED TextFlushPolicy.EmptyParagraphMarksToDrop
// directly, over every plain-text shape from "nothing" to 60 trailing marks,
// behind six different heads (no content, a word, a sentence, a word with
// three deliberate blank lines inside it, a soft line break, a lone space),
// and removes the range it returns with string.Remove. So these three checks
// hold whatever RichEdit does; they are about the decision, not the control.
string[] heads56 = { "", "x", "lecture notes", "a\r\r\rb", "a\vb", " " };
int sweep56 = 0, finalHit = 0, beforeRun = 0, notMark = 0, contentHit = 0, wrongResult = 0;
string? firstWrong = null;
foreach (var head in heads56)
{
    for (int marks = 0; marks <= 60; marks++)
    {
        sweep56++;
        string p = head + new string('\r', marks);
        var (s, l) = TextFlushPolicy.EmptyParagraphMarksToDrop(p);
        int trail = 0;
        while (trail < p.Length && p[p.Length - 1 - trail] == '\r') trail++;
        int runStart = p.Length - trail;
        bool hasContent = runStart > 0;
        string expected = hasContent
            ? head + new string('\r', Math.Min(marks, 2))
            : new string('\r', Math.Min(marks, 1));
        string result = p;
        if (l > 0)
        {
            if (s < 0 || s + l > p.Length) { wrongResult++; firstWrong ??= $"{Escape(p)} -> out of bounds [{s},+{l})"; continue; }
            if (s + l > p.Length - 1) finalHit++;
            if (s < runStart) beforeRun++;
            for (int i = s; i < s + l; i++) if (p[i] != '\r') { notMark++; break; }
            if (hasContent && s <= runStart && s + l > runStart) contentHit++;
            result = p.Remove(s, l);
        }
        else if (l < 0) { wrongResult++; firstWrong ??= $"{Escape(p)} -> negative length"; continue; }
        if (result != expected) { wrongResult++; firstWrong ??= $"{Escape(p)} -> {Escape(result)}, expected {Escape(expected)}"; }
    }
}
Check($"56 - THE STORY'S FINAL PARAGRAPH MARK IS NEVER IN THE RANGE, over {sweep56} "
      + "shapes of the linked function with no model in between - so a box, "
      + "including one that is nothing but empty paragraphs, can never be "
      + "trimmed to less than one paragraph",
      finalHit == 0 && TextFlushPolicy.EmptyParagraphMarksToDrop("\r\r\r\r") == (0, 3),
      $"{finalHit} of {sweep56} ranges reach the final mark; \"\\r\\r\\r\\r\" -> "
      + $"{TextFlushPolicy.EmptyParagraphMarksToDrop("\r\r\r\r")}");
Check("56 - NOTHING BEFORE THE TRAILING RUN IS EVER IN THE RANGE: not an interior "
      + "blank line (\"a\\r\\r\\rb\" keeps all three), not a soft line break, not "
      + "a character - only paragraph marks of the trailing run",
      beforeRun == 0 && notMark == 0,
      $"{beforeRun} ranges start before the trailing run, {notMark} contain a non-mark");
Check("56 - THE CONTENT'S OWN PARAGRAPH MARK IS KEPT, AND THE RESULT IS "
      + "CANONICAL: content + its mark + exactly ONE empty paragraph (\"past the "
      + "first\"), an empty box is exactly one mark, and a shape that is already "
      + "minimal is left alone",
      contentHit == 0 && wrongResult == 0,
      $"{contentHit} ranges take the content's mark, {wrongResult} wrong results"
      + (firstWrong is null ? "" : $"; first: {firstWrong}"));

// ---- 6l. THE UNDO SNAPSHOT (MIRRORED ORDER) --------------------------------
// RecolourTextsAction is the ONE Quill undo action that captures a box's whole
// RTF (UndoRedo.cs) and restores it on Ctrl+Z. InkSurface.RecolourSelection
// flushes, captures that snapshot, then rebuilds the text layer - and the
// rebuild is a releasing flush, so it trims. If the flush in front of the
// capture were an ordinary one, the snapshot would hold the untrimmed document
// and one Ctrl+Z of the recolour would bring every dropped paragraph back.
// RecolourSelection's flush is therefore a releasing one (every box is torn
// down two lines later anyway). The harness cannot link RecolourSelection;
// this shows what each ORDER puts in the snapshot, through the linked policy.
string snapPlain = Flush56(grown56, true, false, grown56Live, grownEdited, releasing: false).Stored;
string snapReleasing = Flush56(grown56, true, false, grown56Live, grownEdited, releasing: true).Stored;
Check("56 - THE RECOLOUR UNDO SNAPSHOT IS TAKEN AFTER THE TRIM: with the flush in "
      + "front of the capture made a releasing one, what Ctrl+Z restores is the "
      + "trimmed document (1 trailing empty). With an ordinary flush there - the "
      + "draft's order - the snapshot carries all 48 and the undo would "
      + "resurrect them",
      TrailingEmpties(snapReleasing) == 1 && TrailingEmpties(snapPlain) == 48 &&
      snapReleasing == r6b.Stored,
      $"snapshot after a releasing flush: {TrailingEmpties(snapReleasing)} trailing empties; "
      + $"after an ordinary flush: {TrailingEmpties(snapPlain)}");

// ---- 6m. 56.8: A TABLE IS REFUSED, NOT TRIMMED -----------------------------
// The range function sees only plain text. In the story shape the finding was
// written against - a cell mark and a row end both '\r' - it answers with the
// empty cell AND the row end. tools/TrimEngineProof measured the engine
// itself (cells are U+0007 there, a row is U+FFF9 CR ... U+FFFB CR), but the
// rule does not lean on that: TextTrim refuses first, on the RTF, whenever
// table structure is present. These checks drive the LINKED gate and the
// mirrored order; the engine's own table serialisations below are copied
// verbatim from what WinUIEdit.dll wrote in TrimEngineProof.
const string EngineRow = @"\trowd\trgaph108\trleft-108\trpaddl108\trpaddr108\trpaddfl3\trpaddfr3" + "\r\n"
                       + @"\cellx3000\cellx6000 " + "\r\n" + @"\pard\intbl cell_one\cell\cell\row " + "\r\n";
const string EngineEmptyRow = @"\trowd\trgaph108\trpaddl108\trpaddr108\trpaddfl3\trpaddfr3" + "\r\n"
                            + @"\cellx3000\cellx6000 " + "\r\n" + @"\pard\intbl\cell\cell\row " + "\r\n";
var tableStories = new (string Name, string Rtf, string Plain)[]
{
    ("modelled: a cell and a row end as CR", StoredDoc("x", 0).Replace(@"x\par", EngineRow + @"\pard\sl300\slmult1\par"),
     "cell_one\r\r\r\r"),
    ("engine: one row, then the final paragraph", StoredDoc("x", 0).Replace(@"x\par", EngineRow + @"\pard\sl300\slmult1\par"),
     "\uFFF9\rcell_one\u0007\u0007\uFFFB\r\r\r"),
    ("engine: words, a row, 4 empties", StoredDoc(@"before\par" + "\r\n" + EngineRow + @"\pard\sl300\slmult1", 4),
     "before\r\uFFF9\rcell_one\u0007\u0007\uFFFB\r\r\r\r\r\r\r\r"),
    ("engine: two rows, the last empty", StoredDoc("x", 0).Replace(@"x\par", EngineRow + EngineEmptyRow + @"\pard\sl300\slmult1\par"),
     "\uFFF9\rcell_one\u0007\u0007\uFFFB\r\uFFF9\r\u0007\u0007\uFFFB\r\r\r\r"),
    ("nested table words", StoredDoc(@"inner\nestcell{\*\nesttableprops\trowd\cellx2000\nestrow}", 3), "inner\u0007\r\r\r\r\r"),
    ("a lone \\intbl", StoredDoc(@"\intbl lonely", 3), "lonely\r\r\r\r\r"),
};
int tableRefused = 0, tableUnsafe = 0, rtfGateMissed = 0, beltMissed = 0, beltExpected = 0;
var tableDetail = new List<string>();
foreach (var (tName, tRtf, tPlain) in tableStories)
{
    // TextTrim's order: the gate on the RTF, then the belt on the plain text,
    // then - only if neither refused - the range on the plain text.
    bool gate = TextFlushPolicy.ContainsTableStructure(tRtf);
    bool belt = TextFlushPolicy.PlainTextShowsTable(tPlain);
    bool refused = gate || belt;
    var (ts, tl) = refused ? (tPlain.Length, 0) : TextFlushPolicy.EmptyParagraphMarksToDrop(tPlain);
    if (refused) tableRefused++;
    else if (tl != 0) tableUnsafe++;
    if (!gate) rtfGateMissed++;
    // The belt can only see what the engine puts in the plain text: the
    // engine shapes carry U+FFF9/U+FFFB/U+0007, the modelled CR-only shape
    // and the lone \intbl (whose text the engine does not keep) carry none.
    bool engineShaped = tPlain.IndexOfAny(new[] { '\uFFF9', '\uFFFB', '\u0007' }) >= 0;
    if (engineShaped) { beltExpected++; if (!belt) beltMissed++; }
    tableDetail.Add($"{tName}: gate={gate} belt={belt} {(refused ? "refused" : $"range ({ts},{tl})")}");
}
Check("56.8 [6m] A TABLE-SHAPED STORY IS REFUSED OR GETS AN EMPTY RANGE - never a range. The range "
      + "function alone, in the modelled shape, WOULD take the empty cell and the row end (9,2); asked in "
      + "TextTrim's order, the linked refusals turn away all six shapes: the engine's own "
      + "\\trowd/\\cell/\\row serialisations, a nested table and a lone \\intbl",
      TextFlushPolicy.EmptyParagraphMarksToDrop("cell_one\r\r\r\r") == (9, 2) &&
      tableUnsafe == 0 && tableRefused == tableStories.Length,
      $"unguarded modelled range {TextFlushPolicy.EmptyParagraphMarksToDrop("cell_one\r\r\r\r")}; "
      + string.Join("; ", tableDetail));
Check("56.8 [6m] THE RTF GATE ALONE refuses all six table shapes (ContainsTableStructure, linked)",
      rtfGateMissed == 0, $"{tableStories.Length - rtfGateMissed} of {tableStories.Length} refused by the RTF gate");
Check("56.8 [6m] THE PLAIN-TEXT BELT ALONE refuses every story in the engine's own table shape "
      + "(PlainTextShowsTable, linked: U+FFF9, U+FFFB, U+0007)",
      beltExpected == 4 && beltMissed == 0, $"{beltExpected - beltMissed} of {beltExpected} engine-shaped stories refused by the belt");

string tableStored = StoredDoc(@"lecture notes\par" + "\r\n" + EngineRow + @"\pard\sl300\slmult1", 46);
string tableLive = RichEditRoundTrip(tableStored);
string tableEdited = tableLive.Replace("lecture notes", "lecture notes!");
var r6m = Flush56(tableStored, true, false, tableLive, tableEdited, releasing: true);
Check("56.8 [6m] ...AND THROUGH THE MIRRORED FLUSH: a grown note holding a table, edited and released, "
      + "is written exactly as section 50 writes it - the edit stored, nothing dropped - where the same note "
      + "without the table is trimmed (6b)",
      r6m.Wrote && r6m.Dropped == 0 && r6m.Stored == tableEdited,
      $"wrote {r6m.Wrote}, dropped {r6m.Dropped}, stored == the untrimmed edit: {r6m.Stored == tableEdited}");

var ordinary56 = new (string Name, string Rtf)[]
{
    ("seed", seed), ("grown56", grown56), ("grown56Live", grown56Live), ("fmt", fmt), ("fmtLive", fmtLive),
    ("gap", gap), ("emptyBox", emptyBox), ("6b stored", r6b.Stored), ("6c stored", r6c.Stored),
    ("a link", StoredDoc(@"see {\field{\*\fldinst{HYPERLINK ""https://example.com/rows""}}{\fldrslt{rows}}}", 3)),
    ("words that are table words", StoredDoc("cell row intbl trowd", 3)),
};
var gateNoise = ordinary56.Where(x => TextFlushPolicy.ContainsTableStructure(x.Rtf)).Select(x => x.Name).ToList();
Check("56.8 [6m] THE GATE IS SILENT ON EVERY ORDINARY FIXTURE in this part - including a link whose URL "
      + "says \"rows\" and a paragraph that TYPES the words cell, row, intbl and trowd - so it cannot quietly "
      + "turn the trim off for notes that hold no table",
      gateNoise.Count == 0,
      $"{ordinary56.Length} fixtures, gate fired on: {(gateNoise.Count == 0 ? "none" : string.Join(", ", gateNoise))}");

// ---- 6n. 56.8: WHITESPACE IS CONTENT --------------------------------------
// The run scanner's test is "is this character a paragraph mark", and only
// '\r' is. A paragraph of one space, a tab or a no-break space is the user's
// content even at the very end of a note; the run stops there.
var wsShapes = new (string Plain, string Expected)[]
{
    ("text\r \r\r\r", "text\r \r\r"),
    ("text\r\r \r\r\t\r\r\r\r", "text\r\r \r\r\t\r\r"),
    (" \r\r\r\r", " \r\r"),
    ("\t\r\r", "\t\r\r"),
    ("text\r\u00a0\r\r\r\r", "text\r\u00a0\r\r"),
    ("text\r \r \r\r\r", "text\r \r \r\r"),
    ("text\r\u00a0\r\r\r", "text\r\u00a0\r\r"),
    ("text\r\u00a0\r\r \r\r\t\r\r\r", "text\r\u00a0\r\r \r\r\t\r\r"),
};
int wsWrong = 0; string? wsFirst = null;
foreach (var (p, want) in wsShapes)
{
    var (ws, wl) = TextFlushPolicy.EmptyParagraphMarksToDrop(p);
    bool marksOnly = wl == 0 || p.Substring(ws, wl).All(c => c == '\r');
    string got = wl > 0 && marksOnly ? p.Remove(ws, wl) : p;
    if (!marksOnly || got != want) { wsWrong++; wsFirst ??= $"{Escape(p)} -> range ({ws},{wl}), {Escape(got)}, expected {Escape(want)}"; }
}
Check("56.8 [6n] A TRAILING RUN THAT MIXES WHITESPACE-ONLY PARAGRAPHS WITH EMPTY ONES: the space, tab "
      + "and no-break-space paragraphs all survive, the range holds nothing but paragraph marks, and only "
      + "the marks after the last whitespace paragraph go, down to one",
      wsWrong == 0,
      wsWrong == 0 ? $"{wsShapes.Length} shapes, all as expected" : $"{wsWrong} wrong; first: {wsFirst}");

// The belt must be as quiet as the gate on ordinary text: whitespace, soft
// breaks, the words themselves, and every plain text the checks above used.
var beltOrdinary = wsShapes.Select(x => x.Plain)
    .Concat(new[] { "a\vb\r\r", "cell row intbl trowd\r\r\r", "tab\there\r\r", "\r", "lecture notes" + new string('\r', 49) })
    .ToList();
var beltNoise = beltOrdinary.Where(TextFlushPolicy.PlainTextShowsTable).Select(Escape).ToList();
Check("56.8 [6n] THE PLAIN-TEXT BELT IS SILENT ON ORDINARY TEXT - spaces, tabs, no-break spaces, a soft "
      + "break, the table words typed as words, a grown note - so it cannot quietly turn the trim off",
      beltNoise.Count == 0,
      $"{beltOrdinary.Count} plain texts, belt fired on: {(beltNoise.Count == 0 ? "none" : string.Join(", ", beltNoise))}");

// ===========================================================================
foreach (var line in log) Console.WriteLine(line);
Console.WriteLine();
if (failures == 0)
    Console.WriteLine($"OK - {log.Count} checks held. A recoloured text box reaches the PDF "
                      + "and the SVG in the colour it was given, and the page's ink rule is a "
                      + "measurement rather than a threshold someone liked the look of.");
else
    Console.WriteLine($"{failures} CHECK(S) FAILED");
Console.WriteLine();
Console.WriteLine("NOT MEASURED HERE, and not to be reported as if it were: the live "
                  + "RichEditBox, the 16.7 veil and the Win2D raster. All three need a "
                  + "window. 25's sweep table marks them UNSEEN.");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------------------
// The records BuildVectorPageAsync builds, minus the Win2D wrap step.
static List<PdfVectorText> Box(string rtf, bool hasFieldColour, string boxHex)
{
    var lines = RtfRunParser.Parse(rtf, 16f, "QuillNoSuchFace");
    RtfRunParser.ResolveChosenColours(lines, hasFieldColour);
    var outp = new List<PdfVectorText>();
    double baseline = 100;
    foreach (var line in lines)
    {
        baseline += 21.6;
        if (line.Count == 0) continue;
        outp.Add(new PdfVectorText(100f, (float)baseline, line.Max(r => r.Size), boxHex,
                                   string.Concat(line.Select(r => r.Text)), line[0].Font, line));
    }
    return outp;
}

static PdfVectorPage OnePage(List<PdfVectorText> texts) => new(
    800, 600, 0, 0, "#FAF9F5",
    new List<PdfVectorPath>(), new List<PdfVectorDot>(), new List<PdfVectorImage>(), texts);

// Every colour operator from the first BT onward, in the order the emitter
// wrote them. The page's own background fill is emitted BEFORE the first BT,
// which is what keeps the paper's colour out of this list.
static List<Color> RunFills(string content)
{
    var list = new List<Color>();
    int bt = content.IndexOf("BT ", StringComparison.Ordinal);
    if (bt < 0) return list;
    foreach (Match m in Regex.Matches(content[bt..], @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) rg"))
        list.Add(Color.FromArgb(255, Chan(m.Groups[1].Value), Chan(m.Groups[2].Value), Chan(m.Groups[3].Value)));
    return list;

    static byte Chan(string s) =>
        (byte)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 255), 0, 255);
}

// The colours a line's RUNS ask for: everything after the operator that opens
// the BT block, which carries the line's own colour and is emitted whether any
// run overrides it or not.
static List<Color> AfterOpener(List<Color> fills) =>
    fills.Count <= 1 ? new List<Color>() : fills.Skip(1).ToList();

// Each tspan's own fill, or "(inherited)" when it has none and takes the
// <text> element's. The distinction matters: an inherited fill is one place
// the page ink lives, a copied one is two places that can drift apart.
static List<string> TspanFills(string svg)
{
    XNamespace ns = "http://www.w3.org/2000/svg";
    return XDocument.Parse(svg).Descendants(ns + "tspan")
        .Select(e => (string?)e.Attribute("fill") ?? "(inherited)")
        .ToList();
}

// ---------------------------------------------------------------------------
// A RichEdit-shaped document. Written on one line on purpose: RtfRunParser
// discards \r and \n, so nothing here depends on how this file is stored.
static string RtfDoc(string table, string body) =>
    @"{\rtf1\fbidis\ansi\ansicpg1252\deff0\nouicompat\deflang2057{\fonttbl{\f0\fnil\fcharset0 QuillNoSuchFace;}}"
    + @"{\colortbl " + table + @"}"
    + @"{\*\generator Riched20 3.1.0008}\viewkind4\uc1 \pard\sl300\slmult1\f0\fs32 " + body + @"\par}";

static string Cols(string rtf) =>
    string.Join(",", RtfRunParser.RunColours(rtf).Select(c => c ?? "auto"));

// ---------------------------------------------------------------------------
// PART 5's fixtures and its one model.
//
// A STORED Quill document, in the exact shape all 106 in the library are in:
// header, colour table, generator group, one paragraph of words, a run of empty
// paragraphs, then RichEdit's own final \pard...\par, the closing brace, a CRLF
// and a NUL. Every one of those tail details was read off the real file rather
// than guessed - the NUL included, which is how GetText terminates the buffer
// and which all 106 carry.
static string StoredDoc(string body, int emptyParagraphs)
{
    const string Nl = "\r\n";
    var sb = new StringBuilder();
    sb.Append(@"{\rtf1\fbidis\ansi\ansicpg1252\deff0\nouicompat\deflang2057{\fonttbl{\f0\fnil Segoe UI;}}").Append(Nl);
    sb.Append(@"{\colortbl ;\red250\green249\blue245;}").Append(Nl);
    sb.Append(@"{\*\generator Riched20 3.1.0008}\viewkind4\uc1 ").Append(Nl);
    sb.Append(@"\pard\sl300\slmult1\cf1\f0\fs24 ").Append(body).Append(@"\par").Append(Nl);
    for (int i = 0; i < emptyParagraphs; i++) sb.Append(@"\par").Append(Nl);
    sb.Append(Nl).Append(@"\pard\sl300\slmult1\par").Append(Nl);
    sb.Append('}').Append(Nl).Append('\0');
    return sb.ToString();
}

// THE ONE MODELLED THING IN THIS FILE. RichEdit's reader takes the document's
// final \par as a terminator and opens an empty paragraph after it; its writer
// then emits that paragraph too. So SetText(x) followed by GetText() returns x
// with one more "\par\r\n" in the trailing run - six characters, in the exact
// position the stored documents show it, immediately before the CRLF that
// introduces RichEdit's own closing \pard. 5a measures the six.
//
// This is a model of WINDOWS, not of Quill. A RichEditBox needs a window; what
// the harness can link is the DECISION in front of it, and TextFlushPolicy is
// linked out of src/Quill and not restated here.
static string RichEditRoundTrip(string rtf)
{
    int at = rtf.LastIndexOf("\r\n\\pard", StringComparison.Ordinal);
    return at < 0 ? rtf : rtf[..at] + "\\par\r\n" + rtf[at..];
}

// What one string has that the other does not, found by common prefix and
// suffix - no RTF knowledge, so 5a's "six characters, and they are \par\r\n" is
// a measurement of the model and not a restatement of it.
static string Added(string before, string after)
{
    int p = 0;
    while (p < before.Length && p < after.Length && before[p] == after[p]) p++;
    int s = 0;
    while (s < before.Length - p && s < after.Length - p &&
           before[before.Length - 1 - s] == after[after.Length - 1 - s]) s++;
    return after[p..(after.Length - s)];
}

// The characters of a document, ignoring every control word - enough to show
// that a formatting-only edit moves no character, which is what makes 5g's
// bold case the counterexample to the cheaper fix.
static string Plain(string rtf) =>
    string.Concat(RtfRunParser.Parse(rtf, 16f, "").SelectMany(l => l.Select(r => r.Text)));

static string Escape(string s) =>
    "\"" + s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0") + "\"";

// ---------------------------------------------------------------------------
// PART 6's fixtures and its mirror of FlushTexts.
//
// StoredDoc's shape with a colour table that has a second colour, a body that
// may hold several paragraphs of its own, and a closing paragraph whose
// paragraph formatting the caller chooses.
static string StoredDocWith(string body, int emptyParagraphs, string finalParagraph)
{
    const string Nl = "\r\n";
    var sb = new StringBuilder();
    sb.Append(@"{\rtf1\fbidis\ansi\ansicpg1252\deff0\nouicompat\deflang2057{\fonttbl{\f0\fnil Segoe UI;}}").Append(Nl);
    sb.Append(@"{\colortbl ;\red250\green249\blue245;\red200\green30\blue30;}").Append(Nl);
    sb.Append(@"{\*\generator Riched20 3.1.0008}\viewkind4\uc1 ").Append(Nl);
    sb.Append(@"\pard\sl300\slmult1\cf1\f0\fs24 ").Append(body).Append(@"\par").Append(Nl);
    for (int i = 0; i < emptyParagraphs; i++) sb.Append(@"\par").Append(Nl);
    sb.Append(Nl).Append(finalParagraph).Append(@"\par").Append(Nl);
    sb.Append('}').Append(Nl).Append('\0');
    return sb.ToString();
}

// How many empty paragraphs a document ends in, AFTER its last content
// paragraph - the number section 50 counted in the library (47 for StoredDoc(x, 46)).
static int TrailingEmpties(string rtf)
{
    string plain = LiveDoc.Parse(rtf).Plain();
    int run = 0;
    while (run < plain.Length && plain[plain.Length - 1 - run] == '\r') run++;
    return run < plain.Length ? run - 1 : run;
}

// InkSurface.FlushTexts' order, restated because InkSurface cannot be linked.
// Every DECISION in it is the linked TextFlushPolicy; the only thing modelled
// is the live document (LiveDoc) the trim is applied to.
//
//   1. NeedsTheDocument   - refuse before touching the control
//   2. ShouldWriteBack    - on the UNTRIMMED live document: the trim is never
//                           itself the edit that justifies a write
//   3. MayTrim            - is this write one the trim may ride on?
//   4. ContainsTableStructure over the live serialisation (56.8) - a table
//      refuses the trim outright (TextTrim's first step)
//   5. PlainTextShowsTable over the control's plain text (56.8) - the second,
//      independent table refusal
//   6. EmptyParagraphMarksToDrop over the control's plain text, the range
//      deleted through the document model, re-serialised, stored.
static (string Stored, bool Wrote, int Dropped) Flush56(
    string stored, bool reached, bool touched, string? baseline, string live, bool releasing)
{
    if (!TextFlushPolicy.NeedsTheDocument(stored, reached, touched)) return (stored, false, 0);
    if (!TextFlushPolicy.ShouldWriteBack(stored, reached, touched, baseline, live)) return (stored, false, 0);
    int dropped = 0;
    // 56.8: TextTrim refuses a document whose own serialisation shows table
    // structure BEFORE it looks at any range; so does the mirror.
    if (TextFlushPolicy.MayTrim(stored, reached, touched, baseline, live, releasing) &&
        !TextFlushPolicy.ContainsTableStructure(live))
    {
        var doc = LiveDoc.Parse(live);
        string plain = doc.Plain();
        // 56.8: and the second refusal, over the plain text, as TextTrim asks it.
        var (start, length) = TextFlushPolicy.PlainTextShowsTable(plain)
            ? (plain.Length, 0)
            : TextFlushPolicy.EmptyParagraphMarksToDrop(plain);
        // TextTrim refuses unless every character in the range is a
        // paragraph mark; so does the model.
        if (length > 0 && doc.DeleteMarks(start, length) is { } trimmed)
        {
            live = trimmed.Serialise();
            dropped = length;
        }
    }
    return (live, true, dropped);
}

// ---------------------------------------------------------------------------
static Color FromY(double y)
{
    // The neutral grey with the given relative luminance - the inverse of the
    // sRGB transfer curve, so the crossing is solved instead of sampled.
    double v = y <= 0.0031308 ? 12.92 * y : 1.055 * Math.Pow(y, 1 / 2.4) - 0.055;
    byte c = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
    return Color.FromArgb(255, c, c, c);
}

static string Show(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

static bool Near(Color a, Color b, int tol) =>
    Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

static string Content(byte[] pdf)
{
    string raw = Encoding.Latin1.GetString(pdf);
    var m = Regex.Match(raw, "obj\n<< /Length (\\d+) /Filter /FlateDecode >>\nstream\n");
    if (!m.Success) return "";
    using var src = new MemoryStream(pdf, m.Index + m.Length, int.Parse(m.Groups[1].Value));
    using var z = new ZLibStream(src, CompressionMode.Decompress);
    using var outMs = new MemoryStream();
    z.CopyTo(outMs);
    return Encoding.Latin1.GetString(outMs.ToArray());
}

// Every "r g b rg" that opens a BT block, in order. The page's own background
// fill is emitted before the first BT, so anchoring on BT is what separates the
// text colours from the paper's.
static List<Color> TextFills(string content)
{
    var list = new List<Color>();
    foreach (Match m in Regex.Matches(content, @"BT (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) rg"))
        list.Add(Color.FromArgb(255,
            Chan(m.Groups[1].Value), Chan(m.Groups[2].Value), Chan(m.Groups[3].Value)));
    return list;

    static byte Chan(string s) =>
        (byte)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 255), 0, 255);
}

static List<string> SvgFills(string svg)
{
    var doc = XDocument.Parse(svg);
    XNamespace ns = "http://www.w3.org/2000/svg";
    return doc.Descendants(ns + "text")
              .Select(e => (string?)e.Attribute("fill") ?? "")
              .ToList();
}

// ---------------------------------------------------------------------------
// PART 6's ONE MODELLED THING: the live RichEdit document, as the writer
// serialises it. A model of WINDOWS, not of Quill, and deliberately narrow:
//
//   * a document is a header (everything before the first \pard), a list of
//     paragraphs (the RTF between one \par and the next), and the writer's
//     tail after the last \par;
//   * its plain text is each paragraph's characters followed by '\r' - one
//     '\r' per \par the writer emits, which is what makes part 5's
//     round-trip model and the stored library consistent (N stored \par, one
//     more after an open);
//   * deleting paragraph mark k through ITextRange removes paragraph k when it
//     is a bare empty paragraph (no characters, no control words - nothing to
//     carry); otherwise its RTF is merged into paragraph k+1, whose mark (and
//     so whose \pard) survives. Only the bare case occurs on the trim's real
//     path; the merge exists so a negative control that deletes the WRONG
//     mark produces a wrong document instead of an exception.
//
// It is never asked about anything but fixtures in the writer's own shape,
// and 6a checks that Parse then Serialise is the identity on them.
sealed class LiveDoc
{
    private static readonly Regex ParTok = new(@"\\par(?![a-z])", RegexOptions.Compiled);
    private static readonly Regex ControlWord = new(@"\\[a-zA-Z]+-?\d* ?", RegexOptions.Compiled);

    private readonly string _header;
    private readonly List<string> _paras;
    private readonly string _tail;

    private LiveDoc(string header, List<string> paras, string tail)
    { _header = header; _paras = paras; _tail = tail; }

    public static LiveDoc Parse(string rtf)
    {
        int body = rtf.IndexOf(@"\pard", StringComparison.Ordinal);
        if (body < 0) body = rtf.Length;
        var pieces = ParTok.Split(rtf[body..]);
        return new LiveDoc(rtf[..body], pieces[..^1].ToList(), pieces[^1]);
    }

    public string Serialise()
    {
        var sb = new StringBuilder(_header);
        foreach (var p in _paras) sb.Append(p).Append(@"\par");
        return sb.Append(_tail).ToString();
    }

    private static string Chars(string para) =>
        ControlWord.Replace(para.Replace("\r\n", ""), "");

    public string Plain() => string.Concat(_paras.Select(p => Chars(p) + "\r"));

    // Deletes plain-text characters [start, start+length) - which must all be
    // paragraph marks, or the edit is refused (null), exactly as InkSurface
    // refuses. Returns the document the writer would then serialise.
    public LiveDoc? DeleteMarks(int start, int length)
    {
        string plain = Plain();
        if (start < 0 || length <= 0 || start + length > plain.Length) return null;
        for (int i = start; i < start + length; i++) if (plain[i] != '\r') return null;
        // plain index -> paragraph index: count the marks before it.
        var doomed = new HashSet<int>();
        int para = 0;
        for (int i = 0; i < start + length; i++)
        {
            if (plain[i] != '\r') continue;
            if (i >= start) doomed.Add(para);
            para++;
        }
        var outp = new List<string>();
        string carry = "";
        for (int k = 0; k < _paras.Count; k++)
        {
            string p = carry + _paras[k];
            carry = "";
            if (!doomed.Contains(k)) { outp.Add(p); continue; }
            bool bare = p.Replace("\r\n", "").Length == 0;
            if (!bare) carry = p;          // merged into the next paragraph
        }
        if (carry.Length > 0) return null; // the story's final mark cannot go
        return new LiveDoc(_header, outp, _tail);
    }
}
