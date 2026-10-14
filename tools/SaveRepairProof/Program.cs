// HOTFIX hotfix-save-stop, PROVED - "one Artboard click silently stops every save".
//
// From the cloud data-safety audit, finding 1. The chain this harness runs
// against the REAL sources (NoteModels.cs, LibraryStore.cs, SyncLog.cs, compiled
// in - nothing is a copy):
//
//   Settings > Artboard on an infinite page shows "U+221E" in both boxes;
//   double.TryParse of that is TRUE under ICU and yields +Infinity; SetPageSize
//   stored it; System.Text.Json refuses to write Infinity; and Save swallowed the
//   exception with a bare return - no WriteError, no log. Every later save
//   failed the same way, including the close save.
//
// What is proved here, in the order the defect travels:
//   1. the parse     PageSizes.TryParseDimension refuses U+221E, "Infinity", "NaN",
//                    empty, zero, negatives, > 1,000,000, and accepts real numbers,
//                    in en-US, tr-TR and de-DE (the box is formatted in the same
//                    culture it is read in).
//   2. the setter    PageSizes.TryApply (what MainWindow.SetPageSize calls) refuses a
//                    non-finite Custom size WHOLE and leaves the page untouched.
//   3. the save      a library holding Infinity/NaN in a page size, in a stroke
//                    point, a text box, a shape and the panel height is SAVED: a
//                    valid file, every value repaired, the rest intact, the user
//                    told (RepairNotice) and crash.log naming each field.
//   4. the failure   a model that cannot be serialised for ANY reason sets
//                    WriteError instead of vanishing.
//   5. the load      a library that already carries an extreme page size loads
//                    repaired and saves; one whose size is not a number at all is
//                    refused by the loader's existing gate (reported, not hidden).
//
// NEGATIVE CONTROLS (see the .csproj): -p:Control=silent|parse|apply|repair
// compiles a mutated copy of the shipping file with one fix switched off. Each
// must compile and turn the checks named in its header red.
//
// ISOLATION is the pattern tools/LayerRoundTrip set: QUILL_DATA_FOLDER before
// the first LibraryStore call, a seeded settings.json, and an ABORT if the
// resolved path is not inside the temp folder this process made.

using System.Globalization;
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
void Note(string label, string detail) => log.Add($"NOTE {label}   -- {detail}");

void Report()
{
    foreach (var l in log) Console.WriteLine(l);
    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
}

void Run(Action section)
{
    try { section(); }
    catch (Exception ex)
    {
        Check("a section ran to completion without an unexpected exception", false,
              ex.GetType().Name + ": " + ex.Message);
    }
}

char Inf = '∞';
string InfText = Inf.ToString();

// ---------------------------------------------------------------------------
// 0. Isolation.
// ---------------------------------------------------------------------------
string scratch = Path.Combine(Path.GetTempPath(), "quill-save-repair-proof", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("QUILL_DATA_FOLDER", scratch);
File.WriteAllText(Path.Combine(scratch, "settings.json"), "{\"DataFolder\":null,\"ImportedLegacy\":true}");

string file = LibraryStore.FilePath;
if (!file.StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("ABORT: LibraryStore resolved to " + file + ", not inside " + scratch);
    return 2;
}
Check("the library under test is isolated in a temp folder", true, file);

string localQuill = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quill");
string cursorPath = Path.Combine(localQuill, "synccursors.json");
byte[]? cursorsBefore = File.Exists(cursorPath) ? File.ReadAllBytes(cursorPath) : null;

string crashLog() => File.Exists(Path.Combine(scratch, "crash.log"))
    ? File.ReadAllText(Path.Combine(scratch, "crash.log")) : "";

NotePage? FirstPage(Library l) => l.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();

Library OneNotebook(string name, params NotePage[] pages)
{
    var lib = new Library();
    var nb = new Notebook { Name = name };
    var sec = new Section { Name = "Section" };
    foreach (var p in pages) sec.Pages.Add(p);
    nb.Sections.Add(sec);
    lib.Notebooks.Add(nb);
    return lib;
}

try
{

// ---------------------------------------------------------------------------
// 1. THE PARSE.
// ---------------------------------------------------------------------------
var savedCulture = CultureInfo.CurrentCulture;

// The precondition, measured rather than assumed: does THIS runtime reproduce the
// defect? Under ICU the infinite sign is the culture's PositiveInfinitySymbol.
CultureInfo.CurrentCulture = new CultureInfo("en-US");
bool oldParseTakesInfinity = double.TryParse(InfText, out double oldParsed) && double.IsPositiveInfinity(oldParsed);
Note("precondition: the OLD parse, double.TryParse(\"U+221E\", out v), in en-US",
     oldParseTakesInfinity
         ? "returned TRUE and +Infinity - the defect is reproducible on this runtime"
         : "returned false on this runtime (invariant globalization?), so the parse checks below are weaker than on the owner's machine");

bool stjRefuses;
try { JsonSerializer.Serialize(new { v = double.PositiveInfinity }); stjRefuses = false; }
catch (Exception) { stjRefuses = true; }
Check("precondition: System.Text.Json refuses to WRITE +Infinity (the reason a stored Infinity stops saves)", stjRefuses);

foreach (var cultureName in new[] { "en-US", "tr-TR", "de-DE" })
{
    CultureInfo.CurrentCulture = new CultureInfo(cultureName);
    string c = $"[{cultureName}] ";
    string[] refused =
    {
        InfText, "-" + InfText, "+" + InfText, " " + InfText + " ", "Infinity", "-Infinity", "NaN",
        "", "   ", "abc", "12abc", "0", "0.0", "-5", "-0", "1000000.5", "1000001", "1e9", "1e999", "1e-999",
    };
    foreach (var t in refused)
        Check(c + $"TryParseDimension refuses \"{(t.Contains(Inf) ? t.Replace(InfText, "U+221E") : t)}\"",
              !PageSizes.TryParseDimension(t, out double got) && got == 0);
    Check(c + "TryParseDimension refuses null", !PageSizes.TryParseDimension(null, out _));

    var ci = CultureInfo.CurrentCulture;
    string dec = (12.5).ToString("0.##", ci);
    Check(c + $"accepts what the box itself shows for 12.5 (\"{dec}\")",
          PageSizes.TryParseDimension(dec, out double v125) && v125 == 12.5);
    Check(c + "accepts \"800\"", PageSizes.TryParseDimension("800", out double v800) && v800 == 800);
    Check(c + "accepts \" 1024 \" (the box trims)", PageSizes.TryParseDimension(" 1024 ", out double v1024) && v1024 == 1024);
    Check(c + "accepts the maximum, 1,000,000", PageSizes.TryParseDimension("1000000", out double vmax) && vmax == PageSizes.MaxDimension);
    Check(c + "accepts \"1e3\" (NumberStyles.Float allows an exponent)", PageSizes.TryParseDimension("1e3", out double v1e3) && v1e3 == 1000);
}
CultureInfo.CurrentCulture = savedCulture;

// ---------------------------------------------------------------------------
// 2. THE SETTER: PageSizes.TryApply, what MainWindow.SetPageSize calls.
// ---------------------------------------------------------------------------
Run(() =>
{
    var a4 = new NotePage { PageSize = PageSizePreset.A4, PageUnit = PageSizeUnit.Millimeters };
    var cust = new NotePage { PageSize = PageSizePreset.Custom, PageWidth = 640, PageHeight = 480, PageUnit = PageSizeUnit.Pixels };
    var inf = new NotePage();

    bool r1 = PageSizes.TryApply(inf, PageSizePreset.Custom, double.PositiveInfinity, double.PositiveInfinity, PageSizeUnit.Inches);
    Check("SetPageSize(Custom, +Inf, +Inf) is refused", !r1);
    Check("...and the infinite page is exactly as it was (still Infinite, 0 x 0, unit untouched)",
          inf.PageSize == PageSizePreset.Infinite && inf.PageWidth == 0 && inf.PageHeight == 0 && inf.PageUnit == PageSizeUnit.Pixels,
          $"{inf.PageSize} {inf.PageWidth}x{inf.PageHeight} {inf.PageUnit}");

    Check("SetPageSize(Custom, NaN, 500) is refused", !PageSizes.TryApply(inf, PageSizePreset.Custom, double.NaN, 500, PageSizeUnit.Pixels));
    Check("SetPageSize(Custom, 500, -Inf) is refused", !PageSizes.TryApply(inf, PageSizePreset.Custom, 500, double.NegativeInfinity, PageSizeUnit.Pixels));
    Check("SetPageSize(Custom, -1, 500) is refused", !PageSizes.TryApply(inf, PageSizePreset.Custom, -1, 500, PageSizeUnit.Pixels));
    Check("SetPageSize(Custom, 500, 1e300) is refused", !PageSizes.TryApply(inf, PageSizePreset.Custom, 500, 1e300, PageSizeUnit.Pixels));
    Check("a page that was Custom 640x480 is unchanged after a refused Infinity",
          !PageSizes.TryApply(cust, PageSizePreset.Custom, double.PositiveInfinity, 480, PageSizeUnit.Inches) &&
          cust.PageWidth == 640 && cust.PageHeight == 480 && cust.PageUnit == PageSizeUnit.Pixels && cust.PageSize == PageSizePreset.Custom);

    Check("a good Custom size is applied", PageSizes.TryApply(inf, PageSizePreset.Custom, 1024, 768, PageSizeUnit.Pixels) &&
          inf.PageSize == PageSizePreset.Custom && inf.PageWidth == 1024 && inf.PageHeight == 768);
    Check("the Units row's pass-through of a Custom page's own 0 x 0 is still accepted (unit changes, nothing else)",
          PageSizes.TryApply(new NotePage { PageSize = PageSizePreset.Custom }, PageSizePreset.Custom, 0, 0, PageSizeUnit.Inches));
    Check("a preset ignores w/h, as before: A4 with garbage dimensions still applies A4",
          PageSizes.TryApply(a4, PageSizePreset.Letter, double.NaN, double.PositiveInfinity, PageSizeUnit.Inches) &&
          a4.PageSize == PageSizePreset.Letter && a4.PageUnit == PageSizeUnit.Inches && a4.PageWidth == 0);
    Check("Infinite is applied", PageSizes.TryApply(inf, PageSizePreset.Infinite, 0, 0, PageSizeUnit.Pixels) && inf.PageSize == PageSizePreset.Infinite);
});

// ---------------------------------------------------------------------------
// 3. PageSizes.Repair, directly.
// ---------------------------------------------------------------------------
Run(() =>
{
    var c1 = new NotePage { PageSize = PageSizePreset.Custom, PageWidth = double.PositiveInfinity, PageHeight = 100 };
    string? w1 = PageSizes.Repair(c1);
    Check("Repair: a Custom page holding +Infinity goes back to Infinite, 0 x 0",
          w1 != null && c1.PageSize == PageSizePreset.Infinite && c1.PageWidth == 0 && c1.PageHeight == 0, w1 ?? "no repair");

    var c2 = new NotePage { PageSize = PageSizePreset.A4, PageWidth = double.NaN, PageHeight = double.NegativeInfinity };
    string? w2 = PageSizes.Repair(c2);
    Check("Repair: a PRESET page keeps its preset (its custom dimensions were never read)",
          w2 != null && c2.PageSize == PageSizePreset.A4 && c2.PageWidth == 0 && c2.PageHeight == 0, w2 ?? "no repair");

    var c3 = new NotePage { PageSize = PageSizePreset.Custom, PageWidth = 1e300, PageHeight = 1e300, PageUnit = PageSizeUnit.Pixels };
    Check("Repair: a finite but absurd 1e300 is repaired too", PageSizes.Repair(c3) != null && c3.PageSize == PageSizePreset.Infinite);

    var c4 = new NotePage { PageSize = PageSizePreset.Custom, PageWidth = 1_000_000, PageHeight = 1_000_000,
                            PageUnit = PageSizeUnit.Miles, UnitsPerInch = 1e305 };
    Check("Repair: a size inside the maximum that OVERFLOWS when converted to world units is repaired",
          PageSizes.Repair(c4) != null && c4.PageSize == PageSizePreset.Infinite);

    var c5 = new NotePage { UnitsPerInch = double.NaN };
    Check("Repair: a NaN UnitsPerInch goes back to 96", PageSizes.Repair(c5) != null && c5.UnitsPerInch == 96);

    var ok1 = new NotePage { PageSize = PageSizePreset.Custom, PageWidth = 1024, PageHeight = 768 };
    var ok2 = new NotePage { PageSize = PageSizePreset.A4 };
    var ok3 = new NotePage();
    Check("Repair: leaves a good Custom page, a preset page and an infinite page ALONE (returns null)",
          PageSizes.Repair(ok1) == null && PageSizes.Repair(ok2) == null && PageSizes.Repair(ok3) == null &&
          ok1.PageSize == PageSizePreset.Custom && ok1.PageWidth == 1024 && ok2.PageSize == PageSizePreset.A4);
});

LibraryStore.EnableSaving();

// ---------------------------------------------------------------------------
// 4. A CLEAN LIBRARY SAVES WITH NO NOTICE AND NO WARNING.
// ---------------------------------------------------------------------------
Run(() =>
{
    var clean = OneNotebook("Clean", new NotePage { Name = "p", PageSize = PageSizePreset.Custom, PageWidth = 800, PageHeight = 600 });
    clean.Notebooks[0].Sections[0].Pages[0].Strokes.Add(new PenStroke { Points = { new StrokePoint(1, 2, 0.5f) } });
    LibraryStore.Save(clean);
    LibraryStore.Flush();
    Check("a clean library: the file is written", File.Exists(file) && new FileInfo(file).Length > 0);
    Check("a clean library: no repair notice, no write error (the fix is silent when nothing is wrong)",
          LibraryStore.RepairNotice == null && LibraryStore.WriteError == null,
          $"notice={LibraryStore.RepairNotice ?? "null"} error={LibraryStore.WriteError ?? "null"}");
    Check("a clean library: nothing in crash.log", crashLog().Length == 0);
});

// ---------------------------------------------------------------------------
// 5. A SIZE THAT IS NOT A NUMBER AT ALL (a string in the file).
// ---------------------------------------------------------------------------
Run(() =>
{
    var odd = OneNotebook("Odd", new NotePage { Name = "p", PageSize = PageSizePreset.Custom, PageWidth = 123456.789, PageHeight = 100 });
    string good = JsonSerializer.Serialize(odd);
    string bad = good.Replace("\"PageWidth\":123456.789", "\"PageWidth\":\"abc\"");
    Check("(setup) the seeded file really carries a non-numeric PageWidth", bad != good && bad.Contains("\"PageWidth\":\"abc\""));
    foreach (var stale in new[] { file + ".bak", file + ".corrupt", file + ".tmp" }) if (File.Exists(stale)) File.Delete(stale);
    File.WriteAllText(file, bad);
    byte[] before = File.ReadAllBytes(file);
    var l = LibraryStore.Load();
    Check("a page whose size is a STRING is refused by the existing loader gate (LoadFailed), not half-loaded",
          LibraryStore.LoadFailed, LibraryStore.LoadError ?? "no error text");
    Check("...and that refused file is byte-for-byte untouched on disk (nothing overwrote it)",
          File.ReadAllBytes(file).SequenceEqual(before));
});

// ---------------------------------------------------------------------------
// 6. A LIBRARY THAT ALREADY CARRIES AN EXTREME (FINITE) SIZE LOADS AND SAVES.
// ---------------------------------------------------------------------------
Run(() =>
{
    var ext = OneNotebook("Extreme",
        new NotePage { Name = "huge", PageSize = PageSizePreset.Custom, PageWidth = 1e300, PageHeight = 1e300 },
        new NotePage { Name = "fine", PageSize = PageSizePreset.Custom, PageWidth = 640, PageHeight = 480 });
    ext.Notebooks[0].Sections[0].Pages[0].Strokes.Add(new PenStroke { Points = { new StrokePoint(3, 4, 0.5f) } });
    File.WriteAllText(file, JsonSerializer.Serialize(ext));
    var loaded = LibraryStore.Load();
    Check("an extreme-but-finite size loads (LoadFailed is false)", !LibraryStore.LoadFailed, LibraryStore.LoadError ?? "no error");
    var pages = loaded.Notebooks[0].Sections[0].Pages;
    Check("...and the huge page was repaired in memory to Infinite, 0 x 0",
          pages[0].PageSize == PageSizePreset.Infinite && pages[0].PageWidth == 0 && pages[0].PageHeight == 0,
          $"{pages[0].PageSize} {pages[0].PageWidth}x{pages[0].PageHeight}");
    Check("...while its ink and the neighbouring good 640x480 page are untouched",
          pages[0].Strokes.Count == 1 && pages[1].PageSize == PageSizePreset.Custom && pages[1].PageWidth == 640 && pages[1].PageHeight == 480);
    Check("...and the user is told, with the field named",
          LibraryStore.RepairNotice != null && LibraryStore.RepairNotice.Contains("on load") && LibraryStore.RepairNotice.Contains("PageWidth"),
          LibraryStore.RepairNotice ?? "no notice");
    Check("...and crash.log names the page and the old value",
          crashLog().Contains("library repair (on load)") && crashLog().Contains("huge") && crashLog().Contains("1E+300"),
          crashLog().Trim().Split('\n').LastOrDefault()?.Trim() ?? "empty");

    loaded.Notebooks[0].Sections[0].Pages[1].Strokes.Add(new PenStroke { Points = { new StrokePoint(9, 9, 0.5f) } });
    LibraryStore.Save(loaded);
    LibraryStore.Flush();
    var back = LibraryStore.Load();
    var bp = back.Notebooks[0].Sections[0].Pages;
    Check("a loaded-then-edited library SAVES: the edit is in the file, the repair stuck",
          !LibraryStore.LoadFailed && bp[1].Strokes.Count == 1 && bp[0].PageSize == PageSizePreset.Infinite && bp[0].Strokes.Count == 1);
});

// ---------------------------------------------------------------------------
// 7. THE DEFECT ITSELF: a poisoned page size reaches Save.
// ---------------------------------------------------------------------------
Run(() =>
{
    var poisoned = new NotePage { Name = "clicked-W", PageSize = PageSizePreset.Custom,
                                  PageWidth = double.PositiveInfinity, PageHeight = double.NaN };
    var a4 = new NotePage { Name = "a4-with-junk", PageSize = PageSizePreset.A4, PageWidth = double.NegativeInfinity };
    var inkPage = new NotePage { Name = "ink" };
    inkPage.Strokes.Add(new PenStroke { Points = { new StrokePoint(5, 6, 0.5f), new StrokePoint(7, 8, 0.5f) } });
    var lib = OneNotebook("Poisoned", poisoned, a4, inkPage);
    bool serialiseThrows;
    try { JsonSerializer.Serialize(lib); serialiseThrows = false; } catch (Exception) { serialiseThrows = true; }
    Check("(setup) the model as the Artboard click leaves it cannot be serialised raw", serialiseThrows);

    File.Delete(Path.Combine(scratch, "crash.log"));
    string fileBefore = File.ReadAllText(file);
    LibraryStore.Save(lib);
    LibraryStore.Flush();
    string after = File.ReadAllText(file);
    Check("Save WROTE a new file (it is not the file it started with)", after != fileBefore);
    bool valid;
    try { using var doc = JsonDocument.Parse(after); valid = doc.RootElement.ValueKind == JsonValueKind.Object; } catch { valid = false; }
    Check("...and the file is valid JSON", valid);
    Check("...holding no Infinity or NaN token", !after.Contains("Infinity") && !after.Contains("NaN"));

    var back = LibraryStore.Load();
    var bp = back.Notebooks[0].Sections[0].Pages;
    Check("reloaded: the clicked page is back to Infinite, 0 x 0",
          !LibraryStore.LoadFailed && bp[0].PageSize == PageSizePreset.Infinite && bp[0].PageWidth == 0 && bp[0].PageHeight == 0,
          $"{bp[0].PageSize} {bp[0].PageWidth}x{bp[0].PageHeight}");
    Check("reloaded: the A4 page is still A4", bp[1].PageSize == PageSizePreset.A4 && bp[1].PageWidth == 0);
    Check("reloaded: the ink page kept both points", bp[2].Strokes.Count == 1 && bp[2].Strokes[0].Points.Count == 2);
    Check("the in-memory model was repaired too (the next save has nothing left to repair)",
          lib.Notebooks[0].Sections[0].Pages[0].PageSize == PageSizePreset.Infinite);
    Check("the user is told: RepairNotice names the field, and WriteError is not set (it saved)",
          LibraryStore.RepairNotice != null && LibraryStore.RepairNotice.Contains("clicked-W") && LibraryStore.RepairNotice.Contains("(on save)") && LibraryStore.WriteError == null,
          LibraryStore.RepairNotice ?? "no notice");
    string cl = crashLog();
    Check("crash.log says which page and which field, old value to new",
          cl.Contains("clicked-W") && cl.Contains("PageWidth Infinity / PageHeight NaN -> 0 / 0") && cl.Contains("a4-with-junk"),
          cl.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "empty");

    // saving CONTINUES: this is the whole point
    string noticeBefore = LibraryStore.RepairNotice!;
    inkPage.Strokes.Add(new PenStroke { Points = { new StrokePoint(1, 1, 0.5f) } });
    LibraryStore.Save(lib);
    LibraryStore.Flush();
    var back2 = LibraryStore.Load();
    Check("the NEXT save lands too: an edit made after the repair is in the file",
          back2.Notebooks[0].Sections[0].Pages[2].Strokes.Count == 2);
});

// ---------------------------------------------------------------------------
// 8. NOT ONLY PAGE SIZES: any double or float anywhere in the model.
// ---------------------------------------------------------------------------
Run(() =>
{
    var pg = new NotePage { Name = "deep" };
    pg.Strokes.Add(new PenStroke
    {
        Size = float.NaN,
        Opacity = float.PositiveInfinity,
        PressureCurve = new List<float> { 0.1f, float.PositiveInfinity, 0.9f },
        Points = { new StrokePoint(float.NaN, 5, 0.5f), new StrokePoint(1, 2, 0.5f) },
    });
    pg.Texts.Add(new TextElement { X = double.PositiveInfinity, Width = double.NaN });
    pg.Shapes.Add(new ShapeElement { W = double.NaN, H = 20, Size = float.NegativeInfinity, TColW = new List<double> { 10, double.PositiveInfinity } });
    var lib = OneNotebook("Deep", pg);
    lib.NotebookPanelH = double.NaN;
    lib.Liquidness = double.PositiveInfinity;
    bool throws;
    try { JsonSerializer.Serialize(lib); throws = false; } catch (Exception) { throws = true; }
    Check("(setup) the model cannot be serialised raw", throws);

    File.Delete(Path.Combine(scratch, "crash.log"));
    LibraryStore.Save(lib);
    LibraryStore.Flush();
    var back = LibraryStore.Load();
    var bp = FirstPage(back);
    Check("a valid file was written and reloads", !LibraryStore.LoadFailed && bp != null);
    if (bp != null)
    {
        var s = bp.Strokes[0];
        Check("stroke point NaN -> 0, its neighbour untouched", s.Points[0].X == 0 && s.Points[0].Y == 5 && s.Points[1].X == 1 && s.Points[1].Y == 2);
        Check("stroke Size NaN -> the shipped default 3", s.Size == 3f);
        Check("nullable stroke Opacity +Inf -> cleared to null (opaque)", s.Opacity == null);
        Check("pressure-curve list element +Inf -> 0, the others untouched",
              s.PressureCurve is { Count: 3 } pc && pc[0] == 0.1f && pc[1] == 0f && pc[2] == 0.9f);
        var t = bp.Texts[0];
        Check("text X +Inf -> 0 and Width NaN -> the shipped default 280", t.X == 0 && t.Width == 280, $"X={t.X} Width={t.Width}");
        Check("table column-width list element +Inf -> 0 (a List<double> inside a shape)", bp.Shapes[0].TColW is { Count: 2 } cw && cw[0] == 10 && cw[1] == 0);
        Check("shape W NaN -> 0, H untouched, Size -Inf -> default 3", bp.Shapes[0].W == 0 && bp.Shapes[0].H == 20 && bp.Shapes[0].Size == 3f);
    }
    Check("library settings: NotebookPanelH NaN -> 0 (unset), Liquidness +Inf -> the shipped 0.35",
          back.NotebookPanelH == 0 && Math.Abs(back.Liquidness - 0.35) < 1e-9, $"{back.NotebookPanelH} {back.Liquidness}");
    string cl = crashLog();
    Check("crash.log records WHY the first serialise failed", cl.Contains("could not be serialised"));
    Check("crash.log names the field by its path (stroke point)", cl.Contains("Strokes[0].Points[0].X NaN -> 0"),
          cl.Split('\n').FirstOrDefault(l => l.Contains("Points[0].X"))?.Trim() ?? "no such line");
    Check("crash.log names a text-box field and a library setting",
          cl.Contains("Texts[0].X Infinity -> 0") && cl.Contains("Library.NotebookPanelH"));
    Check("the user is told (about THIS save: the notice names the panel height), and it saved (WriteError null)",
          LibraryStore.RepairNotice != null && LibraryStore.RepairNotice.Contains("NotebookPanelH") && LibraryStore.WriteError == null, LibraryStore.RepairNotice ?? "no notice");
});

// ---------------------------------------------------------------------------
// 9. A MODEL THAT CANNOT BE SERIALISED EVEN AFTER THE REPAIR.
//    Not exercised, and said so: every property of the real models is a number
//    this walk repairs, a string the serialiser replaces, or a list of those, so
//    there is no input that survives the repair and still throws - a lone
//    surrogate in a name, tried first, simply serialises on this runtime. The
//    branch (WriteError set, nothing queued, library.json untouched) is the code
//    that replaced the old bare "return", and the 'silent' control (see the .csproj) is what
//    shows the repair branch is the one doing the work.
// ---------------------------------------------------------------------------
Note("section 9", "no input in the real model survives the repair and still fails to serialise, so the 'still fails' branch (WriteError) is verified by reading only");

// ---------------------------------------------------------------------------
// 10. SOURCE PINS. These do NOT prove the wiring - the screen check does - they
//     only stop a refactor quietly putting the old calls back.
// ---------------------------------------------------------------------------
Run(() =>
{
    string? FindRoot(string? from)
    {
        while (from != null && !File.Exists(Path.Combine(from, "Quill.sln"))) from = Path.GetDirectoryName(from);
        return from;
    }
    string? root = FindRoot(AppContext.BaseDirectory) ?? FindRoot(Directory.GetCurrentDirectory());
    if (root == null) Note("source pins", "Quill.sln not found above the output folder; pins skipped");
    else
    {
        string settings = File.ReadAllText(Path.Combine(root, "src", "Quill", "Controls", "SettingsWindow.cs"));
        string main = File.ReadAllText(Path.Combine(root, "src", "Quill", "MainWindow.xaml.cs"));
        Check("pin: the Artboard Commit reads its boxes with PageSizes.TryParseDimension",
              settings.Contains("PageSizes.TryParseDimension(wBox.Text") && settings.Contains("PageSizes.TryParseDimension(hBox.Text"));
        Check("pin: ...and no bare double.TryParse of those boxes is left",
              !settings.Contains("double.TryParse(wBox.Text") && !settings.Contains("double.TryParse(hBox.Text"));
        Check("pin: MainWindow.SetPageSize goes through PageSizes.TryApply", main.Contains("PageSizes.TryApply(_curPage"));
        Check("pin: the notebook grip stores no bare NotebookPanel.Height",
              !main.Contains("_library.NotebookPanelH = NotebookPanel.Height;"));
    }
});

}
finally
{
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    byte[]? cursorsAfter = File.Exists(cursorPath) ? File.ReadAllBytes(cursorPath) : null;
    bool same = (cursorsBefore == null && cursorsAfter == null) ||
                (cursorsBefore != null && cursorsAfter != null && cursorsBefore.SequenceEqual(cursorsAfter));
    Check("the real %LOCALAPPDATA%\\Quill\\synccursors.json was not touched by this run", same);
    try { Directory.Delete(scratch, true); } catch { }
}

Report();
return failures == 0 ? 0 : 1;
