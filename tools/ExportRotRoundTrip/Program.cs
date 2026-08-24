// CONCEPTS-REF 17.11a.1's last bullet, CLOSED and then PROVED.
//
//   "PdfExporter and HtmlSvgExporter ignore Rotation - for shapes exactly as
//    much as for text."
//
// The second half was true and is now fixed. The first half was not: stroked
// shapes have never exported square. InkSurface.FlattenShape turns every point
// list about ShapeCenter before it ever becomes a PdfVectorPath, so a rotated
// rectangle arrives at the emitter as four corners already in the right place.
// The exporter has no "Rotation" in it because a path needs none - which is
// exactly why grepping for the word found nothing and reads as an absence.
//
// What genuinely could not be flattened is text and images: glyphs and pixels
// are placed by a MATRIX, not by their corners. Those two are what this change
// gave an angle to, and what this tool measures:
//
//   1. A TURN IS A TURN. World y runs down, PDF y runs up, so the emitted
//      matrix is the world rotation with its angle negated. Both signs look
//      right in a review, so the angle is recovered back out of the emitted
//      bytes and compared, and the matrix is checked for orthonormality and a
//      determinant of +1 - a turn, never a flip and never a scale.
//   2. ONE BOX IS SEVERAL RECORDS. A text box is flattened one record per
//      wrapped line and every line must turn about the box's ONE shared centre.
//      Per-line centres would fan the box open - and would still look like
//      "text rotates" in a screenshot. So rigidity is measured, and the check
//      is then RUN AGAINST A DELIBERATELY FANNED BOX to show it can tell them
//      apart instead of passing everything.
//   3. NOTHING UN-TURNED MOVED. A page with no angles on it must emit the
//      bytes it emitted before the angle existed, which is checked by building
//      the same page through the pre-change constructor arity and comparing the
//      content streams byte for byte.
//
// Against the real things: the real PdfExporter, the real HtmlSvgExporter, the
// real PDF bytes (inflated back out of the content stream) and the real SVG
// markup (parsed as XML, not string-matched).

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Quill.Services;

int failures = 0;
var log = new List<string>();

void Check(string label, bool ok, string detail = "")
{
    log.Add($"{(ok ? "PASS" : "FAIL")} {label}{(detail.Length > 0 ? "   -- " + detail : "")}");
    if (!ok) failures++;
}

// ---------------------------------------------------------------------------
// The page under test. Offsets are zero so the world -> page mapping stays
// legible: PDF x = wx * K, PDF y = H*K - wy * K, and SVG is world px outright.
// ---------------------------------------------------------------------------
const double K = 72.0 / 96.0;
const double PageW = 800, PageH = 600;
const double Deg = 31.7;                      // two decimals: survives "0.##"
const string Face = "QuillNoSuchFace";        // resolves to nothing -> Helvetica

double hPt = PageH * K;
double Xp(double wx) => wx * K;
double Yp(double wy) => hPt - wy * K;

// A three-line text box: left 100, top 80, 240 x 90, so its centre is 220,125.
var boxCentre = (X: 220.0, Y: 125.0);
var anchors = new[] { (X: 104.0, Y: 96.0), (X: 104.0, Y: 117.6), (X: 104.0, Y: 139.2) };

// An image shape: 160 x 120 at 400,300, so its centre is 480,360.
const double ImX = 400, ImY = 300, ImW = 160, ImH = 120;
var imgCentre = (X: ImX + ImW / 2, Y: ImY + ImH / 2);

List<PdfVectorText> Lines(double deg, bool perLineCentre = false)
{
    var list = new List<PdfVectorText>();
    for (int i = 0; i < anchors.Length; i++)
    {
        string txt = "Line " + i;
        var runs = new List<PdfVectorTextRun> { new(txt, 16f, Face, false, false) };
        double cx = perLineCentre ? anchors[i].X : boxCentre.X;
        double cy = perLineCentre ? anchors[i].Y : boxCentre.Y;
        list.Add(new PdfVectorText((float)anchors[i].X, (float)anchors[i].Y, 16f, "#141413",
                                   txt, Face, runs, deg, cx, cy));
    }
    return list;
}

List<PdfVectorImage> Imgs(double deg) => new()
{
    new PdfVectorImage(ImX, ImY, ImW, ImH, 2, 2, new byte[2 * 2 * 4], deg, imgCentre.X, imgCentre.Y)
};

PdfVectorPage Page(List<PdfVectorText> texts, List<PdfVectorImage> imgs) =>
    new(PageW, PageH, 0, 0, "#FFFFFF",
        new List<PdfVectorPath>(), new List<PdfVectorDot>(), imgs, texts);

string PdfOf(PdfVectorPage pg) => Content(PdfExporter.CreateVector(new[] { pg }));
string SvgOf(PdfVectorPage pg) => HtmlSvgExporter.PageToSvg(pg);

string flat = PdfOf(Page(Lines(0), Imgs(0)));
string turned = PdfOf(Page(Lines(Deg), Imgs(Deg)));
string fanned = PdfOf(Page(Lines(Deg, perLineCentre: true), Imgs(Deg)));

// ---------------------------------------------------------------------------
// 1. The un-turned page is EXACTLY the page it always was.
// ---------------------------------------------------------------------------
Check("a content stream comes back out of the emitted PDF at all",
      flat.Length > 0 && turned.Length > 0, $"{flat.Length} / {turned.Length} bytes inflated");

// Built through the constructor arity that existed BEFORE the angle did. If the
// new parameters changed one byte of an un-turned export, this is where it shows.
var oldText = new List<PdfVectorText>();
for (int i = 0; i < anchors.Length; i++)
{
    string txt = "Line " + i;
    oldText.Add(new PdfVectorText((float)anchors[i].X, (float)anchors[i].Y, 16f, "#141413", txt, Face,
                                  new List<PdfVectorTextRun> { new(txt, 16f, Face, false, false) }));
}
var oldImgs = new List<PdfVectorImage> { new(ImX, ImY, ImW, ImH, 2, 2, new byte[2 * 2 * 4]) };
string legacy = PdfOf(Page(oldText, oldImgs));

Check("the angle is APPENDED: a page built through the pre-change constructor "
      + "emits the identical content stream", legacy == flat,
      legacy == flat ? $"{legacy.Length} bytes, byte for byte" : "the un-turned export MOVED");

Check("an un-turned line still emits the identity text matrix, unchanged",
      Regex.IsMatch(flat, @"1 0 0 1 -?[\d.]+ -?[\d.]+ Tm"), "1 0 0 1 tx ty Tm");
Check("an un-turned image still emits the plain axis-aligned placement",
      Regex.IsMatch(flat, @"q " + Num(ImW * K) + " 0 0 " + Num(ImH * K) + @" -?[\d.]+ -?[\d.]+ cm"),
      $"q {Num(ImW * K)} 0 0 {Num(ImH * K)} x y cm");
Check("an un-turned <text> carries no transform attribute at all",
      !SvgOf(Page(Lines(0), Imgs(0))).Contains("transform="), "nothing added where nothing turns");

// ---------------------------------------------------------------------------
// 2. The emitted text matrix is a rotation, and the one that was asked for.
// ---------------------------------------------------------------------------
var flatTm = AllTm(flat);
var turnTm = AllTm(turned);
Check("every line of the box emits its own text matrix",
      flatTm.Count == anchors.Length && turnTm.Count == anchors.Length,
      $"{turnTm.Count} matrices for {anchors.Length} lines");

var m0 = turnTm[0];
double a = m0[0], b = m0[1], c = m0[2], d = m0[3];
Check("the matrix's columns are unit length - a turn, not a scale",
      Near(a * a + b * b, 1, 1e-5) && Near(c * c + d * d, 1, 1e-5),
      $"|col1|^2={a * a + b * b:0.########}, |col2|^2={c * c + d * d:0.########}");
Check("the matrix's columns are perpendicular", Near(a * c + b * d, 0, 1e-5),
      $"dot={a * c + b * d:0.########}");
Check("its determinant is +1 - a turn, never a flip", Near(a * d - b * c, 1, 1e-5),
      $"det={a * d - b * c:0.########}");

// PDF y runs up, so the emitted angle is the world angle negated. Recovering it
// is the only way to settle a sign that reads as correct in either direction.
double recovered = Math.Atan2(-b, a) * 180.0 / Math.PI;
Check("the angle recovered back out of the matrix IS the angle that went in, "
      + "once the y-flip is undone", Near(recovered, Deg, 1e-3),
      $"asked {Deg}, recovered {recovered:0.#####}");
Check("and the raw matrix is NOT the world matrix - the flip really is applied",
      b < 0 && c > 0, $"b={b:0.####} c={c:0.####}, world order would be b>0");

// ---------------------------------------------------------------------------
// 3. The box turns RIGIDLY, about its one shared centre.
// ---------------------------------------------------------------------------
bool sameLinear = turnTm.All(m => Near(m[0], a, 1e-9) && Near(m[1], b, 1e-9)
                               && Near(m[2], c, 1e-9) && Near(m[3], d, 1e-9));
Check("every line of the box runs in the SAME direction - one turn for the box, "
      + "not one per line", sameLinear, "all matrices share their linear part");

bool anchorsRight = true;
for (int i = 0; i < anchors.Length; i++)
{
    var w = Turn(anchors[i].X, anchors[i].Y, boxCentre.X, boxCentre.Y, Deg);
    if (!Near(turnTm[i][4], Xp(w.X), 0.02) || !Near(turnTm[i][5], Yp(w.Y), 0.02)) anchorsRight = false;
}
Check("each line's emitted anchor is that line turned about the BOX's centre, "
      + "worked out independently here", anchorsRight,
      $"{anchors.Length} anchors within 0.02pt of the arithmetic");

double SpanOf(List<double[]> ms) =>
    Dist(ms[0][4], ms[0][5], ms[^1][4], ms[^1][5]);
Check("the box keeps its shape: first-to-last line distance survives the turn",
      Near(SpanOf(flatTm), SpanOf(turnTm), 0.02),
      $"flat {SpanOf(flatTm):0.###}pt, turned {SpanOf(turnTm):0.###}pt");

bool radiiHeld = true;
double cxp = Xp(boxCentre.X), cyp = Yp(boxCentre.Y);
for (int i = 0; i < anchors.Length; i++)
    if (!Near(Dist(flatTm[i][4], flatTm[i][5], cxp, cyp),
              Dist(turnTm[i][4], turnTm[i][5], cxp, cyp), 0.02)) radiiHeld = false;
Check("and every line stays the distance it was from that shared centre", radiiHeld,
      "no line drifts in or out as the box turns");

// The check above has to be able to FAIL, or it is decoration. Same box, same
// angle, but one centre PER LINE - what deriving a centre per record would give.
var fanTm = AllTm(fanned);
bool fanAnchorsRight = true;
for (int i = 0; i < anchors.Length; i++)
{
    var w = Turn(anchors[i].X, anchors[i].Y, boxCentre.X, boxCentre.Y, Deg);
    if (!Near(fanTm[i][4], Xp(w.X), 0.02) || !Near(fanTm[i][5], Yp(w.Y), 0.02)) fanAnchorsRight = false;
}
Check("the shared-centre check has teeth: a box given one centre PER LINE fails it",
      !fanAnchorsRight, "the fanned box is detected, not waved through");

bool fanNeverMoved = true;
for (int i = 0; i < anchors.Length; i++)
    if (!Near(fanTm[i][4], flatTm[i][4], 0.02) || !Near(fanTm[i][5], flatTm[i][5], 0.02))
        fanNeverMoved = false;
Check("and this is how it would have hidden: every line spins on its own start, "
      + "so no anchor moves and the box never actually turns", fanNeverMoved,
      "line direction tilts, box position does not - a screenshot would still look rotated");

Check("the fanned box's lines DO still tilt, so the only thing separating it from "
      + "the real turn is the centre", fanTm.All(m => Near(m[0], a, 1e-9) && Near(m[1], b, 1e-9)),
      "same linear part, wrong pivot - exactly the failure the shared centre prevents");

// ---------------------------------------------------------------------------
// 4. The turn composes and inverts the way a rotation must.
// ---------------------------------------------------------------------------
var back = AllTm(PdfOf(Page(Lines(-Deg), Imgs(-Deg))))[0];
Check("turning by -angle emits the transpose of turning by +angle",
      Near(back[0], a, 1e-5) && Near(back[1], -b, 1e-5)
      && Near(back[2], -c, 1e-5) && Near(back[3], d, 1e-5),
      "the inverse of a rotation is its transpose");
Check("the two compose to the identity",
      Near(a * back[0] + c * back[1], 1, 1e-5) && Near(b * back[0] + d * back[1], 0, 1e-5)
      && Near(a * back[2] + c * back[3], 0, 1e-5) && Near(b * back[2] + d * back[3], 1, 1e-5),
      "M(t) . M(-t) = I, measured on the emitted numbers");

var full = AllTm(PdfOf(Page(Lines(360), Imgs(360))))[0];
Check("360 degrees emits the identity matrix - no angle normalisation bug hiding "
      + "in the wrap", Near(full[0], 1, 1e-5) && Near(Math.Abs(full[1]), 0, 1e-5)
      && Near(Math.Abs(full[2]), 0, 1e-5) && Near(full[3], 1, 1e-5),
      $"[{full[0]} {full[1]} {full[2]} {full[3]}]");
Check("...and puts the anchor back where the un-turned page put it",
      Near(full[4], flatTm[0][4], 0.02) && Near(full[5], flatTm[0][5], 0.02),
      "a full turn moves nothing");

Check("a turned anchor is genuinely somewhere else - the change does something",
      !Near(turnTm[0][4], flatTm[0][4], 0.5) || !Near(turnTm[0][5], flatTm[0][5], 0.5),
      $"flat ({flatTm[0][4]:0.##}, {flatTm[0][5]:0.##}) -> turned ({turnTm[0][4]:0.##}, {turnTm[0][5]:0.##})");

// ---------------------------------------------------------------------------
// 5. The image turns too, and turns the same way text does.
// ---------------------------------------------------------------------------
var cm = Cm(turned)!;
var cmFlat = Cm(flat)!;
double e1x = cm[0], e1y = cm[1], e2x = cm[2], e2y = cm[3];
Check("the image's cm matrix keeps both edge lengths - the picture turns, it "
      + "does not stretch", Near(Math.Sqrt(e1x * e1x + e1y * e1y), ImW * K, 0.02)
      && Near(Math.Sqrt(e2x * e2x + e2y * e2y), ImH * K, 0.02),
      $"{Math.Sqrt(e1x * e1x + e1y * e1y):0.##} x {Math.Sqrt(e2x * e2x + e2y * e2y):0.##} pt");
// Normalised: the cm entries are written at "0.##" like every other coordinate,
// so a raw dot product of two ~100pt edges carries the rounding of all four.
double cosBetween = (e1x * e2x + e1y * e2y)
                  / (Math.Sqrt(e1x * e1x + e1y * e1y) * Math.Sqrt(e2x * e2x + e2y * e2y));
Check("its edges stay perpendicular", Near(cosBetween, 0, 1e-3),
      $"cos between edges = {cosBetween:0.#######}");
Check("the angle recovered from the image matrix is the angle that went in",
      Near(Math.Atan2(-e1y, e1x) * 180.0 / Math.PI, Deg, 1e-2),
      $"recovered {Math.Atan2(-e1y, e1x) * 180.0 / Math.PI:0.####}");
Check("text and image agree on the direction of the turn - the two kinds that "
      + "needed an angle both got the SAME one",
      Near(Math.Atan2(-e1y, e1x), Math.Atan2(-b, a), 5e-4),
      "no subject kind is left square, and none is turned the other way");

// The unit square's four corners, mapped through cm, are the image's corners.
var quad = new[] { Map(cm, 0, 0), Map(cm, 1, 0), Map(cm, 1, 1), Map(cm, 0, 1) };
var quadFlat = new[] { Map(cmFlat, 0, 0), Map(cmFlat, 1, 0), Map(cmFlat, 1, 1), Map(cmFlat, 0, 1) };
bool cornersRight = true;
double icx = Xp(imgCentre.X), icy = Yp(imgCentre.Y);
for (int i = 0; i < 4; i++)
{
    // Turn the un-turned corner about the centre in PDF space (angle negated by
    // the flip) and compare to where the emitted matrix actually puts it.
    double r = -Deg * Math.PI / 180.0;
    double dx = quadFlat[i].X - icx, dy = quadFlat[i].Y - icy;
    double ex = icx + dx * Math.Cos(r) - dy * Math.Sin(r);
    double ey = icy + dx * Math.Sin(r) + dy * Math.Cos(r);
    if (!Near(quad[i].X, ex, 0.05) || !Near(quad[i].Y, ey, 0.05)) cornersRight = false;
}
Check("all four image corners land where turning the un-turned corners about the "
      + "shape's centre puts them", cornersRight, "the quad is the same quad, turned");

double qcx = quad.Average(p => p.X), qcy = quad.Average(p => p.Y);
Check("and the image's centre does not move - it turns about itself",
      Near(qcx, icx, 0.05) && Near(qcy, icy, 0.05),
      $"centre ({qcx:0.##}, {qcy:0.##}) vs ({icx:0.##}, {icy:0.##})");

// ---------------------------------------------------------------------------
// 6. The SVG says the same thing, in SVG's own terms.
// ---------------------------------------------------------------------------
string svgFlat = SvgOf(Page(Lines(0), Imgs(0)));
string svgTurn = SvgOf(Page(Lines(Deg), Imgs(Deg)));

XDocument? docFlat = null, docTurn = null;
try { docFlat = XDocument.Parse(svgFlat); docTurn = XDocument.Parse(svgTurn); } catch { }
Check("both SVGs are well-formed XML - the transform attribute did not break the "
      + "markup", docFlat != null && docTurn != null,
      docTurn != null ? "parsed by XDocument, not by string matching" : "PARSE FAILED");

XNamespace svgNs = "http://www.w3.org/2000/svg";
var texts = docTurn!.Descendants(svgNs + "text").ToList();
var image = docTurn.Descendants(svgNs + "image").First();
string want = $"rotate({Deg.ToString("0.##", CultureInfo.InvariantCulture)} "
            + $"{boxCentre.X.ToString("0.##", CultureInfo.InvariantCulture)} "
            + $"{boxCentre.Y.ToString("0.##", CultureInfo.InvariantCulture)})";

Check("every <text> of the box carries a rotate() about the box's shared centre",
      texts.Count == anchors.Length && texts.All(t => (string?)t.Attribute("transform") == want),
      want);
string degStr = Deg.ToString("0.##", CultureInfo.InvariantCulture);
Check("SVG's angle is NOT negated - its user space runs y-down like the canvas, "
      + "so the sign differs from the PDF matrix on purpose",
      ((string?)texts[0].Attribute("transform"))!.Contains("rotate(" + degStr + ' '),
      $"SVG says +{degStr}, the PDF matrix carries {-recovered:0.##} in its own y-up frame");
Check("the <image> turns about its own centre, by the same angle",
      ((string?)image.Attribute("transform")) ==
        $"rotate({Deg.ToString("0.##", CultureInfo.InvariantCulture)} "
      + $"{imgCentre.X.ToString("0.##", CultureInfo.InvariantCulture)} "
      + $"{imgCentre.Y.ToString("0.##", CultureInfo.InvariantCulture)})",
      (string?)image.Attribute("transform") ?? "(none)");

Check("the turn goes on the element, so a turned box keeps the x/y it always had",
      (string?)texts[0].Attribute("x") == (string?)docFlat!.Descendants(svgNs + "text").First().Attribute("x")
      && (string?)texts[0].Attribute("y") == (string?)docFlat.Descendants(svgNs + "text").First().Attribute("y"),
      "rotate() carries the anchor with it - nothing else had to move");

var flatTspans = docFlat.Descendants(svgNs + "tspan").Select(x => x.ToString()).ToList();
var turnTspans = docTurn.Descendants(svgNs + "tspan").Select(x => x.ToString()).ToList();
Check("the runs inside are untouched by the turn - tilted text is still "
      + "selectable text with its formatting intact",
      flatTspans.SequenceEqual(turnTspans), $"{turnTspans.Count} tspans identical");

Check("the image is still ONE <image> with its own width and height, simply turned",
      (string?)image.Attribute("width") == ImW.ToString("0.##", CultureInfo.InvariantCulture)
      && (string?)image.Attribute("height") == ImH.ToString("0.##", CultureInfo.InvariantCulture)
      && ((string?)image.Attribute("href"))!.StartsWith("data:image/png;base64,"),
      "not re-rasterised, not split, not dropped");

// The whole-document guarantee: turning is the ONLY difference between the two.
string stripped = Regex.Replace(svgTurn, " transform=\"rotate\\([^\"]*\\)\"", "");
Check("stripping the rotate() attributes off the turned SVG gives back the "
      + "un-turned SVG exactly", stripped == svgFlat,
      stripped == svgFlat ? "one attribute is the entire difference" : "something else changed too");

// ---------------------------------------------------------------------------
Report();
return failures > 0 ? 1 : 0;

// ---------------------------------------------------------------------------

void Report()
{
    foreach (var l in log) Console.WriteLine(l);
    Console.WriteLine();
    if (failures > 0) Console.WriteLine($"FAILED {failures} check(s).");
    else Console.WriteLine($"OK - {log.Count} checks held. CONCEPTS-REF 17.11a.1's "
                           + "export angle is a measurement, not a claim.");
}

static bool Near(double x, double y, double eps) => Math.Abs(x - y) < eps;

static double Dist(double x0, double y0, double x1, double y1)
    => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

/// <summary>The checker's own rotation arithmetic, written independently of the
/// exporter's, so "where should this land" is answered twice.</summary>
static (double X, double Y) Turn(double px, double py, double cx, double cy, double deg)
{
    double r = deg * Math.PI / 180.0;
    double cos = Math.Cos(r), sin = Math.Sin(r);
    double dx = px - cx, dy = py - cy;
    return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
}

/// <summary>A PDF matrix [a b c d e f] applied to a point.</summary>
static (double X, double Y) Map(double[] m, double x, double y)
    => (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]);

/// <summary>The page's content stream, inflated back out of the real PDF bytes.
/// Font files carry /Length1 and images carry /Type /XObject, so this pattern
/// picks out the content stream and nothing else.</summary>
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

static List<double[]> AllTm(string content)
{
    var list = new List<double[]>();
    foreach (Match m in Regex.Matches(content,
        @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm"))
        list.Add(Enumerable.Range(1, 6)
            .Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray());
    return list;
}

static double[]? Cm(string content)
{
    var m = Regex.Match(content,
        @"q (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm /Im0 Do Q");
    return m.Success
        ? Enumerable.Range(1, 6)
            .Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray()
        : null;
}
