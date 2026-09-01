// THE CLONE PATHS, PROVED.
//
// PenStroke.CloneWithPoints has always said why it carries the layer key:
//
//   "leaving it out would silently drop every fragment of an erased stroke onto
//    the base layer - content that is intact, moved, and impossible to notice
//    until the layer it was on is hidden (CONCEPTS-REF 18.10)."
//
// Six other copy paths did not honour it. Each had written its own initialiser
// list, and between them they dropped the pen, the opacity, the padlock, the
// equation source, the axis labels, every table cell's fill and border, and the
// layer key. CloneWithPoints itself dropped two of them - Opacity and Locked -
// so a rub through a 40% highlight handed back fragments at 100%.
//
// Three things are settled here, by doing them rather than claiming them:
//
//   1. COMPLETENESS. A copy carries every field but the Id. Checked BY
//      REFLECTION over the real models, against a fixture that is itself checked
//      for having left no field at its default - so the test cannot pass by
//      exercising half the class, and a field added tomorrow and forgotten in the
//      clone fails HERE instead of shipping.
//   2. THE COUNTERFACTUAL. The initialiser lists that shipped are re-created and
//      run through the same checks, and they FAIL. A green test whose red state
//      was never seen is not evidence.
//   3. PERSISTENCE. The copy survives save -> reload with its layer intact, and
//      is really hidden when that layer is hidden - measured through the real
//      PageLayers.IsVisible, which is the exact sentence 18.10 is about.
//
// Plus the one field a copy must NOT carry blindly - a cell's TableId - and the
// duplicate rule that decides when it may.
//
// ISOLATION, exactly as tools/LayerRoundTrip and tools/TextRotRoundTrip do it.
// QUILL_DATA_FOLDER is set before the first call, a settings.json is seeded
// inside it so the loader never reaches for the pre-rename anchor, and the run
// ABORTS rather than continues if the resolved path is not inside the temp folder
// this process made. SyncLog's %LOCALAPPDATA%\Quill state is snapshotted and
// checked BEFORE it is put back, so a reopened leak says so instead of being
// papered over.

using System.Reflection;
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
string scratch = Path.Combine(Path.GetTempPath(), "quill-clone-roundtrip", Guid.NewGuid().ToString("N"));
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
// 1. THE FIXTURES, AND THE PROOF THAT THEY ARE FIXTURES.
// ---------------------------------------------------------------------------
// Every writable field set to something that is NOT its default. Checked, not
// trusted: a fixture that left a field alone would let a clone that drops that
// field pass, which is precisely how the shipped lists went unnoticed.
var srcStroke = new PenStroke
{
    Pen = PenType.Crayon, Color = "#B4530A", Size = 7.25f, Sens = 0.625f, Opacity = 0.4f,
    Points = { new StrokePoint(10, 20, 0.3f), new StrokePoint(40, 55, 0.9f) },
    Locked = true, LayerKey = 5, CreatedTicks = 638000000000000001,
    PressureCurve = new List<float> { 0.25f, 0.4f, 0.5f, 0.6f, 0.75f, 0.8f }
};
var srcShape = new ShapeElement
{
    Kind = ShapeKind.Table, X = 11.5, Y = 22.25, W = 340, H = 180,
    Color = "#2B6CB0", Size = 4.5f, Pen = PenType.Marker, Opacity = 0.35f,
    ImagePath = "attachments/plot.png", EquationLatex = @"\frac{x^2}{2}",
    AxisLabelX = "t", AxisLabelY = "v", AxisLabelZ = "w",
    Rotation = 37.371234567890123,
    TRows = 3, TCols = 2,
    TColW = new List<double> { 120, 220 }, TRowH = new List<double> { 40, 50, 90 },
    FillColor = "#FFF1D0", BorderColor = "#8A3B00", BorderWidth = 2.5f,
    MergeColSpan = 2, MergeRowSpan = 3, HeaderRow = true,
    Locked = true, LayerKey = 7, CreatedTicks = 638000000000000002
};
var srcText = new TextElement
{
    X = 64.5, Y = 128.25, Width = 311, WidthPinned = true, MaxWidth = 880, AutoWidth = true,
    Rtf = @"{\rtf1\ansi hello\par}", TextColor = "#C2185B", Rotation = -63.125,
    TableId = Guid.Parse("0000aaaa-0000-0000-0000-00000000ab01"), TableRow = 2, TableCol = 1,
    FillColor = "#E8F0FF", BorderColor = "#123456", BorderWidth = 1.75f,
    CellColSpan = 2, CellRowSpan = 3,
    Locked = true, LayerKey = 9, CreatedTicks = 638000000000000003
};

Check("the PenStroke fixture leaves no field at its default", NoDefaults(srcStroke, "Id"),
      DefaultsLeft(srcStroke, "Id"));
Check("the ShapeElement fixture leaves no field at its default", NoDefaults(srcShape, "Id"),
      DefaultsLeft(srcShape, "Id"));
Check("the TextElement fixture leaves no field at its default", NoDefaults(srcText, "Id"),
      DefaultsLeft(srcText, "Id"));

// ---------------------------------------------------------------------------
// 2. COMPLETENESS: a copy carries every field but the Id.
// ---------------------------------------------------------------------------
// By reflection, so this is a statement about the CLASS and not about the list
// of properties whoever wrote the test happened to think of.
var clonedStroke = srcStroke.CloneWithPoints(
    srcStroke.Points.Select(p => new StrokePoint(p.X, p.Y, p.Pressure)).ToList());
var clonedShape = srcShape.Clone();
var clonedText = srcText.Clone();

Check("PenStroke.CloneWithPoints carries EVERY field but the Id",
      Same(srcStroke, clonedStroke, out string sd, "Id"), sd);
Check("ShapeElement.Clone carries EVERY field but the Id",
      Same(srcShape, clonedShape, out string hd, "Id"), hd);
Check("TextElement.Clone carries EVERY field but the Id",
      Same(srcText, clonedText, out string td, "Id"), td);

Check("...and the Id is a NEW one on each - that is what makes it a copy rather "
      + "than a second reference",
      clonedStroke.Id != srcStroke.Id && clonedShape.Id != srcShape.Id && clonedText.Id != srcText.Id);

// Equal by value is not enough for a list: a SHARED list means widening the
// copy's second column also widens the original's.
Check("a copy's mutable lists are its own, not shared with the original",
      !ReferenceEquals(clonedShape.TColW, srcShape.TColW) &&
      !ReferenceEquals(clonedShape.TRowH, srcShape.TRowH) &&
      !ReferenceEquals(clonedStroke.PressureCurve, srcStroke.PressureCurve));
clonedShape.TColW![0] = 999;
clonedShape.TRowH![0] = 999;
Check("...proved by writing to the copy's column and row lists and finding the "
      + "original's untouched",
      srcShape.TColW![0] == 120 && srcShape.TRowH![0] == 40,
      $"original still {srcShape.TColW[0]} x {srcShape.TRowH[0]}");
clonedShape.TColW[0] = 120;
clonedShape.TRowH[0] = 40;

// ---------------------------------------------------------------------------
// 3. THE COUNTERFACTUAL: the lists that shipped, run through the same checks.
// ---------------------------------------------------------------------------
// A test that has only ever been seen green is not evidence. These are the
// initialiser lists as they actually stood in InkSurface.CloneShape and
// InkSurface.CloneText, reproduced verbatim, and they must FAIL.
var shippedShapeClone = new ShapeElement
{
    Kind = srcShape.Kind, X = srcShape.X, Y = srcShape.Y, W = srcShape.W, H = srcShape.H,
    Color = srcShape.Color, Size = srcShape.Size, ImagePath = srcShape.ImagePath,
    Rotation = srcShape.Rotation,
    TRows = srcShape.TRows, TCols = srcShape.TCols,
    TColW = srcShape.TColW != null ? new List<double>(srcShape.TColW) : null,
    TRowH = srcShape.TRowH != null ? new List<double>(srcShape.TRowH) : null,
    FillColor = srcShape.FillColor, BorderColor = srcShape.BorderColor,
    BorderWidth = srcShape.BorderWidth,
    MergeColSpan = srcShape.MergeColSpan, MergeRowSpan = srcShape.MergeRowSpan,
    HeaderRow = srcShape.HeaderRow
};
var shippedTextClone = new TextElement
{
    X = srcText.X, Y = srcText.Y, Width = srcText.Width, Rtf = srcText.Rtf,
    Rotation = srcText.Rotation
};

Check("the clipboard's OLD shape copy fails this same check - the harness can go red",
      !Same(srcShape, shippedShapeClone, out string oldShapeDrop, "Id"), oldShapeDrop);
Check("the clipboard's OLD text copy fails it too",
      !Same(srcText, shippedTextClone, out string oldTextDrop, "Id"), oldTextDrop);
Check("and both of them dropped the LAYER KEY specifically - the 18.10 field",
      shippedShapeClone.LayerKey == 0 && shippedTextClone.LayerKey == 0 &&
      srcShape.LayerKey == 7 && srcText.LayerKey == 9,
      $"copies landed on layer {shippedShapeClone.LayerKey} / {shippedTextClone.LayerKey}, "
      + $"originals were on {srcShape.LayerKey} / {srcText.LayerKey}");

// ---------------------------------------------------------------------------
// 4. THE FIELD A COPY MUST NOT CARRY BLINDLY.
// ---------------------------------------------------------------------------
// TableId is the reverse case: carrying it is what does the damage, because a
// table repositions every text that names it. CloneAsFreeBox cuts the cell
// identity and NOTHING else - the words, the angle, the padlock, the layer and
// the cell's own fill and border all survive.
var freeBox = srcText.CloneAsFreeBox();
Check("TextElement.Clone keeps the cell identity - it is the copy a duplicated "
      + "TABLE re-links to itself",
      clonedText.TableId == srcText.TableId && clonedText.TableRow == 2 && clonedText.TableCol == 1);
Check("TextElement.CloneAsFreeBox cuts it: no table, no row, no column, no spans",
      freeBox.TableId == null && freeBox.TableRow == 0 && freeBox.TableCol == 0 &&
      freeBox.CellColSpan == 1 && freeBox.CellRowSpan == 1);
Check("...and cuts NOTHING else: a detached cell keeps its words, its angle, its "
      + "padlock, its LAYER and its own fill and border",
      Same(srcText, freeBox, out string fbDrop,
           "Id", "TableId", "TableRow", "TableCol", "CellColSpan", "CellRowSpan"),
      fbDrop);

// ---------------------------------------------------------------------------
// 5. THE DUPLICATE RULE, over a table and its cells.
// ---------------------------------------------------------------------------
// A lasso round a table catches the table AND its cell bubbles: SelectWithLasso
// adds any text whose centre is inside, and a cell is a text. So the duplicate
// sees the same cell twice - once by walking the table's cells, once in the
// selected texts - and must emit it ONCE, wired to the copy.
var tbl = new ShapeElement { Kind = ShapeKind.Table, X = 100, Y = 100, W = 200, H = 80,
                             TRows = 1, TCols = 2, LayerKey = 4, Locked = true };
var cell0 = new TextElement { X = 106, Y = 102, Width = 88, Rtf = "left",
                              TableId = tbl.Id, TableRow = 0, TableCol = 0,
                              FillColor = "#FFEEDD", LayerKey = 4 };
var cell1 = new TextElement { X = 206, Y = 102, Width = 88, Rtf = "right",
                              TableId = tbl.Id, TableRow = 0, TableCol = 1, LayerKey = 4 };
var loose = new TextElement { X = 500, Y = 500, Width = 200, Rtf = "a free box", LayerKey = 6 };
var strayCell = new TextElement { X = 700, Y = 700, Width = 90, Rtf = "cell of another table",
                                  TableId = Guid.Parse("0000cccc-0000-0000-0000-00000000cc01"),
                                  TableRow = 3, TableCol = 4, LayerKey = 6 };

var dupPage = new NotePage();
dupPage.Shapes.Add(tbl);
dupPage.Texts.AddRange(new[] { cell0, cell1, loose, strayCell });

var (dupStrokes, dupShapes, dupTexts) = ElementClone.Duplicate(
    Array.Empty<PenStroke>(), new[] { tbl }, new[] { cell0, cell1, loose, strayCell },
    dupPage.Texts, 40f);

var tblCopy = dupShapes.Single();
Check("duplicating a lassoed table yields ONE table copy", dupShapes.Count == 1 && dupStrokes.Count == 0);
Check("...offset by 40 on both axes, and still locked, still on its layer",
      tblCopy.X == 140 && tblCopy.Y == 140 && tblCopy.Locked && tblCopy.LayerKey == 4,
      $"({tblCopy.X}, {tblCopy.Y}) locked={tblCopy.Locked} layer={tblCopy.LayerKey}");
Check("...and exactly FOUR texts come with it - the table's two cells, re-linked, "
      + "plus the two selected texts that are not cells OF IT. The cells the lasso "
      + "caught are not copied a second time",
      dupTexts.Count == 4, $"{dupTexts.Count} texts: " +
      string.Join(", ", dupTexts.Select(t => $"\"{t.Rtf}\"")));
Check("no copy anywhere still names the ORIGINAL table - that copy would be "
      + "dragged back into the original's grid by its next reflow, landing on top "
      + "of the cell it came from",
      dupTexts.All(t => t.TableId != tbl.Id),
      string.Join(", ", dupTexts.Select(t => t.TableId?.ToString() ?? "free")));
Check("both cells are re-linked to the COPY, keeping their row, column and fill",
      dupTexts.Count(t => t.TableId == tblCopy.Id) == 2 &&
      dupTexts.Any(t => t.TableId == tblCopy.Id && t.TableCol == 0 && t.FillColor == "#FFEEDD") &&
      dupTexts.Any(t => t.TableId == tblCopy.Id && t.TableCol == 1));
Check("the free box is copied as a free box, offset, on its own layer",
      dupTexts.Any(t => t.Rtf == "a free box" && t.TableId == null &&
                        t.X == 540 && t.Y == 540 && t.LayerKey == 6));
Check("and a cell whose OWN table is not part of this duplicate becomes a free "
      + "box rather than a bubble pointing at a table that was never copied",
      dupTexts.Any(t => t.Rtf == "cell of another table" && t.TableId == null &&
                        t.TableRow == 0 && t.TableCol == 0 && t.LayerKey == 6));

// The regression this replaced, spelled out. The shipped text branch copied every
// selected text AND carried its TableId, on top of the cells CloneTableCells had
// already re-linked - so the same lasso produced SIX texts, two of them ghosts
// still naming the original table and destined to be reflowed on top of the very
// cells they were copied from.
var shippedDupTexts = new List<TextElement>();
foreach (var c in new[] { cell0, cell1 })                       // CloneTableCells, re-linked
    shippedDupTexts.Add(new TextElement { Rtf = c.Rtf, TableId = tblCopy.Id });
foreach (var t in new[] { cell0, cell1, loose, strayCell })     // the text branch, as it stood
    shippedDupTexts.Add(new TextElement { Rtf = t.Rtf, TableId = t.TableId });
int shippedGhosts = shippedDupTexts.Count(t => t.TableId == tbl.Id);
Check("the OLD duplicate emitted six texts for this same selection, two of them "
      + "ghosts pointing back at the original table",
      shippedDupTexts.Count == 6 && shippedGhosts == 2 &&
      dupTexts.Count == 4 && dupTexts.Count(t => t.TableId == tbl.Id) == 0,
      $"was {shippedDupTexts.Count} texts with {shippedGhosts} ghost(s), "
      + $"now {dupTexts.Count} with 0");

// ---------------------------------------------------------------------------
// 6. PERSISTENCE, AND 18.10 IN THE ONE SENTENCE IT IS ABOUT.
// ---------------------------------------------------------------------------
// The claim is not "the field is assigned". It is "the copy is still on that
// layer after a save and a reload, and is hidden when that layer is hidden".
// Measured through the real store and the real PageLayers.IsVisible.
var lib = new Library();
var nb = new Notebook { Name = "Clones" };
var sec = new Section { Name = "Section" };
var page = new NotePage { Name = "Page one" };
nb.Sections.Add(sec); sec.Pages.Add(page); lib.Notebooks.Add(nb);

PageLayers.Materialise(page);
var hidden = PageLayers.Add(page, "Working");
hidden.Hidden = true;
int hiddenKey = hidden.Key;
Check("the page has a real second layer, and it is hidden",
      hiddenKey != 0 && !PageLayers.IsVisible(page, hiddenKey) && PageLayers.IsVisible(page, 0),
      $"layer key {hiddenKey}");

var onHidden = new PenStroke
{
    Pen = PenType.Highlighter, Color = "#FFD166", Size = 18f, Opacity = 0.4f,
    Points = { new StrokePoint(200, 200, 0.5f), new StrokePoint(400, 240, 0.7f) },
    Locked = true, LayerKey = hiddenKey
};
var shapeOnHidden = new ShapeElement
{
    Kind = ShapeKind.AxesXYZ, X = 300, Y = 300, W = 200, H = 200, Pen = PenType.Marker,
    Opacity = 0.55f, EquationLatex = @"y=\sin x", AxisLabelX = "time", AxisLabelY = "volts",
    AxisLabelZ = "depth", Locked = true, LayerKey = hiddenKey
};
var textOnHidden = new TextElement
{
    X = 600, Y = 600, Width = 240, Rtf = @"{\rtf1\ansi on the working layer\par}",
    Rotation = 12.5, Locked = true, LayerKey = hiddenKey
};
page.Strokes.Add(onHidden); page.Shapes.Add(shapeOnHidden); page.Texts.Add(textOnHidden);

var (cs, ch, ct) = ElementClone.Duplicate(
    new[] { onHidden }, new[] { shapeOnHidden }, new[] { textOnHidden }, page.Texts, 40f);
page.Strokes.AddRange(cs); page.Shapes.AddRange(ch); page.Texts.AddRange(ct);

var copyStrokeId = cs.Single().Id;
var copyShapeId = ch.Single().Id;
var copyTextId = ct.Single().Id;

LibraryStore.EnableSaving();
LibraryStore.Save(lib);
LibraryStore.Flush();

var back = LibraryStore.Load();
var bp = back.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("the library reloads", bp != null && !LibraryStore.LoadFailed, LibraryStore.LoadError ?? "no error");
if (bp == null) { Report(); return 1; }

var bStroke = bp.Strokes.First(s => s.Id == copyStrokeId);
var bShape = bp.Shapes.First(s => s.Id == copyShapeId);
var bText = bp.Texts.First(t => t.Id == copyTextId);

Check("18.10 - a duplicated STROKE comes back off disk still on the layer it was "
      + "copied from, not on the base layer",
      bStroke.LayerKey == hiddenKey, $"layer {bStroke.LayerKey}, wanted {hiddenKey}");
Check("...and PageLayers agrees it is HIDDEN, which is the whole sentence: the "
      + "copy disappears with its layer instead of surviving on the base one",
      !PageLayers.IsVisible(bp, bStroke.LayerKey));
Check("...with its opacity and its padlock intact - a rub through a 40% highlight "
      + "no longer hands back fragments at 100%",
      bStroke.Opacity == 0.4f && bStroke.Locked,
      $"opacity {bStroke.Opacity?.ToString() ?? "null"}, locked {bStroke.Locked}");
Check("18.10 - a duplicated SHAPE likewise, with its pen, opacity, equation "
      + "source and all three axis labels",
      bShape.LayerKey == hiddenKey && !PageLayers.IsVisible(bp, bShape.LayerKey) &&
      bShape.Locked && bShape.Pen == PenType.Marker && bShape.Opacity == 0.55f &&
      bShape.EquationLatex == @"y=\sin x" && bShape.AxisLabelX == "time" &&
      bShape.AxisLabelY == "volts" && bShape.AxisLabelZ == "depth",
      $"layer {bShape.LayerKey}, pen {bShape.Pen}, opacity {bShape.Opacity}");
Check("18.10 - and a duplicated TEXT BOX, with its angle and its padlock",
      bText.LayerKey == hiddenKey && !PageLayers.IsVisible(bp, bText.LayerKey) &&
      bText.Locked && bText.Rotation == 12.5,
      $"layer {bText.LayerKey}, rot {bText.Rotation}");

// The counterfactual again, and this time through the store: the copy the old
// code made lands on the base layer, and PageLayers says it is VISIBLE - the
// content that "is intact, moved, and impossible to notice".
var strandedShape = shippedShapeClone;   // LayerKey 0, exactly as the old list left it
Check("the copy the OLD code made is on the base layer and PageLayers calls it "
      + "VISIBLE - on a page whose working layer is hidden, that is the bug: the "
      + "copy stays on screen after the layer it belongs to is switched off",
      strandedShape.LayerKey == 0 && PageLayers.IsVisible(bp, strandedShape.LayerKey));

// A second save/reload must not drift: every element identical byte for byte.
var census1 = ElementCensus(bp);
LibraryStore.Save(back);
LibraryStore.Flush();
var again = LibraryStore.Load();
var ap = again.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("a page of copies saved by this build reloads again", ap != null && !LibraryStore.LoadFailed);
if (ap == null) { Report(); return 1; }
var census2 = ElementCensus(ap);
Check("every element is byte-for-byte identical after a SECOND save and reload, "
      + "so nothing a copy carries drifts on each cycle",
      census1.Count == census2.Count &&
      census1.All(kv => census2.TryGetValue(kv.Key, out var v) && v == kv.Value),
      $"{census1.Count} elements compared");

// And the copies really are separate rows on the page, not aliases of the
// originals: moving one must not move the other.
Check("a copy is a separate element on the page, not a second reference to the "
      + "original", bp.Strokes.Count == 2 && bp.Shapes.Count == 2 && bp.Texts.Count == 2 &&
      !ReferenceEquals(bp.Strokes[0], bp.Strokes[1]),
      $"{bp.Strokes.Count} strokes, {bp.Shapes.Count} shapes, {bp.Texts.Count} texts");

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
    else Console.WriteLine($"OK - {log.Count} checks held. A copy carries every field "
                           + "but the Id, and CONCEPTS-REF 18.10's layer key is a "
                           + "measurement rather than a comment.");
}

// ---- reflection over the models -------------------------------------------
// The point of doing this by reflection: a field added to PenStroke,
// ShapeElement or TextElement and forgotten in the clone shows up HERE, without
// anybody remembering to extend a list in this file.

static PropertyInfo[] Fields(Type t, params string[] except)
    => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && !except.Contains(p.Name))
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

// Values compared as JSON, so a List<double> compares by its contents and a
// float? by its presence, without a per-type branch here.
static string Val(object? o) => JsonSerializer.Serialize(o);

/// <summary>Every field of <paramref name="a"/> equals the same field of
/// <paramref name="b"/>. Names the ones that do not.</summary>
static bool Same<T>(T a, T b, out string detail, params string[] except) where T : notnull
{
    var bad = new List<string>();
    foreach (var p in Fields(typeof(T), except))
    {
        string va = Val(p.GetValue(a)), vb = Val(p.GetValue(b));
        if (va != vb) bad.Add($"{p.Name} {va} -> {vb}");
    }
    detail = bad.Count == 0
        ? Fields(typeof(T), except).Length + " fields compared"
        : "DROPPED/CHANGED: " + string.Join("; ", bad);
    return bad.Count == 0;
}

/// <summary>The fields of <paramref name="o"/> still holding the value a freshly
/// constructed one has. Empty means the fixture actually exercises the class -
/// without which a clone that drops an untouched field would pass unnoticed,
/// which is exactly how the shipped lists went unnoticed.</summary>
static List<string> AtDefault<T>(T o, params string[] except) where T : notnull, new()
{
    var fresh = new T();
    return Fields(typeof(T), except)
        .Where(p => Val(p.GetValue(o)) == Val(p.GetValue(fresh)))
        .Select(p => p.Name)
        .ToList();
}

static bool NoDefaults<T>(T o, params string[] except) where T : notnull, new()
    => AtDefault(o, except).Count == 0;

static string DefaultsLeft<T>(T o, params string[] except) where T : notnull, new()
{
    var same = AtDefault(o, except);
    return same.Count == 0
        ? $"all {Fields(typeof(T), except).Length} fields differ from a fresh one"
        : "STILL AT DEFAULT: " + string.Join(", ", same);
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
