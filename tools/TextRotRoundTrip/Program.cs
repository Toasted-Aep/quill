// CONCEPTS-REF 17.11a requirement 2, PROVED.
//
//   "I want every rotatable object to freely rotate."
//
// 17.11a said that could not ship because "a TextElement is an axis-aligned box
// that takes no rotation at all". It is not: TextElement.Rotation has been in
// the model since #20. So the sweep turns freely now, and the two things that
// makes true are settled here by doing them rather than by claiming them:
//
//   1. AN ANGLE IS STATE. It has to survive save -> reload to the bit, and a
//      page written before text carried one has to keep reading as the
//      un-turned page it is.
//   2. A FREE ANGLE IS NOT INVERTIBLE THE WAY A QUARTER TURN IS. The quarter
//      turn exchanges and negates coordinates and is exact in floating point; a
//      free angle is a sine and a cosine and is not. RotateFreeMixedAction
//      therefore holds the exact before-state for shapes and text and turns ink
//      back arithmetically - so "exact" is measured for the first and a bound is
//      measured for the second.
//
// Against the real things: the real models, the real serialiser, the real op
// log that Save calls, and the real UndoRedoManager.
//
// ISOLATION, exactly as tools/LayerRoundTrip does it. QUILL_DATA_FOLDER is set
// before the first call, a settings.json is seeded inside it so the loader never
// reaches for the pre-rename anchor, and the run ABORTS rather than continues if
// the resolved path is not inside the temp folder this process made. SyncLog's
// %LOCALAPPDATA%\Quill state is snapshotted and checked BEFORE it is put back,
// so a reopened leak says so instead of being papered over.

using System.Text.Json;
using Quill.Models;
using Quill.Services;

int failures = 0;
var log = new List<string>();

void Check(string label, bool ok, string detail = "")
{
    log.Add($"{(ok ? "PASS" : "FAIL")} {label}{(detail.Length > 0 ? "   -- " + detail : "")}");
    if (!ok) failures++;
}

// ---------------------------------------------------------------------------
// 0. An isolated library, and a hard refusal to run outside it.
// ---------------------------------------------------------------------------
string scratch = Path.Combine(Path.GetTempPath(), "quill-textrot-roundtrip", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("QUILL_DATA_FOLDER", scratch);

File.WriteAllText(Path.Combine(scratch, "settings.json"),
                  "{\"DataFolder\":null,\"ImportedLegacy\":true}");

string file = LibraryStore.FilePath;
if (!file.StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("ABORT: LibraryStore resolved to " + file);
    Console.Error.WriteLine("       which is not inside " + scratch);
    Console.Error.WriteLine("       Refusing to run: this test must never touch a real library.");
    return 2;
}
Check("the library under test is isolated in a temp folder", true, file);
Check("the isolated settings.json is inside it too, so nothing reaches for the "
      + "pre-rename anchor", !LibraryStore.SettingsUnreadable && LibraryStore.Settings.ImportedLegacy);

string localQuill = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quill");
string cursorPath = Path.Combine(localQuill, "synccursors.json");
string devicePath = Path.Combine(localQuill, "deviceid.txt");
byte[]? cursorsBefore = File.Exists(cursorPath) ? File.ReadAllBytes(cursorPath) : null;
byte[]? deviceBefore = File.Exists(devicePath) ? File.ReadAllBytes(devicePath) : null;

try
{

// ---------------------------------------------------------------------------
// 1. A PAGE FROM BEFORE TEXT CARRIED AN ANGLE.
// ---------------------------------------------------------------------------
// Hand-written, because the whole question is what happens to JSON that carries
// no "Rotation" key on a text box at all. It cannot be round-tripped out of
// today's model - today's model always writes one.
const string NbId = "0000aaaa-0000-0000-0000-0000000000a1";
const string SecId = "0000bbbb-0000-0000-0000-0000000000b1";
const string PgId = "0000cccc-0000-0000-0000-0000000000c1";
const string Tx0 = "0000f000-0000-0000-0000-0000000000f1";
const string Tx1 = "0000f000-0000-0000-0000-0000000000f2";
const string Sh0 = "0000e000-0000-0000-0000-0000000000e1";
const string St0 = "0000d000-0000-0000-0000-0000000000d1";

const string OldRtf = @"{\rtf1\ansi{\colortbl ;\red20\green20\blue19;}\cf1 square on the page\par}";

string oldJson =
    "{\"Notebooks\":[{\"Id\":\"" + NbId + "\",\"Name\":\"Before text turned\",\"Color\":\"#D97757\"," +
    "\"Sections\":[{\"Id\":\"" + SecId + "\",\"Name\":\"Section\",\"Pages\":[{" +
      "\"Id\":\"" + PgId + "\",\"Name\":\"Page one\",\"Background\":\"#FFFDF7\"," +
      "\"Strokes\":[{\"Id\":\"" + St0 + "\",\"Pen\":0,\"Color\":\"#2B6CB0\",\"Size\":3," +
        "\"Points\":[{\"X\":100,\"Y\":100,\"Pressure\":0.5},{\"X\":300,\"Y\":100,\"Pressure\":0.7}]}]," +
      "\"Shapes\":[{\"Id\":\"" + Sh0 + "\",\"Kind\":1,\"X\":200,\"Y\":200,\"W\":100,\"H\":40," +
        "\"Color\":\"#B4530A\"}]," +
      "\"Texts\":[" +
        "{\"Id\":\"" + Tx0 + "\",\"X\":40,\"Y\":400,\"Width\":300,\"Rtf\":" +
          JsonSerializer.Serialize(OldRtf) + "}," +
        "{\"Id\":\"" + Tx1 + "\",\"X\":500,\"Y\":600,\"Width\":180,\"Rtf\":" +
          JsonSerializer.Serialize(OldRtf) + "}]," +
      "\"Comments\":[]}]}]}]}";

File.WriteAllText(file, oldJson);
Check("the file under test carries no text angle at all - it is genuinely older "
      + "than a turnable text box",
      !oldJson.Contains("Rotation"), oldJson.Length + " bytes");

var lib = LibraryStore.Load();
Check("a library written before text turned loads", !LibraryStore.LoadFailed,
      LibraryStore.LoadError ?? "no error");

var page = lib.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("the page came back", page != null);
if (page == null) { Report(); return 1; }
Check("with exactly the content it had",
      page.Strokes.Count == 1 && page.Shapes.Count == 1 && page.Texts.Count == 2,
      $"{page.Strokes.Count} strokes, {page.Shapes.Count} shapes, {page.Texts.Count} texts");

Check("17.11a - a text box written before the angle existed reads back at 0, "
      + "which is exactly the square box it was drawn as",
      page.Texts.All(t => t.Rotation == 0.0),
      string.Join(", ", page.Texts.Select(t => t.Rotation.ToString("R"))));

// ---------------------------------------------------------------------------
// 2. THE ANGLE IS STATE: it survives save -> reload TO THE BIT.
// ---------------------------------------------------------------------------
// The angles are chosen to be the ones a free drag actually produces and a
// quarter step never does: not round, not representable as a short decimal, and
// one of them negative and one a hair under the (-180, 180] wrap.
double[] angles = { 37.371234567890123, -179.99940000000001, 0.5000000000000001 };
var t0 = page.Texts[0];
var t1 = page.Texts[1];
var sh = page.Shapes[0];
t0.Rotation = angles[0];
t1.Rotation = angles[1];
sh.Rotation = angles[2];

LibraryStore.EnableSaving();
LibraryStore.Save(lib);
LibraryStore.Flush();

string saved = File.ReadAllText(file);
// "R" rather than the literal digits typed above: System.Text.Json writes the
// SHORTEST representation that round-trips, which for this value is one digit
// shorter than the source text. Pinning the typed digits would have been
// pinning the compiler's rendering of the constant, not the store's.
string angleText = angles[0].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
Check("a save by this build writes the text angle",
      saved.Contains("\"Rotation\":" + angleText),
      angleText + " found in " + saved.Length + " bytes");

var back = LibraryStore.Load();
var bp = back.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("and it reloads", bp != null && !LibraryStore.LoadFailed, LibraryStore.LoadError ?? "no error");
if (bp == null) { Report(); return 1; }

var b0 = bp.Texts.First(t => t.Id.ToString() == Tx0);
var b1 = bp.Texts.First(t => t.Id.ToString() == Tx1);
var bsh = bp.Shapes.First(s => s.Id.ToString() == Sh0);

// BitConverter, not a tolerance. "Round-trips with no loss" is a claim about
// bits, and a tolerance is how a claim about bits gets downgraded to a claim
// about pixels without anybody noticing.
Check("17.11a - a free angle on a text box round-trips BIT FOR BIT",
      BitConverter.DoubleToInt64Bits(b0.Rotation) == BitConverter.DoubleToInt64Bits(angles[0]),
      b0.Rotation.ToString("R"));
Check("...including a negative one a hair inside the wrap",
      BitConverter.DoubleToInt64Bits(b1.Rotation) == BitConverter.DoubleToInt64Bits(angles[1]),
      b1.Rotation.ToString("R"));
Check("...and a shape's, on the same page, unchanged by any of this",
      BitConverter.DoubleToInt64Bits(bsh.Rotation) == BitConverter.DoubleToInt64Bits(angles[2]),
      bsh.Rotation.ToString("R"));

// The rest of the box has to be intact too: an angle that arrived by trampling
// the width would pass every check above.
Check("nothing else about the turned box moved",
      b0.X == 40 && b0.Y == 400 && b0.Width == 300 && b0.Rtf == OldRtf,
      $"X={b0.X} Y={b0.Y} W={b0.Width}");

// A PAGE SAVED BY THIS BUILD STAYS READABLE. The strong form: every element's
// serialised bytes are identical across a second save/reload, so the angle has
// not made the file drift on each cycle.
var census1 = ElementCensus(bp);
LibraryStore.Save(back);
LibraryStore.Flush();
var again = LibraryStore.Load();
var ap = again.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("a page saved by this build reloads again", ap != null && !LibraryStore.LoadFailed);
if (ap == null) { Report(); return 1; }
var census2 = ElementCensus(ap);
Check("every element is byte-for-byte identical after a SECOND save and reload, "
      + "so a stored angle does not drift on each cycle",
      census1.Count == census2.Count &&
      census1.All(kv => census2.TryGetValue(kv.Key, out var v) && v == kv.Value),
      $"{census1.Count} elements compared");

// ---------------------------------------------------------------------------
// 3. THE FREE ROTATION, THROUGH THE REAL UNDO STACK.
// ---------------------------------------------------------------------------
// The geometry first, by hand. A text box turns about ITS OWN centre and its
// centre is carried round the pivot - so a box whose centre is 100 to the right
// of the pivot, turned a quarter clockwise, has its centre 100 BELOW the pivot
// and its angle up by 90. Anything else and the free sweep and the renderer
// disagree about what "rotate" means.
var geoPage = new NotePage();
var gt = new TextElement { X = 150, Y = 80, Width = 100, Rotation = 0 };   // centre (200, 100) for W=100,H=40
geoPage.Texts.Add(gt);
var mgr = new UndoRedoManager();
var sized = new List<RotateFreeMixedAction.SizedText>
{
    new(gt, 100, 40)
};
mgr.Push(new RotateFreeMixedAction(new List<PenStroke>(), new List<ShapeElement>(), sized,
                                   100, 100, 90), geoPage);
Check("17.11a - a text box carries its CENTRE round the pivot and adds the "
      + "angle: centre (200,100) about (100,100) by 90 lands at (100,200)",
      Near(gt.X + 50, 100) && Near(gt.Y + 20, 200) && Near(gt.Rotation, 90),
      $"centre ({gt.X + 50:0.###}, {gt.Y + 20:0.###}) at {gt.Rotation:0.###} deg");

mgr.Undo(geoPage);
Check("...and undo puts it back EXACTLY - the before-state is held, not "
      + "recomputed by turning the other way",
      gt.X == 150.0 && gt.Y == 80.0 && gt.Rotation == 0.0,
      $"X={gt.X.ToString("R")} Y={gt.Y.ToString("R")} rot={gt.Rotation.ToString("R")}");

mgr.Redo(geoPage);
Check("...and redo turns it again", Near(gt.X + 50, 100) && Near(gt.Y + 20, 200) && Near(gt.Rotation, 90));

// Four free quarter turns are a full circle, and the LAST undo of four has to
// land on the original numbers rather than four accumulated sines.
var spinPage = new NotePage();
var st = new TextElement { X = 11.25, Y = 22.5, Width = 137, Rotation = -12.5 };
var ss = new ShapeElement { X = 300, Y = 40, W = 90, H = 60, Rotation = 5.25 };
spinPage.Texts.Add(st); spinPage.Shapes.Add(ss);
double sx = st.X, sy = st.Y, sr = st.Rotation;
double hx = ss.X, hy = ss.Y, hr = ss.Rotation;
var spin = new UndoRedoManager();
for (int i = 0; i < 4; i++)
    spin.Push(new RotateFreeMixedAction(
        new List<PenStroke>(), new List<ShapeElement> { ss },
        new List<RotateFreeMixedAction.SizedText> { new(st, 137, 40) }, 500, 500, 90), spinPage);
Check("four free 90 degree turns about a distant pivot bring a box back to "
      + "where it started",
      Near(st.X, sx, 1e-9) && Near(st.Y, sy, 1e-9) && Near(WrapTo180(st.Rotation), sr, 1e-9),
      $"X={st.X:0.#########} Y={st.Y:0.#########} rot={st.Rotation:0.#########}");
for (int i = 0; i < 4; i++) spin.Undo(spinPage);
Check("and undoing all four restores the EXACT doubles both objects started "
      + "with - a free angle cannot creep through the stack",
      st.X == sx && st.Y == sy && st.Rotation == sr &&
      ss.X == hx && ss.Y == hy && ss.Rotation == hr,
      $"text ({st.X.ToString("R")}, {st.Y.ToString("R")}, {st.Rotation.ToString("R")})");

// INK IS THE ONE THING TURNED BACK ARITHMETICALLY, because a copy of every
// point per action would size the undo stack like the page. So the bound is
// measured rather than assumed. A float coordinate carries about 7 significant
// digits; at a world coordinate of 10^3 a rounding step is ~1e-4, so a tenth of
// a world unit after ten full cycles is a real ceiling, not a generous one.
var inkPage = new NotePage();
var stroke = new PenStroke { Color = "#141413", Size = 3 };
for (int i = 0; i < 64; i++) stroke.Points.Add(new StrokePoint(1000 + i * 3f, 700 - i * 2f, 0.5f));
inkPage.Strokes.Add(stroke);
var inkBefore = stroke.Points.Select(p => (p.X, p.Y)).ToList();
var inkMgr = new UndoRedoManager();
for (int cycle = 0; cycle < 10; cycle++)
{
    inkMgr.Push(new RotateFreeMixedAction(new List<PenStroke> { stroke }, new List<ShapeElement>(),
                                          new List<RotateFreeMixedAction.SizedText>(),
                                          640, 480, 37.37), inkPage);
    inkMgr.Undo(inkPage);
}
double worst = 0;
for (int i = 0; i < stroke.Points.Count; i++)
{
    double dx = stroke.Points[i].X - inkBefore[i].X, dy = stroke.Points[i].Y - inkBefore[i].Y;
    worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
}
Check("ink turned by a free angle and back, TEN times, drifts less than a tenth "
      + "of a world unit - the price of not snapshotting every point per action",
      worst < 0.1, $"worst point moved {worst:0.######} world units over 10 cycles");

// THE ANGLE A FREE ROTATION LEAVES IS A STORABLE ONE. A sweep that wound past
// half a turn must not leave 400 degrees in the model where every other angle
// in this codebase lives in (-180, 180].
var wrapPage = new NotePage();
var wt = new TextElement { X = 0, Y = 0, Width = 100, Rotation = 170 };
wrapPage.Texts.Add(wt);
new RotateFreeMixedAction(new List<PenStroke>(), new List<ShapeElement>(),
                          new List<RotateFreeMixedAction.SizedText> { new(wt, 100, 40) },
                          0, 0, 50).Do(wrapPage);
Check("17.11a - a sweep past half a turn wraps into (-180, 180], the window the "
      + "page angle already uses",
      wt.Rotation > -180 && wt.Rotation <= 180 && Near(wt.Rotation, -140),
      wt.Rotation.ToString("0.###"));

// AND IT STILL PERSISTS. The whole point of the section: an angle a free drag
// produced, not one a test assigned.
var live = LibraryStore.Load();
var lp = live.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
if (lp == null) { Report(); return 1; }
var lt = lp.Texts.First(t => t.Id.ToString() == Tx0);
new RotateFreeMixedAction(new List<PenStroke>(), new List<ShapeElement>(),
                          new List<RotateFreeMixedAction.SizedText> { new(lt, 300, 96) },
                          123.5, 456.25, -63.125).Do(lp);
double turned = lt.Rotation;
double tx = lt.X, ty = lt.Y;
LibraryStore.Save(live);
LibraryStore.Flush();
var reread = LibraryStore.Load();
var rp = reread.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
if (rp == null) { Report(); return 1; }
var rt = rp.Texts.First(t => t.Id.ToString() == Tx0);
Check("17.11a, the whole claim in one line: an angle and a position produced by "
      + "an actual free rotation survive save -> reload BIT FOR BIT",
      BitConverter.DoubleToInt64Bits(rt.Rotation) == BitConverter.DoubleToInt64Bits(turned) &&
      BitConverter.DoubleToInt64Bits(rt.X) == BitConverter.DoubleToInt64Bits(tx) &&
      BitConverter.DoubleToInt64Bits(rt.Y) == BitConverter.DoubleToInt64Bits(ty),
      $"{turned.ToString("R")} at ({tx.ToString("R")}, {ty.ToString("R")})");

// ---------------------------------------------------------------------------
// 4. THE QUARTER TURN SURVIVES, AND STILL AGREES WITH ITSELF.
// ---------------------------------------------------------------------------
// It is the mode bar's Rotate button now rather than the sweep's detent, so it
// is still reachable and still has to be exact - four presses return the page.
var qPage = new NotePage();
var qt = new TextElement { X = 60, Y = 90, Width = 240, Rotation = 0 };
qPage.Texts.Add(qt);
double qx = qt.X, qy = qt.Y;
var qMgr = new UndoRedoManager();
for (int i = 0; i < 4; i++)
    qMgr.Push(new RotateQuarterMixedAction(new List<PenStroke>(), new List<ShapeElement>(),
                                           new List<TextElement> { qt }, 400, 300), qPage);
Check("16.2's quarter turn is still here and still exact: four presses return a "
      + "text box to the same doubles it started with",
      qt.X == qx && qt.Y == qy && Math.Abs(WrapTo180(qt.Rotation)) < 1e-12,
      $"X={qt.X.ToString("R")} Y={qt.Y.ToString("R")} rot={qt.Rotation.ToString("R")}");

// ---------------------------------------------------------------------------
Report();
return failures > 0 ? 1 : 0;

}
finally
{
    bool cleanBefore = SameBytes(cursorPath, cursorsBefore) && SameBytes(devicePath, deviceBefore);
    RestoreOrRemove(cursorPath, cursorsBefore);
    RestoreOrRemove(devicePath, deviceBefore);
    bool cursorsOk = SameBytes(cursorPath, cursorsBefore) && cleanBefore;
    bool deviceOk = SameBytes(devicePath, deviceBefore) && cleanBefore;
    Console.WriteLine((cursorsOk && deviceOk ? "PASS" : "FAIL") +
        " isolation - SyncLog's %LOCALAPPDATA%\\Quill state is byte-for-byte as it " +
        "was found   -- synccursors " + (cursorsOk ? "untouched" : "DIRTY") +
        ", deviceid " + (deviceOk ? "untouched" : "DIRTY"));
    if (!cursorsOk || !deviceOk) Environment.ExitCode = 1;
    try { Directory.Delete(scratch, recursive: true); } catch { }
}

// ---------------------------------------------------------------------------

void Report()
{
    foreach (var l in log) Console.WriteLine(l);
    Console.WriteLine();
    if (failures > 0) Console.WriteLine($"FAILED {failures} check(s).");
    else Console.WriteLine($"OK - {log.Count} checks held. CONCEPTS-REF 17.11a's "
                           + "text angle is a measurement, not a claim.");
}

static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) < eps;

static double WrapTo180(double deg)
{
    deg %= 360;
    if (deg > 180) deg -= 360;
    if (deg <= -180) deg += 360;
    return deg;
}

/// <summary>Every element's serialised bytes, keyed by id. What must not move
/// when a page is saved and reloaded a second time.</summary>
static Dictionary<string, string> ElementCensus(NotePage p)
{
    var o = new JsonSerializerOptions { WriteIndented = false };
    var d = new Dictionary<string, string>();
    foreach (var s in p.Strokes) d["st:" + s.Id] = JsonSerializer.Serialize(s, o);
    foreach (var s in p.Shapes) d["sh:" + s.Id] = JsonSerializer.Serialize(s, o);
    foreach (var t in p.Texts) d["tx:" + t.Id] = JsonSerializer.Serialize(t, o);
    return d;
}

static void RestoreOrRemove(string path, byte[]? original)
{
    try
    {
        if (original == null) { if (File.Exists(path)) File.Delete(path); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, original);
        }
    }
    catch { }
}

static bool SameBytes(string path, byte[]? original)
{
    try
    {
        if (original == null) return !File.Exists(path);
        return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(original);
    }
    catch { return false; }
}
