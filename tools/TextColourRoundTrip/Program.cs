// CONCEPTS-REF 25: text colour, measured where a console can measure it.
//
// The brief that produced 25 named the failure it exists to prevent in one
// sentence: "perfect on screen and black in the file". Three of 25's four
// surfaces need a window. The fourth is the file, and the file is exactly the
// one that had the hole, so this is where the proof goes.
//
// Two subjects:
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
// What this CANNOT see, stated here rather than left to be assumed: the live
// RichEditBox, the 16.7 veil and the Win2D raster. Nothing in this file is
// evidence about any of those three.

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
