// Run 25 / 49.8 - the three layer actions NO harness runs.
//
// 16a557a's claim is that add, rename and reorder are now undoable operations
// on the page's undo stack. LayerRoundTrip's 83 checks never construct one of
// them: it exercises RemoveLayerAction and reorders by calling PageLayers.Move
// directly. So this runs the ACTIONS, through the REAL UndoRedoManager, over
// the REAL serialiser, and looks at the BYTES.
//
// Isolation is LayerRoundTrip's, unchanged, including the hard abort.

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

string scratch = Path.Combine(Path.GetTempPath(), "quill-vp25-layerops", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("QUILL_DATA_FOLDER", scratch);
File.WriteAllText(Path.Combine(scratch, "settings.json"),
                  "{\"DataFolder\":null,\"ImportedLegacy\":true}");

string file = LibraryStore.FilePath;
if (!file.StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("ABORT: LibraryStore resolved to " + file + ", outside " + scratch);
    return 2;
}
Check("isolated in a temp folder", true, file);

// A page in the shape a build that predates layers wrote: no Layers, no
// LayerKey, no ActiveLayer.
const string NbId = "0000aaaa-0000-0000-0000-00000000aaaa";
const string SecId = "0000bbbb-0000-0000-0000-00000000bbbb";
const string PgId = "0000cccc-0000-0000-0000-00000000cccc";
const string St0 = "0000d000-0000-0000-0000-00000000d000";
const string St1 = "0000d001-0000-0000-0000-00000000d001";
const string Sh0 = "0000e000-0000-0000-0000-00000000e000";
const string Tx0 = "0000f000-0000-0000-0000-00000000f000";

string oldJson =
    "{\"Notebooks\":[{\"Id\":\"" + NbId + "\",\"Name\":\"VP25\"," +
    "\"Sections\":[{\"Id\":\"" + SecId + "\",\"Name\":\"S\",\"Pages\":[{" +
      "\"Id\":\"" + PgId + "\",\"Name\":\"Page one\",\"Background\":\"#FCFCFC\"," +
      "\"Strokes\":[" +
        "{\"Id\":\"" + St0 + "\",\"Pen\":0,\"Color\":\"#1B5FC1\",\"Size\":40,\"Opacity\":0.62," +
          "\"Points\":[{\"X\":200,\"Y\":400,\"Pressure\":0.85},{\"X\":700,\"Y\":400,\"Pressure\":0.85}]}," +
        "{\"Id\":\"" + St1 + "\",\"Pen\":0,\"Color\":\"#E07A1F\",\"Size\":40," +
          "\"Points\":[{\"X\":500,\"Y\":400,\"Pressure\":0.85},{\"X\":1000,\"Y\":400,\"Pressure\":0.85}]}]," +
      "\"Shapes\":[{\"Id\":\"" + Sh0 + "\",\"Kind\":0,\"X\":200,\"Y\":700,\"W\":700,\"H\":60," +
        "\"Color\":\"#2E8B4A\",\"Opacity\":0.46}]," +
      "\"Texts\":[{\"Id\":\"" + Tx0 + "\",\"X\":200,\"Y\":790,\"Width\":520,\"Rtf\":" +
        JsonSerializer.Serialize(@"{\rtf1\ansi hello\par}") + "}]," +
      "\"Comments\":[]}]}]}]}";

File.WriteAllText(file, oldJson);
Check("the file under test carries none of the new keys - genuinely older than layers",
      !oldJson.Contains("Layer"), oldJson.Length + " bytes");

var lib = LibraryStore.Load();
Check("a library written before layers loads", !LibraryStore.LoadFailed,
      LibraryStore.LoadError ?? "no error");
// The write gate. LibraryStore.Save is a NO-OP until a window has adopted a
// real library, and it fails silently - this harness's first run "passed"
// nothing because every Save returned at the first line.
LibraryStore.EnableSaving();
Check("saving is enabled, or every Save below would silently do nothing",
      LibraryStore.SavingEnabled);
var page = lib.Notebooks[0].Sections[0].Pages[0];
Check("the page starts with NO layers array, the way every real page does",
      page.Layers == null && PageLayers.IsImplicit(page));
long bytesBefore = new FileInfo(file).Length;

var undo = new UndoRedoManager();

// ===========================================================================
// A. AddLayerAction  -  CHECK 2
// ===========================================================================
var add = new AddLayerAction();
undo.Push(add, page);
int newKey = add.Key;

Check("A1 add materialises the list and makes a SECOND layer",
      page.Layers is { Count: 2 } && page.Layers![0].Key == PageLayers.BaseKey && newKey != 0,
      $"keys [{string.Join(",", page.Layers!.Select(l => l.Key))}], new={newKey}");
Check("A2 the new layer goes ON TOP (last = top, InOrder paints bottom first)",
      page.Layers![^1].Key == newKey);
Check("A3 new ink lands on what you just made: ActiveLayer followed the add",
      page.ActiveLayer == newKey && PageLayers.Active(page).Key == newKey);
Check("A4 the existing content did NOT move - every element still on base",
      page.Strokes.All(s => s.LayerKey == 0) && page.Shapes.All(s => s.LayerKey == 0) &&
      page.Texts.All(t => t.LayerKey == 0));

// 18.9 seam 5 as the app does it: new content takes ActiveLayerKey.
var freshInk = new PenStroke { Id = Guid.NewGuid(), Color = "#7B3FA0", LayerKey = PageLayers.Active(page).Key };
page.Strokes.Add(freshInk);
Check("A5 seam 5: content stamped from the active layer lands on the NEW layer",
      freshInk.LayerKey == newKey);

LibraryStore.Save(lib); LibraryStore.Flush();
string json = File.ReadAllText(file);
Check("A5b the save actually reached the disk and the page now carries a Layers array",
      json.Contains("\"Layers\""), json.Length + " bytes");
using (var doc = JsonDocument.Parse(json))
{
    var layersEl = doc.RootElement.GetProperty("Notebooks")[0].GetProperty("Sections")[0]
                      .GetProperty("Pages")[0].GetProperty("Layers");
    var top = layersEl[1];
    Check("A6 CHECK 2: a new layer does NOT write \"Hidden\": false - false is the "
          + "zero value and costs nothing",
          !top.TryGetProperty("Hidden", out _),
          "properties: " + string.Join(",", top.EnumerateObject().Select(p => p.Name)));
    Check("A7 nor \"Locked\": false, for the same reason",
          !top.TryGetProperty("Locked", out _));
    Check("A8 Name is written, and it is the EMPTY STRING, not a derived \"Layer 2\"",
          top.TryGetProperty("Name", out var n) && n.GetString() == "",
          "Name=" + (top.TryGetProperty("Name", out var n2) ? "\"" + n2.GetString() + "\"" : "<absent>"));
}
Check("A9 and the page really did start costing bytes it did not cost before",
      new FileInfo(file).Length > bytesBefore,
      $"{bytesBefore} -> {new FileInfo(file).Length}");

// Undo the add. 18.4: an untouched page must go back to costing nothing.
page.Strokes.Remove(freshInk);          // the ink the user would also have undone
undo.Undo(page);
Check("A10 undoing the first add takes the Layers ARRAY away again, not just the row",
      page.Layers == null && PageLayers.IsImplicit(page));
Check("A11 and puts ActiveLayer back", page.ActiveLayer == 0);

undo.Redo(page);
Check("A12 redo restores the SAME KEY, so anything stamped with it is not orphaned",
      page.Layers is { Count: 2 } && page.Layers![^1].Key == newKey,
      $"key back = {page.Layers![^1].Key}, was {newKey}");

// ===========================================================================
// B. RenameLayerAction  -  CHECK 4
// ===========================================================================
undo.Push(new RenameLayerAction(newKey, "Sketch"), page);
Check("B1 a name persists in the model",
      page.Layers!.Single(l => l.Key == newKey).Name == "Sketch");

LibraryStore.Save(lib); LibraryStore.Flush();
var afterName = LibraryStore.Load().Notebooks[0].Sections[0].Pages[0];
Check("B2 and survives save -> reload",
      afterName.Layers!.Single(l => l.Key == newKey).Name == "Sketch");

Check("B3 DisplayName shows the chosen name while there is one",
      PageLayers.DisplayName(page, page.Layers!.Single(l => l.Key == newKey)) == "Sketch");

// CLEAR IT. 18: persisting the derived label "would make a derived label look
// like a decision".
undo.Push(new RenameLayerAction(newKey, ""), page);
Check("B4 CHECK 4: clearing the box writes \"\" in the model",
      page.Layers!.Single(l => l.Key == newKey).Name == "");
Check("B5 and the DERIVED label comes back on screen, a different thing",
      PageLayers.DisplayName(page, page.Layers!.Single(l => l.Key == newKey)) == "Layer 2");

LibraryStore.Save(lib); LibraryStore.Flush();
using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
{
    var top = doc.RootElement.GetProperty("Notebooks")[0].GetProperty("Sections")[0]
                 .GetProperty("Pages")[0].GetProperty("Layers")[1];
    string? onDisk = top.TryGetProperty("Name", out var n) ? n.GetString() : "<absent>";
    Check("B6 CHECK 4 ON DISK: the file carries \"\" and NOT \"Layer 2\"",
          onDisk == "" || onDisk == "<absent>", "Name=\"" + onDisk + "\"");
}

undo.Undo(page);
Check("B7 undoing the clear puts the chosen name back",
      page.Layers!.Single(l => l.Key == newKey).Name == "Sketch");

// Whitespace is the same case as empty.
undo.Push(new RenameLayerAction(newKey, "   "), page);
Check("B8 a name of three spaces is not a name - it normalises to \"\"",
      page.Layers!.Single(l => l.Key == newKey).Name == "");
Check("B9 and the action says so in its own description",
      new RenameLayerAction(newKey, "  ").Description == "Clear layer name");

// ===========================================================================
// C. MoveLayerAction  -  CHECK 1's FILE HALF
// ===========================================================================
// Put content on the top layer first, so a reorder has something to repoint if
// it were going to.
page.Strokes[1].LayerKey = newKey;
page.Shapes[0].LayerKey = newKey;
page.Texts[0].LayerKey = newKey;
LibraryStore.Save(lib); LibraryStore.Flush();

string CensusOf(NotePage p) => string.Join("|",
    p.Strokes.Select(s => s.Id + ":" + s.LayerKey)
     .Concat(p.Shapes.Select(s => s.Id + ":" + s.LayerKey))
     .Concat(p.Texts.Select(t => t.Id + ":" + t.LayerKey)));

string censusBefore = CensusOf(page);
var orderBefore = page.Layers!.Select(l => l.Key).ToList();

undo.Push(new MoveLayerAction(newKey, 0), page);
var orderAfter = page.Layers!.Select(l => l.Key).ToList();

Check("C1 the layer moved in the list",
      !orderAfter.SequenceEqual(orderBefore) && orderAfter[0] == newKey,
      $"[{string.Join(",", orderBefore)}] -> [{string.Join(",", orderAfter)}]");
Check("C2 CHECK 1: NOT ONE element's LayerKey moved - strokes, shapes AND texts",
      CensusOf(page) == censusBefore);
Check("C3 paint order followed the list: InOrder now yields the moved layer first",
      PageLayers.InOrder(page).First().Layer.Key == newKey);

LibraryStore.Save(lib); LibraryStore.Flush();
var afterMove = LibraryStore.Load().Notebooks[0].Sections[0].Pages[0];
Check("C4 and the new order survives save -> reload",
      afterMove.Layers!.Select(l => l.Key).SequenceEqual(orderAfter));
Check("C5 with every LayerKey still where it was, read back off the disk",
      CensusOf(afterMove) == censusBefore);

undo.Undo(page);
Check("C6 undo puts the stack back",
      page.Layers!.Select(l => l.Key).SequenceEqual(orderBefore));
Check("C7 and STILL no element's key moved",
      CensusOf(page) == censusBefore);

// A move that cannot move must not cost a press of Ctrl+Z.
int depthBefore = 0;
var noop = new MoveLayerAction(newKey, 99);        // clamps to where it already is
noop.Do(page);
noop.Undo(page);
Check("C8 a clamped no-op move undoes to the same order rather than shuffling",
      page.Layers!.Select(l => l.Key).SequenceEqual(orderBefore),
      string.Join(",", page.Layers!.Select(l => l.Key)));
_ = depthBefore;

// ===========================================================================
// D. RemoveLayerAction on a NON-EMPTY layer  -  CHECK 3
// ===========================================================================
var doomed = page.Layers!.Single(l => l.Key == newKey);
_ = doomed;
int strokesBefore = page.Strokes.Count, shapesBefore = page.Shapes.Count, textsBefore = page.Texts.Count;
int onDoomed = page.Strokes.Count(s => s.LayerKey == newKey) +
               page.Shapes.Count(s => s.LayerKey == newKey) +
               page.Texts.Count(t => t.LayerKey == newKey);
Check("D0 the layer about to be deleted is NOT empty", onDoomed == 3, onDoomed + " objects");

var rm = new RemoveLayerAction(newKey);            // DEFAULT mode, deliberately
Check("D1 CHECK 3: the DEFAULT mode is DeleteContent - deleting a layer deletes "
      + "the drawing on it. 18.7 and 18.12 item 3 still say reassign-to-base.",
      default(LayerRemoval) == LayerRemoval.DeleteContent &&
      rm.Description == "Delete layer",
      "Description=\"" + rm.Description + "\"");

undo.Push(rm, page);
Check("D2 and it really does take the content",
      page.Strokes.Count == strokesBefore - 1 && page.Shapes.Count == shapesBefore - 1 &&
      page.Texts.Count == textsBefore - 1 && page.Layers!.Count == 1);

LibraryStore.Save(lib); LibraryStore.Flush();

undo.Undo(page);
Check("D3 CHECK 3: undo brings the LAYER back",
      page.Layers!.Count == 2 && page.Layers!.Any(l => l.Key == newKey));
Check("D4 CHECK 3: and its CONTENTS - not an empty shell",
      page.Strokes.Count == strokesBefore && page.Shapes.Count == shapesBefore &&
      page.Texts.Count == textsBefore);
Check("D5 CHECK 3: every restored element still names the layer it belonged to",
      CensusOf(page) == censusBefore, "census matches the pre-delete census");

LibraryStore.Save(lib); LibraryStore.Flush();
var afterUndo = LibraryStore.Load().Notebooks[0].Sections[0].Pages[0];
Check("D6 CHECK 3: and the restoration survives save -> reload",
      CensusOf(afterUndo) == censusBefore && afterUndo.Layers!.Count == 2);

// ===========================================================================
// E. 49.3's promise across all four operations
// ===========================================================================
var ops = LibraryStore.Load().Notebooks[0].Sections[0].Pages[0];
Check("E1 49.3: through add, rename, reorder, delete and undo, every element's "
      + "OWN opacity is untouched",
      ops.Strokes.Single(s => s.Id == Guid.Parse(St0)).Opacity == 0.62f &&
      ops.Shapes.Single(s => s.Id == Guid.Parse(Sh0)).Opacity == 0.46f,
      $"stroke={ops.Strokes.Single(s => s.Id == Guid.Parse(St0)).Opacity}, " +
      $"shape={ops.Shapes.Single(s => s.Id == Guid.Parse(Sh0)).Opacity}");

foreach (var l in log) Console.WriteLine(l);
Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"OK - {log.Count} checks held."
    : $"{failures} of {log.Count} checks FAILED.");
return failures == 0 ? 0 : 1;
