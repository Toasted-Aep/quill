// CONCEPTS-REF 18, PROVED.
//
//   "Every existing page has content with no layer. It must land somewhere
//    sensible on load, and a page saved by the new build must not become
//    unreadable to anything that reads it."
//
// A claim about persistence is settled by persisting, so this builds a library
// in the shape a build that predates layers wrote, loads it through the REAL
// store, gives it layers, saves, reloads and diffs - against the real things:
//
//   * the real models     (src/Quill/Models/NoteModels.cs, compiled in)
//   * the real layers     (src/Quill/Models/LayerModels.cs, compiled in)
//   * the real serialiser (src/Quill/Services/LibraryStore.cs, compiled in)
//   * the real op log     (src/Quill/Services/SyncLog.cs, which Save calls)
//
// Written to the pattern tools/VeilRoundTrip set for section 16.7, because the
// two failures this model has to refuse are both invisible until after they
// have happened: content that is intact but on the wrong layer, and content
// that is intact but cannot be seen.
//
// ISOLATION. LibraryStore reads QUILL_DATA_FOLDER before anything else and an
// isolated instance touches nothing outside it (LibraryStore.EnvFolder's own
// remarks). It is set before the first call here, a settings.json is seeded
// inside it so the loader never reaches for the pre-rename anchor, and the run
// ABORTS rather than continues if the resolved path is not inside the temp
// folder this process made.
//
// ONE THING USED TO LIVE OUTSIDE IT. SyncLog keeps its per-device sync cursors
// in %LOCALAPPDATA%\Quill\synccursors.json - NOT in the data folder, on purpose,
// because they are per-machine read offsets that must never sync - and
// LibraryStore.Save calls SyncLog.OnSaved unconditionally. A harness that saves
// therefore rewrote the real user's cursor file from an empty in-memory one,
// resetting the read offset for every peer and, per the roadmap's own "SyncLog
// replay" risk, potentially resurrecting erased strokes on the next launch.
// tools/VeilRoundTrip had been doing it on every run.
//
// That is now fixed IN SyncLog rather than here: its state directory follows
// QUILL_DATA_FOLDER, which is this app's one signal for "an isolated instance"
// (LibraryStore.IsIsolated). An isolated folder is a temp folder nothing syncs,
// so the reason those files live outside the data folder does not apply there.
// The snapshot below therefore has nothing to put back - and it stays anyway,
// checked BEFORE the restore, so that if the leak ever reopens this says so
// instead of quietly papering over it.

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
string scratch = Path.Combine(Path.GetTempPath(), "quill-layer-roundtrip", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("QUILL_DATA_FOLDER", scratch);

// Seed the anchor's own settings file INSIDE the scratch folder. Without it the
// settings getter finds nothing here, reaches for the pre-rename
// Documents\LectureInk\settings.json, and Load goes on to merge that lineage's
// notebooks into the library under test - which would make the counts below
// depend on what happens to be on the machine.
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

// The two files SyncLog keeps outside the data folder, snapshotted.
string localQuill = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quill");
string cursorPath = Path.Combine(localQuill, "synccursors.json");
string devicePath = Path.Combine(localQuill, "deviceid.txt");
byte[]? cursorsBefore = File.Exists(cursorPath) ? File.ReadAllBytes(cursorPath) : null;
byte[]? deviceBefore = File.Exists(devicePath) ? File.ReadAllBytes(devicePath) : null;

try
{

// ---------------------------------------------------------------------------
// 1. A LIBRARY IN THE SHAPE A BUILD THAT PREDATES LAYERS WROTE.
// ---------------------------------------------------------------------------
// Hand-written rather than round-tripped out of today's model, because the whole
// question is what happens to JSON that carries none of the new keys. Nothing
// here says "Layers", "LayerKey" or "ActiveLayer" - it cannot, it is older than
// all three.
const string NbId = "0000aaaa-0000-0000-0000-00000000aaaa";
const string SecId = "0000bbbb-0000-0000-0000-00000000bbbb";
const string PgId = "0000cccc-0000-0000-0000-00000000cccc";
const string St0 = "0000d000-0000-0000-0000-00000000d000";
const string St1 = "0000d001-0000-0000-0000-00000000d001";
const string St2 = "0000d002-0000-0000-0000-00000000d002";
const string Sh0 = "0000e000-0000-0000-0000-00000000e000";
const string Sh1 = "0000e001-0000-0000-0000-00000000e001";
const string Tx0 = "0000f000-0000-0000-0000-00000000f000";

const string OldRtf = @"{\rtf1\ansi{\colortbl ;\red20\green20\blue19;}\cf1 a note [with] brackets\par}";

string oldJson =
    "{\"Notebooks\":[{\"Id\":\"" + NbId + "\",\"Name\":\"Before layers\",\"Color\":\"#D97757\"," +
    "\"Sections\":[{\"Id\":\"" + SecId + "\",\"Name\":\"Section\",\"Pages\":[{" +
      "\"Id\":\"" + PgId + "\",\"Name\":\"Page one\",\"Background\":\"#FFFDF7\"," +
      "\"Strokes\":[" +
        "{\"Id\":\"" + St0 + "\",\"Pen\":0,\"Color\":\"#2B6CB0\",\"Size\":3,\"Opacity\":0.4," +
          "\"Points\":[{\"X\":10,\"Y\":20,\"Pressure\":0.5},{\"X\":60,\"Y\":80,\"Pressure\":0.7}]}," +
        "{\"Id\":\"" + St1 + "\",\"Pen\":1,\"Color\":\"#D97757\",\"Size\":5," +
          "\"Points\":[{\"X\":11,\"Y\":21,\"Pressure\":0.5}]}," +
        "{\"Id\":\"" + St2 + "\",\"Pen\":4,\"Color\":\"#00A37A\",\"Size\":2," +
          "\"Points\":[{\"X\":12,\"Y\":22,\"Pressure\":0.5}]}]," +
      "\"Shapes\":[" +
        "{\"Id\":\"" + Sh0 + "\",\"Kind\":1,\"X\":10,\"Y\":10,\"W\":90,\"H\":60,\"Color\":\"#B4530A\"}," +
        "{\"Id\":\"" + Sh1 + "\",\"Kind\":6,\"X\":120,\"Y\":140,\"W\":320,\"H\":240," +
          "\"Color\":\"#3A3A3A\",\"ImagePath\":\"assets\\\\photo.png\"}]," +
      "\"Texts\":[{\"Id\":\"" + Tx0 + "\",\"X\":40,\"Y\":400,\"Width\":300,\"Rtf\":" +
        JsonSerializer.Serialize(OldRtf) + "}]," +
      "\"Comments\":[]}]}]}]}";

File.WriteAllText(file, oldJson);
Check("the file under test carries none of the new keys - it is genuinely older "
      + "than layers",
      !oldJson.Contains("Layer"), oldJson.Length + " bytes");

var lib = LibraryStore.Load();
Check("a library written before layers loads", !LibraryStore.LoadFailed,
      LibraryStore.LoadError ?? "no error");

var page = lib.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("the page came back", page != null);
if (page == null) { Report(); return 1; }

Check("with exactly the content it had",
      lib.Notebooks.Count == 1 && page.Strokes.Count == 3 && page.Shapes.Count == 2 &&
      page.Texts.Count == 1,
      $"{lib.Notebooks.Count} notebook(s), {page.Strokes.Count} strokes, " +
      $"{page.Shapes.Count} shapes, {page.Texts.Count} texts");

// ---------------------------------------------------------------------------
// 2. MIGRATION: nothing is migrated, and everything lands somewhere sensible.
// ---------------------------------------------------------------------------
Check("18.4 - the page carries no stored layer list at all, so nothing was "
      + "rewritten on load", PageLayers.IsImplicit(page) && page.Layers == null);

var implicitLayers = PageLayers.All(page);
Check("18.4 - it nevertheless HAS a layer: one, base-keyed, visible, unlocked, "
      + "fully opaque",
      implicitLayers.Count == 1 && implicitLayers[0].Key == PageLayers.BaseKey &&
      !implicitLayers[0].Hidden && !implicitLayers[0].Locked &&
      Math.Abs(implicitLayers[0].Opacity - 1f) < 1e-6f,
      $"{implicitLayers.Count} layer, key {implicitLayers[0].Key}");

bool allBase = page.Strokes.All(s => s.LayerKey == PageLayers.BaseKey) &&
               page.Shapes.All(s => s.LayerKey == PageLayers.BaseKey) &&
               page.Texts.All(t => t.LayerKey == PageLayers.BaseKey);
Check("18.4 - every element on it is on that layer", allBase);

bool allVisible = page.Strokes.All(s => PageLayers.IsVisible(page, s.LayerKey)) &&
                  page.Shapes.All(s => PageLayers.IsVisible(page, s.LayerKey)) &&
                  page.Texts.All(t => PageLayers.IsVisible(page, t.LayerKey));
Check("18.7 - and every element on it is VISIBLE", allVisible);

Check("the implicit base layer names itself from its position",
      PageLayers.DisplayName(page, implicitLayers[0]) == "Layer 1",
      PageLayers.DisplayName(page, implicitLayers[0]));

// ---------------------------------------------------------------------------
// 3. 18.5 - InOrder on a page with no layers IS today's draw order.
// ---------------------------------------------------------------------------
var buckets = PageLayers.InOrder(page).ToList();
bool oneBucket = buckets.Count == 1;
bool sameOrder = oneBucket &&
    buckets[0].Shapes.SequenceEqual(page.Shapes) &&
    buckets[0].Strokes.SequenceEqual(page.Strokes) &&
    buckets[0].Texts.SequenceEqual(page.Texts);
Check("18.5 - a page with no layers yields exactly ONE bucket holding every "
      + "element, by reference, in the order the draw loop walks today",
      sameOrder, oneBucket ? $"{buckets[0].Count} elements" : $"{buckets.Count} buckets");

// ---------------------------------------------------------------------------
// 4. A SAVE BY THE NEW BUILD ADDS NOTHING.
// ---------------------------------------------------------------------------
LibraryStore.EnableSaving();
LibraryStore.Save(lib);
LibraryStore.Flush();
string saved = File.ReadAllText(file);

Check("18.4 - a page written before layers, saved by a build that HAS layers, "
      + "still carries no \"Layers\" key",
      !saved.Contains("\"Layers\"", StringComparison.Ordinal));
Check("18.3 - and no \"LayerKey\" on any of its six elements",
      !saved.Contains("\"LayerKey\"", StringComparison.Ordinal));
Check("18.4 - and no \"ActiveLayer\"",
      !saved.Contains("\"ActiveLayer\"", StringComparison.Ordinal));
Check("...while still carrying everything it came in with",
      new[] { "#2B6CB0", "#D97757", "#00A37A", "#B4530A", "#3A3A3A", "photo.png" }
          .All(h => saved.Contains(h, StringComparison.OrdinalIgnoreCase)) &&
      saved.Contains("brackets", StringComparison.Ordinal));

LibraryStore.Save(lib);
LibraryStore.Flush();
Check("saving the same unchanged library twice is byte-identical",
      File.ReadAllText(file) == saved);

// ---------------------------------------------------------------------------
// 5. GIVING THAT PAGE LAYERS.
// ---------------------------------------------------------------------------
var materialised = PageLayers.Materialise(page);
Check("18.4 - materialising is what makes a page start carrying a list, and it "
      + "starts as the one layer the page already had",
      !PageLayers.IsImplicit(page) && materialised.Count == 1 &&
      materialised[0].Key == PageLayers.BaseKey);
Check("materialising twice changes nothing",
      ReferenceEquals(PageLayers.Materialise(page), materialised) && materialised.Count == 1);

var ink = PageLayers.Add(page, "Ink");
var guides = PageLayers.Add(page, "Guides");
Check("18.2 - added layers get fresh keys and stack on top",
      ink.Key == 1 && guides.Key == 2 &&
      page.Layers!.Select(l => l.Key).SequenceEqual(new[] { 0, 1, 2 }),
      string.Join(",", page.Layers!.Select(l => $"{l.Key}:{PageLayers.DisplayName(page, l)}")));

page.Strokes[1].LayerKey = ink.Key;
page.Strokes[2].LayerKey = ink.Key;
page.Shapes[1].LayerKey = guides.Key;
PageLayers.SetActive(page, ink.Key);

Check("18.5 - three layers now, and the buckets hold what was put in them",
      PageLayers.InOrder(page).Select(b => b.Count).SequenceEqual(new[] { 3, 2, 1 }),
      string.Join(",", PageLayers.InOrder(page).Select(b => $"{PageLayers.DisplayName(page, b.Layer)}={b.Count}")));

// ---------------------------------------------------------------------------
// 6. ROUND TRIP WITH LAYERS.
// ---------------------------------------------------------------------------
guides.Hidden = true;
guides.Opacity = 0.3f;
guides.Locked = true;

LibraryStore.Save(lib);
LibraryStore.Flush();
var reloaded = LibraryStore.Load();
Check("the layered library reloads", !LibraryStore.LoadFailed, LibraryStore.LoadError ?? "no error");

var back = reloaded.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("the layered page came back", back != null);
if (back == null) { Report(); return 1; }

var bl = back.Layers;
Check("18.2 - every layer field survives save -> reload",
      bl != null && bl.Count == 3 &&
      bl[0].Key == 0 && bl[0].Name == "" && !bl[0].Hidden && !bl[0].Locked && bl[0].Opacity == 1f &&
      bl[1].Key == 1 && bl[1].Name == "Ink" && !bl[1].Hidden && !bl[1].Locked && bl[1].Opacity == 1f &&
      bl[2].Key == 2 && bl[2].Name == "Guides" && bl[2].Hidden && bl[2].Locked && bl[2].Opacity == 0.3f,
      bl == null ? "no list" : string.Join(" | ", bl.Select(l =>
          $"{l.Key}/{l.Name}/hid={l.Hidden}/lock={l.Locked}/op={l.Opacity}")));

Check("18.2 - and so does the active layer", back.ActiveLayer == ink.Key, back.ActiveLayer.ToString());

Check("18.3 - every element came back on the layer it was put on",
      back.Strokes.Single(s => s.Id == Guid.Parse(St0)).LayerKey == 0 &&
      back.Strokes.Single(s => s.Id == Guid.Parse(St1)).LayerKey == 1 &&
      back.Strokes.Single(s => s.Id == Guid.Parse(St2)).LayerKey == 1 &&
      back.Shapes.Single(s => s.Id == Guid.Parse(Sh0)).LayerKey == 0 &&
      back.Shapes.Single(s => s.Id == Guid.Parse(Sh1)).LayerKey == 2 &&
      back.Texts.Single(t => t.Id == Guid.Parse(Tx0)).LayerKey == 0);

Check("nothing was lost in the process",
      back.Strokes.Count == 3 && back.Shapes.Count == 2 && back.Texts.Count == 1 &&
      back.Strokes.Single(s => s.Id == Guid.Parse(St0)).Points.Count == 2);

// ---------------------------------------------------------------------------
// 7. 18.8 - VISIBILITY AND OPACITY ARE RENDER-TIME. The 16.7 promise, again.
// ---------------------------------------------------------------------------
// Reset the two flags, save, then set them again and save: the ONLY difference
// between the two files must be inside the layers array itself.
var g = back.Layers!.Single(l => l.Key == 2);
g.Hidden = false; g.Opacity = 1f; g.Locked = false;
LibraryStore.Save(reloaded); LibraryStore.Flush();
string plain = File.ReadAllText(file);
var censusBefore = ElementCensus(back);

g.Hidden = true; g.Opacity = 0.3f;
LibraryStore.Save(reloaded); LibraryStore.Flush();
string dimmed = File.ReadAllText(file);
var censusAfter = ElementCensus(back);

Check("18.8 - hiding a layer and dropping another to 30% does change the file",
      plain != dimmed);
Check("18.8 - but the ONLY thing that changed is inside the layers array: strip "
      + "it from both and the two files are byte-identical",
      StripLayerArrays(plain) == StripLayerArrays(dimmed),
      $"{StripLayerArrays(plain).Length} vs {StripLayerArrays(dimmed).Length} bytes");
Check("18.8 - not one element's serialised bytes moved",
      censusBefore.Count == censusAfter.Count &&
      censusBefore.All(kv => censusAfter.TryGetValue(kv.Key, out var v) && v == kv.Value),
      $"{censusBefore.Count} elements compared");
Check("18.8 - the stroke that was already translucent still stores its OWN "
      + "opacity, not the layer's",
      back.Strokes.Single(s => s.Id == Guid.Parse(St0)).Opacity == 0.4f);
Check("18.8 - the layer answers 0 while hidden, and its opacity when not",
      PageLayers.EffectiveOpacity(g) == 0f &&
      PageLayers.EffectiveOpacity(new Layer { Opacity = 0.3f }) == 0.3f);

// ---------------------------------------------------------------------------
// 8. 18.9 seam 3 - selection scoping.
// ---------------------------------------------------------------------------
Check("a hidden or locked layer is not editable; the base layer is",
      !PageLayers.IsEditable(back, 2) && PageLayers.IsEditable(back, 0) &&
      PageLayers.IsEditable(back, 1));
Check("\"is this in the active layer?\" answers per element",
      PageLayers.InActive(back, back.Strokes.Single(s => s.Id == Guid.Parse(St1)).LayerKey) &&
      !PageLayers.InActive(back, back.Strokes.Single(s => s.Id == Guid.Parse(St0)).LayerKey));

// ---------------------------------------------------------------------------
// 8b. 18.1 - THE SCOPE SEAM, which the bottom mode bar is being built against.
// ---------------------------------------------------------------------------
Check("18.1 - AllLayers is the ZERO value, so an unset scope means today's "
      + "behaviour", default(LayerScope) == LayerScope.AllLayers);

var onBase = back.Strokes.Single(s => s.Id == Guid.Parse(St0));   // base layer
var onInkLayer = back.Strokes.Single(s => s.Id == Guid.Parse(St1)); // active layer
Check("18.1 - under All layers everything is in scope",
      PageLayers.InScope(back, onBase.LayerKey, LayerScope.AllLayers) &&
      PageLayers.InScope(back, onInkLayer.LayerKey, LayerScope.AllLayers));
Check("18.1 - under Active layer only the active layer's content is",
      !PageLayers.InScope(back, onBase.LayerKey, LayerScope.ActiveLayer) &&
      PageLayers.InScope(back, onInkLayer.LayerKey, LayerScope.ActiveLayer));
Check("18.1 - CanSelect is scope AND not hidden AND not locked",
      PageLayers.CanSelect(back, onInkLayer.LayerKey, LayerScope.ActiveLayer) &&
      !PageLayers.CanSelect(back, onBase.LayerKey, LayerScope.ActiveLayer) &&
      PageLayers.CanSelect(back, onBase.LayerKey, LayerScope.AllLayers) &&
      !PageLayers.CanSelect(back, 2, LayerScope.AllLayers));   // layer 2 is hidden+locked

// THE COMPATIBILITY GUARANTEE the mode bar's stub is entitled to rely on: with
// one implicit layer, the two scopes are INDISTINGUISHABLE. A stub that
// hard-codes one layer cannot diverge from a real list until a second layer
// exists.
var stubPage = new NotePage();
stubPage.Strokes.Add(new PenStroke { Color = "#111111" });
stubPage.Shapes.Add(new ShapeElement());
stubPage.Texts.Add(new TextElement());
bool stubSame =
    stubPage.Strokes.Concat<object>(stubPage.Shapes).Concat(stubPage.Texts).All(_ => true) &&
    stubPage.Strokes.All(s => PageLayers.CanSelect(stubPage, s.LayerKey, LayerScope.AllLayers) ==
                              PageLayers.CanSelect(stubPage, s.LayerKey, LayerScope.ActiveLayer)) &&
    stubPage.Shapes.All(s => PageLayers.CanSelect(stubPage, s.LayerKey, LayerScope.AllLayers) ==
                             PageLayers.CanSelect(stubPage, s.LayerKey, LayerScope.ActiveLayer)) &&
    stubPage.Texts.All(t => PageLayers.CanSelect(stubPage, t.LayerKey, LayerScope.AllLayers) ==
                            PageLayers.CanSelect(stubPage, t.LayerKey, LayerScope.ActiveLayer));
Check("18.1 - on a page with ONE implicit layer the two scopes are "
      + "indistinguishable, and everything is selectable under both",
      stubSame && PageLayers.CanSelect(stubPage, 0, LayerScope.ActiveLayer) &&
      PageLayers.IsImplicit(stubPage));

// ---------------------------------------------------------------------------
// 9. 18.9 seam 4 - the Objects library's rows.
// ---------------------------------------------------------------------------
var rows = PageLayers.Rows(back).ToList();
Check("every object on the page gets exactly one row",
      rows.Count == back.Strokes.Count + back.Shapes.Count + back.Texts.Count,
      $"{rows.Count} rows");
Check("rows come out TOP LAYER FIRST",
      rows[0].Layer.Key == 2 && rows[^1].Layer.Key == 0,
      string.Join(" ", rows.Select(r => $"{r.Layer.Key}:{r.Label}")));
Check("an image row is labelled with its file name, not \"Image\"",
      rows.Any(r => r.Kind == LayerObjectKind.Shape && r.Label == "photo.png"));

// ---------------------------------------------------------------------------
// 10. 18.7 - AN ORPHANED KEY STILL DRAWS, and is not rewritten.
// ---------------------------------------------------------------------------
back.Strokes.Single(s => s.Id == Guid.Parse(St2)).LayerKey = 999;
var orphan = back.Strokes.Single(s => s.Id == Guid.Parse(St2));
Check("18.7 - a key naming no layer resolves to the BASE layer",
      PageLayers.Of(back, 999).Key == PageLayers.BaseKey);
Check("18.7 - so the element is visible, not lost", PageLayers.IsVisible(back, 999));
Check("18.7 - and InOrder puts it in the base bucket rather than dropping it",
      PageLayers.InOrder(back).First().Strokes.Contains(orphan));
Check("18.7 - its key is NOT repaired, so a build that restores the missing "
      + "layer can reunite them",
      orphan.LayerKey == 999);

LibraryStore.Save(reloaded); LibraryStore.Flush();
var afterOrphan = LibraryStore.Load()
    .Notebooks[0].Sections[0].Pages[0].Strokes.Single(s => s.Id == Guid.Parse(St2));
Check("18.7 - and it survives a round trip still pointing at nothing",
      afterOrphan.LayerKey == 999);

Check("18.10 - NextKey steps over an orphaned key, so a new layer cannot adopt "
      + "somebody else's drawing",
      PageLayers.NextKey(back) == 1000, PageLayers.NextKey(back).ToString());
back.Strokes.Single(s => s.Id == Guid.Parse(St2)).LayerKey = 1;

// ---------------------------------------------------------------------------
// 11. THE RULING: DELETING A LAYER DELETES ITS DRAWING - AND IS UNDOABLE.
// ---------------------------------------------------------------------------
// 18.12 item 3. The model's original default reassigned to the base layer and
// could not destroy work; the user ruled for Photoshop's behaviour. That makes
// the undo load-bearing rather than a nicety, so it is proved through the REAL
// UndoRedoManager - Push/Undo/Redo - and not by calling the action's own
// methods.
Check("18.12 item 3 - DeleteContent is the DEFAULT, and it is the enum's zero "
      + "value so an unset mode cannot silently mean the other thing",
      default(LayerRemoval) == LayerRemoval.DeleteContent);

var undoStack = new UndoRedoManager();

// A census of the doomed layer's content, by identity and by layer key, taken
// while it is still on the page.
var doomedKey = 2;
var doomedShapes = back.Shapes.Where(s => s.LayerKey == doomedKey).ToList();
int shapesBefore = back.Shapes.Count;
int shapeIndexBefore = back.Shapes.FindIndex(s => s.Id == Guid.Parse(Sh1));
Check("the layer about to be deleted really has content on it",
      doomedShapes.Count == 1 && doomedShapes[0].Id == Guid.Parse(Sh1));

undoStack.Push(new RemoveLayerAction(doomedKey), back);
Check("18.12 item 3 - deleting a layer takes the layer",
      back.Layers!.Count == 2 && back.Layers!.All(l => l.Key != doomedKey));
Check("18.12 item 3 - and takes its DRAWING with it, which is the ruling",
      back.Shapes.Count == shapesBefore - 1 &&
      back.Shapes.All(s => s.Id != Guid.Parse(Sh1)));

undoStack.Undo(back);
Check("18.12 item 3 - UNDO brings the layer back, at its own position in the "
      + "stack",
      back.Layers!.Count == 3 && back.Layers![2].Key == doomedKey,
      string.Join(",", back.Layers!.Select(l => l.Key)));
Check("18.12 item 3 - and brings the CONTENT back, not an empty shell",
      back.Shapes.Count == shapesBefore &&
      back.Shapes.Any(s => s.Id == Guid.Parse(Sh1)));
Check("18.12 item 3 - every element returns with its LayerKey INTACT, so it is "
      + "on the layer it belonged to and not dropped on the base one",
      back.Shapes.Single(s => s.Id == Guid.Parse(Sh1)).LayerKey == doomedKey);
Check("18.12 item 3 - and at the index it held, so z-order survives the undo",
      back.Shapes.FindIndex(s => s.Id == Guid.Parse(Sh1)) == shapeIndexBefore,
      $"index {back.Shapes.FindIndex(s => s.Id == Guid.Parse(Sh1))}, was {shapeIndexBefore}");
Check("18.12 item 3 - the restored element is the SAME OBJECT, so anything "
      + "holding a reference to it (a selection, the spatial grid) is not stale",
      ReferenceEquals(back.Shapes.Single(s => s.Id == Guid.Parse(Sh1)), doomedShapes[0]));

undoStack.Redo(back);
Check("18.12 item 3 - redo deletes it again",
      back.Layers!.Count == 2 && back.Shapes.Count == shapesBefore - 1);
undoStack.Undo(back);
Check("18.12 item 3 - and a second undo restores it again, so the action is "
      + "re-runnable rather than single-use",
      back.Layers!.Count == 3 && back.Shapes.Count == shapesBefore &&
      back.Shapes.Single(s => s.Id == Guid.Parse(Sh1)).LayerKey == doomedKey);

// The undo has to survive the disk, not just the session.
LibraryStore.Save(reloaded); LibraryStore.Flush();
var afterUndo = LibraryStore.Load().Notebooks[0].Sections[0].Pages[0];
Check("18.12 item 3 - and the restored content survives save -> reload still on "
      + "its own layer",
      afterUndo.Shapes.Any(s => s.Id == Guid.Parse(Sh1)) &&
      afterUndo.Shapes.Single(s => s.Id == Guid.Parse(Sh1)).LayerKey == doomedKey &&
      afterUndo.Layers!.Count == 3);

Check("18.7 - the base layer still cannot be removed",
      !PageLayers.Remove(back, PageLayers.BaseKey) && back.Layers!.Count == 3);

// A refused removal must not leave the action thinking it did something.
var refused = new RemoveLayerAction(PageLayers.BaseKey);
undoStack.Push(refused, back);
int layersAfterRefusal = back.Layers!.Count, strokesAfterRefusal = back.Strokes.Count;
undoStack.Undo(back);
Check("18.7 - undoing a REFUSED deletion puts nothing back, because nothing was "
      + "taken",
      back.Layers!.Count == layersAfterRefusal && back.Strokes.Count == strokesAfterRefusal);

// ReassignToBase is kept for the routes that are getting rid of a LAYER rather
// than deleting a drawing.
var keeper = PageLayers.Add(back, "Merge me");
back.Strokes[0].LayerKey = keeper.Key;
int strokesBefore = back.Strokes.Count;
undoStack.Push(new RemoveLayerAction(keeper.Key, LayerRemoval.ReassignToBase), back);
Check("18.12 item 3 - ReassignToBase is still there for merging down: the layer "
      + "goes, the drawing stays and is visible",
      back.Strokes.Count == strokesBefore &&
      back.Strokes[0].LayerKey == PageLayers.BaseKey &&
      PageLayers.IsVisible(back, back.Strokes[0].LayerKey));
undoStack.Undo(back);
Check("18.12 item 3 - and undoing THAT puts the content back on its old layer",
      back.Strokes[0].LayerKey == keeper.Key &&
      back.Layers!.Any(l => l.Key == keeper.Key));
undoStack.Push(new RemoveLayerAction(keeper.Key, LayerRemoval.ReassignToBase), back);

// ---------------------------------------------------------------------------
// 12. REORDERING MOVES THE LIST AND REPOINTS NOTHING.
// ---------------------------------------------------------------------------
var keysBefore = back.Strokes.Select(s => s.LayerKey).ToList();
var orderBefore = back.Layers!.Select(l => l.Key).ToList();     // [0, 1, 2]
PageLayers.Move(back, 1, 0);
Check("18.2 - a moved layer changes the order",
      back.Layers!.Select(l => l.Key).SequenceEqual(new[] { 1, 0, 2 }),
      $"[{string.Join(",", orderBefore)}] -> [{string.Join(",", back.Layers!.Select(l => l.Key))}]");
Check("18.2 - and repoints no element, because order is the list and identity "
      + "is the key",
      back.Strokes.Select(s => s.LayerKey).SequenceEqual(keysBefore));
Check("18.5 - the paint order followed it",
      PageLayers.InOrder(back).First().Layer.Key == 1);

// ---------------------------------------------------------------------------
// 13. 18.10 - CloneWithPoints carries the key. This is the ERASER's path.
// ---------------------------------------------------------------------------
var onInk = new PenStroke { LayerKey = 7, Color = "#123456", Opacity = 0.5f };
var frag = onInk.CloneWithPoints(new List<StrokePoint> { new(1, 2, 0.5f) });
Check("18.10 - a fragment of an erased stroke stays on its stroke's layer",
      frag.LayerKey == 7, frag.LayerKey.ToString());

// ---------------------------------------------------------------------------
// 14. 18.3 - THE ColorPickerMode DISASTER, REPRODUCED ON PURPOSE.
// ---------------------------------------------------------------------------
// "a shipped build wrote "Copic" here, and a plain int property makes that file
// undeserializable - which cost the whole library." LayerKey is on hundreds of
// thousands of elements, so it carries the same tolerant converter. Three
// wrong shapes, one library that still loads.
string badJson =
    "{\"Notebooks\":[{\"Id\":\"" + NbId + "\",\"Name\":\"Hand edited\"," +
    "\"Sections\":[{\"Id\":\"" + SecId + "\",\"Name\":\"S\",\"Pages\":[{" +
      "\"Id\":\"" + PgId + "\",\"Name\":\"P\",\"ActiveLayer\":\"Ink\"," +
      "\"Layers\":[{\"Key\":0},{\"Key\":3,\"Name\":\"Three\"}]," +
      "\"Strokes\":[" +
        "{\"Id\":\"" + St0 + "\",\"Color\":\"#2B6CB0\",\"LayerKey\":\"Overlay\",\"Points\":[]}," +
        "{\"Id\":\"" + St1 + "\",\"Color\":\"#D97757\",\"LayerKey\":\"3\",\"Points\":[]}," +
        "{\"Id\":\"" + St2 + "\",\"Color\":\"#00A37A\",\"LayerKey\":null,\"Points\":[]}]," +
      "\"Shapes\":[],\"Texts\":[],\"Comments\":[]}]}]}]}";

File.WriteAllText(file, badJson);
var tolerated = LibraryStore.Load();
Check("18.3 - a LayerKey written as a NAME does not cost the user their library",
      !LibraryStore.LoadFailed && tolerated.Notebooks.Count == 1,
      LibraryStore.LoadError ?? "loaded");

var tp = tolerated.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("18.3 - the page and all three strokes came through",
      tp != null && tp.Strokes.Count == 3);
if (tp != null)
{
    Check("18.3 - an unreadable key reads as the base layer, which is VISIBLE",
          tp.Strokes.Single(s => s.Id == Guid.Parse(St0)).LayerKey == PageLayers.BaseKey &&
          PageLayers.IsVisible(tp, tp.Strokes.Single(s => s.Id == Guid.Parse(St0)).LayerKey));
    Check("18.3 - a NUMERIC string is still the number it says",
          tp.Strokes.Single(s => s.Id == Guid.Parse(St1)).LayerKey == 3);
    Check("18.3 - and an explicit null is the base layer too",
          tp.Strokes.Single(s => s.Id == Guid.Parse(St2)).LayerKey == PageLayers.BaseKey);
    Check("18.3 - an unreadable ActiveLayer leaves the page drawing on the base "
          + "layer rather than nowhere",
          PageLayers.Active(tp).Key == PageLayers.BaseKey);
    Check("18.7 - the layer that key 3 names really exists, so the second stroke "
          + "is on it and not orphaned",
          PageLayers.Of(tp, 3).Name == "Three");
}

// ---------------------------------------------------------------------------
// 15. 18.10 - the op log carries the layer list, and costs an unlayered page 0.
// ---------------------------------------------------------------------------
var oplogs = Directory.GetFiles(scratch, "oplog.*.jsonl");
Check("18.10 - the op log was written into the ISOLATED folder", oplogs.Length == 1,
      oplogs.Length == 1 ? Path.GetFileName(oplogs[0]) : $"{oplogs.Length} files");

// SyncLog's per-device state used to live in %LOCALAPPDATA% unconditionally, so
// a harness that saved rewrote the REAL user's read cursors from its own empty
// copy - the roadmap's replay risk, arriving by the front door. It now follows
// QUILL_DATA_FOLDER, so the fix is in SyncLog rather than in each harness.
Check("isolation - synccursors.json was written INSIDE the isolated folder",
      File.Exists(Path.Combine(scratch, "synccursors.json")));
Check("isolation - and so was deviceid.txt",
      File.Exists(Path.Combine(scratch, "deviceid.txt")));
Check("isolation - the user's own %LOCALAPPDATA%\\Quill state was never touched, "
      + "with no snapshot needed to make that true",
      SameBytes(cursorPath, cursorsBefore) && SameBytes(devicePath, deviceBefore));
if (oplogs.Length == 1)
{
    var lines = File.ReadAllLines(oplogs[0]);
    var pageOps = lines.Where(l => l.Contains("\"K\":\"pg\"")).ToList();
    Check("18.10 - a page op was logged", pageOps.Count > 0, $"{pageOps.Count} of {lines.Length} ops");
    Check("18.10 - the FIRST page op - the page before it had layers - carries no "
          + "layer list, so upgrading churns no ops at all",
          pageOps.Count > 0 && !pageOps[0].Contains("Layers"));
    Check("18.10 - a later one does, so a peer receives the layers its elements "
          + "name", pageOps.Any(o => o.Contains("Layers")));
    Check("18.10 - and never the ACTIVE layer, which is this person's UI state",
          !pageOps.Any(o => o.Contains("ActiveLayer")));
}

// ---------------------------------------------------------------------------
Report();
return failures > 0 ? 1 : 0;

}
finally
{
    // SyncLog now keeps its per-device state inside the data folder whenever
    // QUILL_DATA_FOLDER is set, so this should find nothing to put back. The
    // snapshot stays, and is checked BEFORE the restore: if the leak ever
    // reopens, this says so instead of quietly papering over it.
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
    else Console.WriteLine($"OK - {log.Count} checks held. CONCEPTS-REF 18 is a "
                           + "measurement, not a claim.");
}

/// <summary>Every element's serialised bytes, keyed by id. What must not move
/// when a layer's visibility does.</summary>
static Dictionary<string, string> ElementCensus(NotePage p)
{
    var o = new JsonSerializerOptions { WriteIndented = false };
    var d = new Dictionary<string, string>();
    foreach (var s in p.Strokes) d["st:" + s.Id] = JsonSerializer.Serialize(s, o);
    foreach (var s in p.Shapes) d["sh:" + s.Id] = JsonSerializer.Serialize(s, o);
    foreach (var t in p.Texts) d["tx:" + t.Id] = JsonSerializer.Serialize(t, o);
    return d;
}

/// <summary>Removes every <c>"Layers":[ ... ]</c> array from a library document,
/// bracket-matched and string-aware so an RTF run carrying a bracket cannot end
/// the scan early. What is left is the whole file EXCEPT the layer records.</summary>
static string StripLayerArrays(string json)
{
    const string key = "\"Layers\":";
    var sb = new System.Text.StringBuilder(json.Length);
    int i = 0;
    while (true)
    {
        int at = json.IndexOf(key, i, StringComparison.Ordinal);
        if (at < 0) { sb.Append(json, i, json.Length - i); break; }
        sb.Append(json, i, at - i);
        int j = at + key.Length;
        while (j < json.Length && json[j] != '[') j++;   // "Layers":null has no array
        if (j >= json.Length) { i = at + key.Length; continue; }
        int depth = 0;
        bool inStr = false, esc = false;
        for (; j < json.Length; j++)
        {
            char c = json[j];
            if (esc) { esc = false; continue; }
            if (inStr) { if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) { j++; break; } }
        }
        i = j;
    }
    return sb.ToString();
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
