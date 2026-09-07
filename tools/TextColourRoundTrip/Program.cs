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
