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
// 16. 58.2 / 58.4 - THE DRAW PLAN: layer order across element types.
// ---------------------------------------------------------------------------
// The REAL DrawPlan out of LayerModels.cs - the function InkSurface.DrawRegion
// iterates, the thumbnail walks and hit-testing reads in reverse. Pure, so it
// is asked directly: which element is painted when.
{
    // Layer A (key 0, bottom) and layer B (key 1, top), each holding one shape,
    // one stroke and one text box.
    var dp = new NotePage();
    var aSh = new ShapeElement { LayerKey = 0 };
    var bSh = new ShapeElement { LayerKey = 1 };
    var aSt = new PenStroke { LayerKey = 0, Color = "#111111" };
    var bSt = new PenStroke { LayerKey = 1, Color = "#222222" };
    var aTx = new TextElement { LayerKey = 0 };
    var bTx = new TextElement { LayerKey = 1 };
    // The layers first: NextKey scans the elements too (18.7), so adding layer
    // B after elements already claiming key 1 would hand B key 2.
    PageLayers.Materialise(dp);
    var addedB = PageLayers.Add(dp);            // on top
    // List order deliberately NOT layer order: B's shape and stroke come first
    // in their lists, so a plan that walked lists flat would get it wrong.
    dp.Shapes.Add(bSh); dp.Shapes.Add(aSh);
    dp.Strokes.Add(bSt); dp.Strokes.Add(aSt);
    dp.Texts.Add(bTx); dp.Texts.Add(aTx);
    Check("58.4 - setup: layer B has key 1 and sits on top of A (key 0)",
          addedB.Key == 1 && PageLayers.All(dp).Select(l => l.Key).SequenceEqual(new[] { 0, 1 }));

    List<object> Ink(NotePage p) => DrawPlan.For(p).Sequence(p)
        .Where(e => e.Step.Kind is DrawStepKind.Shapes or DrawStepKind.Strokes)
        .Select(e => e.Element!).ToList();
    string Names(List<object> seq) => string.Join(", ", seq.Select(o =>
        ReferenceEquals(o, aSh) ? "A.shape" : ReferenceEquals(o, aSt) ? "A.stroke" :
        ReferenceEquals(o, bSh) ? "B.shape" : ReferenceEquals(o, bSt) ? "B.stroke" : "?"));

    var ab = Ink(dp);
    Check("58.2 - two layers each holding a shape and a stroke paint A.shapes, "
          + "A.strokes, B.shapes, B.strokes",
          ab.Count == 4 && ReferenceEquals(ab[0], aSh) && ReferenceEquals(ab[1], aSt)
          && ReferenceEquals(ab[2], bSh) && ReferenceEquals(ab[3], bSt), Names(ab));

    var plan = DrawPlan.For(dp);
    Check("58.2 - so B's shape is painted ABOVE A's stroke - the case type order "
          + "got wrong",
          plan.StepOf(DrawStepKind.Shapes, 1) > plan.StepOf(DrawStepKind.Strokes, 0));

    Check("58.2 - paint is the FIRST step, and there is exactly one",
          plan.Steps.Count > 0 && plan.Steps[0].Kind == DrawStepKind.Paint
          && plan.Steps.Count(s => s.Kind == DrawStepKind.Paint) == 1,
          string.Join(" ", plan.Steps.Select(s => s.Kind + (s.Bucket >= 0 ? s.Bucket.ToString() : ""))));

    int lastInk = plan.Steps.Select((s, i) => (s, i))
        .Where(x => x.s.Kind is DrawStepKind.Shapes or DrawStepKind.Strokes).Max(x => x.i);
    int firstText = plan.Steps.Select((s, i) => (s, i))
        .Where(x => x.s.Kind == DrawStepKind.Texts).Min(x => x.i);
    Check("58.2 - text is above ALL ink whatever the layer order: every Texts step "
          + "comes after every shape and stroke step", firstText > lastInk);

    // Reorder: B to the bottom. A list move, nothing repointed (18.2).
    PageLayers.Move(dp, 1, 0);
    var ba = Ink(dp);
    Check("58.2 - reordering the stack swaps them: B.shapes, B.strokes, A.shapes, "
          + "A.strokes",
          ba.Count == 4 && ReferenceEquals(ba[0], bSh) && ReferenceEquals(ba[1], bSt)
          && ReferenceEquals(ba[2], aSh) && ReferenceEquals(ba[3], aSt), Names(ba));
    var planBA = DrawPlan.For(dp);
    Check("58.4 - and hit-testing's answer turns over with it: A's shape is now "
          + "above B's stroke",
          DrawPlan.IsAbove(planBA.StepOf(DrawStepKind.Shapes, 0), 0,
                           planBA.StepOf(DrawStepKind.Strokes, 1), 0)
          && !DrawPlan.IsAbove(planBA.StepOf(DrawStepKind.Strokes, 1), 0,
                               planBA.StepOf(DrawStepKind.Shapes, 0), 0));
    Check("58.4 - within one layer a stroke is above that layer's shape, and a "
          + "later list index above an earlier one",
          DrawPlan.IsAbove(planBA.StepOf(DrawStepKind.Strokes, 0), 0,
                           planBA.StepOf(DrawStepKind.Shapes, 0), 5)
          && DrawPlan.IsAbove(planBA.StepOf(DrawStepKind.Strokes, 0), 2,
                              planBA.StepOf(DrawStepKind.Strokes, 0), 1));
    PageLayers.Move(dp, 1, 1);                  // back to A bottom, B top

    // Hidden, and 0% - one fact, EffectiveOpacity (49.1).
    var layerB = PageLayers.Of(dp, 1);
    layerB.Hidden = true;
    var hid = DrawPlan.For(dp);
    var hidSeq = hid.Sequence(dp).Where(e => e.Element != null).Select(e => e.Element!).ToList();
    Check("49.1 - a HIDDEN layer yields nothing: no step, no shape, no stroke, no text",
          !hid.Steps.Any(s => s.Bucket == 1)
          && !hidSeq.Any(o => ReferenceEquals(o, bSh) || ReferenceEquals(o, bSt) || ReferenceEquals(o, bTx))
          && hid.StepOf(DrawStepKind.Shapes, 1) == -1 && hid.StepOf(DrawStepKind.Strokes, 1) == -1,
          Names(Ink(dp)));
    Check("49.1 - and the visible layer still draws in full",
          Ink(dp).Count == 2 && hidSeq.Any(o => ReferenceEquals(o, aTx)));
    Check("58.4 - a hidden layer's element is never 'above' anything to a hit-test",
          !DrawPlan.IsAbove(hid.StepOf(DrawStepKind.Strokes, 1), 9,
                            hid.StepOf(DrawStepKind.Shapes, 0), 0));
    layerB.Hidden = false;
    layerB.Opacity = 0f;
    Check("49.1 - a layer at 0% opacity is the SAME fact as hidden: nothing drawn",
          !DrawPlan.For(dp).Steps.Any(s => s.Bucket == 1));
    layerB.Opacity = 0.4f;
    var faded = DrawPlan.For(dp);
    Check("18.8 - a layer at 40% draws, and its steps carry EffectiveOpacity as the "
          + "multiplier",
          faded.Steps.Where(s => s.Bucket == 1).All(s => Math.Abs(s.Multiplier - 0.4f) < 1e-6)
          && faded.Steps.Count(s => s.Bucket == 1) == 3);
    layerB.Opacity = 1f;

    // 18.7: an unknown key is drawn - in the base layer's bucket.
    var dpOrphan = new PenStroke { LayerKey = 99, Color = "#999999" };
    dp.Strokes.Add(dpOrphan);
    var withOrphan = DrawPlan.For(dp).Sequence(dp).ToList();
    int orphanAt = withOrphan.FindIndex(e => ReferenceEquals(e.Element, dpOrphan));
    Check("18.7 - an element whose key names no layer is DRAWN, not dropped",
          orphanAt >= 0 && withOrphan[orphanAt].Step.Kind == DrawStepKind.Strokes);
    Check("18.7 - and it paints with the base layer (A), below B's shape",
          orphanAt >= 0 && withOrphan[orphanAt].Step.Bucket == PageLayers.All(dp).ToList().FindIndex(l => l.Key == 0)
          && orphanAt < withOrphan.FindIndex(e => ReferenceEquals(e.Element, bSh)));
    Check("18.7 - it was not rewritten: its key is still 99", dpOrphan.LayerKey == 99);

    // InOrder and the plan share one resolver: every element lands in the same
    // bucket both ways.
    var dpBuckets = PageLayers.InOrder(dp).ToList();
    var dplan = DrawPlan.For(dp);
    bool agree = true;
    for (int b = 0; b < dpBuckets.Count; b++)
    {
        foreach (var s in dpBuckets[b].Shapes) agree &= dplan.BucketOf(s.LayerKey) == b;
        foreach (var s in dpBuckets[b].Strokes) agree &= dplan.BucketOf(s.LayerKey) == b;
        foreach (var t in dpBuckets[b].Texts) agree &= dplan.BucketOf(t.LayerKey) == b;
    }
    Check("18.9 - InOrder's buckets and the plan's agree for every element, the "
          + "orphan included", agree);
    dp.Strokes.Remove(dpOrphan);

    // The big-page ink cache (#43) draws every stroke as one image. Where it
    // may do so without changing the picture:
    Check("58.4 - ink cache: with B's SHAPE between A's strokes and B's strokes, "
          + "one all-strokes image has no correct height (-1)",
          DrawPlan.For(dp).InkCacheStep(dp) == -1);
    dp.Shapes.Remove(bSh);
    var noBShape = DrawPlan.For(dp);
    Check("58.4 - ink cache: with no shape between them it is A's Strokes step",
          noBShape.InkCacheStep(dp) == noBShape.StepOf(DrawStepKind.Strokes, 0));
    dp.Shapes.Insert(0, bSh);
}
{
    // 18.5 / 58.2: a page with NO Layers array, and one with one layer, paint
    // their elements exactly as the old loop walked them - every shape in list
    // order, then every stroke in list order. Only paint moved (below it all).
    var op = new NotePage();
    var s1 = new ShapeElement(); var s2 = new ShapeElement();
    var k1 = new PenStroke { Color = "#1" }; var k2 = new PenStroke { Color = "#2" };
    var k3 = new PenStroke { LayerKey = 42, Color = "#3" };   // orphan: still drawn
    var t1 = new TextElement();
    op.Shapes.Add(s1); op.Shapes.Add(s2);
    op.Strokes.Add(k1); op.Strokes.Add(k2); op.Strokes.Add(k3);
    op.Texts.Add(t1);
    var oldOrder = new List<object> { s1, s2, k1, k2, k3, t1 };

    bool SameAsOld(NotePage p)
    {
        var seq = DrawPlan.For(p).Sequence(p).Where(e => e.Element != null)
                                           .Select(e => e.Element!).ToList();
        return seq.Count == oldOrder.Count && seq.Zip(oldOrder).All(z => ReferenceEquals(z.First, z.Second));
    }
    var implicitPlan = DrawPlan.For(op);
    // 58.10 ruling B added the WetPaint step (the live oil scratch) after the
    // ink; it draws nothing unless an oil gesture is live (section 18).
    Check("58.4 / 58.10 - a page with no Layers array has ONE bucket and the plan Paint, "
          + "Shapes, Strokes, WetPaint, Texts",
          PageLayers.IsImplicit(op) && implicitPlan.LayerCount == 1
          && implicitPlan.Steps.Select(s => s.Kind).SequenceEqual(new[]
             { DrawStepKind.Paint, DrawStepKind.Shapes, DrawStepKind.Strokes,
               DrawStepKind.WetPaint, DrawStepKind.Texts }));
    Check("58.4 - and it paints every element in exactly the old order", SameAsOld(op));
    PageLayers.Materialise(op);
    Check("58.4 - a page with ONE real layer paints them in exactly the old order too",
          !PageLayers.IsImplicit(op) && SameAsOld(op));
    Check("58.4 - the one-layer ink cache sits at that layer's Strokes step, where "
          + "it always sat relative to shapes",
          DrawPlan.For(op).InkCacheStep(op) == 2);
}

// ---------------------------------------------------------------------------
// 17. 58.10 RULING A - INVISIBLE MEANS UNSELECTABLE, EVERYWHERE.
// ---------------------------------------------------------------------------
// One fact - PageLayers.IsVisible, i.e. EffectiveOpacity > 0 - and every pick
// path reads it. The paths are run through the REAL shared code the canvas
// calls: LayerPick.Catchable (CanCatch: click, lasso, rectangle),
// LayerPick.Lasso (SelectWithLasso: lasso AND rectangle), and LayerPick.Topmost
// (HitStrokeForClick, FindStrokeNear = the eraser preview, SampleColorAt = the
// eyedropper, TopmostShape = the press grab and the axes/equation editors).
// Each path is asked, per element, "can you answer with THIS one?" - the
// geometry is replaced by identity, because the geometry is not what 58.10
// is about. What the harness cannot reach is InkSurface's geometry and its
// wiring of these calls; that was read and compiled (58.10).
{
    var sp = new NotePage();
    PageLayers.Materialise(sp);                          // V: key 0, visible
    var lH = PageLayers.Add(sp, "H"); lH.Hidden = true;  // hidden
    var lZ = PageLayers.Add(sp, "Z"); lZ.Opacity = 0f;   // 0% - ruling A's case
    var lL = PageLayers.Add(sp, "L"); lL.Locked = true;  // locked, drawn
    var lF = PageLayers.Add(sp, "F"); lF.Opacity = 0.4f; // 40%, drawn
    var keyName = new Dictionary<int, string>
        { [0] = "V", [lH.Key] = "H", [lZ.Key] = "Z", [lL.Key] = "L", [lF.Key] = "F" };
    var stOf = new Dictionary<int, PenStroke>();
    var shOf = new Dictionary<int, ShapeElement>();
    var txOf = new Dictionary<int, TextElement>();
    foreach (var k in keyName.Keys)
    {
        var st = new PenStroke { LayerKey = k, Color = "#101010" };
        st.Points.Add(new StrokePoint(10, 10, 0.5f));
        sp.Strokes.Add(st); stOf[k] = st;
        var sh = new ShapeElement { LayerKey = k }; sp.Shapes.Add(sh); shOf[k] = sh;
        var tx = new TextElement { LayerKey = k }; sp.Texts.Add(tx); txOf[k] = tx;
    }
    var rp = DrawPlan.For(sp);

    bool Click(PenStroke s) => ReferenceEquals(LayerPick.Topmost(rp, sp.Strokes, DrawStepKind.Strokes,
        x => x.LayerKey, x => LayerPick.Catchable(sp, x.LayerKey, x.Locked, false, LayerScope.AllLayers),
        x => ReferenceEquals(x, s)), s);
    // The eraser preview and the eyedropper's stroke walk make the SAME call
    // (no lock or scope gate): LayerPick.Topmost with only a candidate test.
    bool EraserPreview(PenStroke s) => ReferenceEquals(LayerPick.Topmost(rp, sp.Strokes, DrawStepKind.Strokes,
        x => x.LayerKey, null, x => ReferenceEquals(x, s)), s);
    bool EyedropperShape(ShapeElement s) => ReferenceEquals(LayerPick.Topmost(rp, sp.Shapes, DrawStepKind.Shapes,
        x => x.LayerKey, null, x => ReferenceEquals(x, s)), s);
    (List<PenStroke> St, List<ShapeElement> Sh, List<TextElement> Tx) LassoAll(NotePage p)
    {
        var a = new List<PenStroke>(); var b = new List<ShapeElement>(); var c = new List<TextElement>();
        LayerPick.Lasso(p, false, LayerScope.AllLayers, null, _ => true, null, _ => true, _ => true, a, b, c);
        return (a, b, c);
    }
    var lassoed = LassoAll(sp);
    bool Lassoed(int k) => lassoed.St.Contains(stOf[k]) || lassoed.Sh.Contains(shOf[k]) || lassoed.Tx.Contains(txOf[k]);

    string Row(int k) => $"{keyName[k]}: visible={PageLayers.IsVisible(sp, k)} drawn={rp.IsDrawn(k)} " +
        $"canSelect={PageLayers.CanSelect(sp, k, LayerScope.AllLayers)} click={Click(stOf[k])} " +
        $"lasso={Lassoed(k)} eraserPreview={EraserPreview(stOf[k])} eyedropper={EraserPreview(stOf[k]) || EyedropperShape(shOf[k])} " +
        $"pressGrab={EyedropperShape(shOf[k])}";
    string table = string.Join(" | ", keyName.Keys.Select(Row));

    Check("58.10 A - the fact: a layer at 0% is NOT visible, exactly as a hidden one is "
          + "(until 58.10 IsVisible was !Hidden)",
          !PageLayers.IsVisible(sp, lH.Key) && !PageLayers.IsVisible(sp, lZ.Key)
          && PageLayers.IsVisible(sp, 0) && PageLayers.IsVisible(sp, lL.Key) && PageLayers.IsVisible(sp, lF.Key));
    Check("58.10 A - the plan's drawn fact and PageLayers.IsVisible agree for every layer",
          keyName.Keys.All(k => rp.IsDrawn(k) == PageLayers.IsVisible(sp, k)
                                && (rp.StepOf(DrawStepKind.Strokes, k) >= 0) == rp.IsDrawn(k)));
    Check("58.10 A - CanSelect (and so CanCatch) refuses the 0% layer, not only the hidden one",
          !PageLayers.CanSelect(sp, lZ.Key, LayerScope.AllLayers) && !PageLayers.IsEditable(sp, lZ.Key)
          && !PageLayers.CanSelect(sp, lH.Key, LayerScope.AllLayers));
    foreach (var (k, what) in new[] { (lH.Key, "HIDDEN"), (lZ.Key, "0%") })
        Check($"58.10 A - PARITY on the {what} layer: click, lasso/rectangle, eraser preview, "
              + "eyedropper and press grab ALL refuse it",
              !Click(stOf[k]) && !Lassoed(k) && !EraserPreview(stOf[k]) && !EyedropperShape(shOf[k]),
              Row(k));
    Check("58.10 A - and on the visible and 40% layers every path answers",
          new[] { 0, lF.Key }.All(k => Click(stOf[k]) && lassoed.St.Contains(stOf[k]) && lassoed.Sh.Contains(shOf[k])
                                       && lassoed.Tx.Contains(txOf[k]) && EraserPreview(stOf[k]) && EyedropperShape(shOf[k])),
          table);
    Check("58.10 A - Locked stays a SEPARATE fact: the selection gestures refuse the locked "
          + "layer, while the eyedropper, eraser preview and press grab still see it (it is drawn)",
          !Click(stOf[lL.Key]) && !Lassoed(lL.Key) && EraserPreview(stOf[lL.Key]) && EyedropperShape(shOf[lL.Key]),
          Row(lL.Key));

    // Topmost across layers: F is the top drawn layer.
    var anyClick = LayerPick.Topmost(rp, sp.Strokes, DrawStepKind.Strokes, x => x.LayerKey,
        x => LayerPick.Catchable(sp, x.LayerKey, x.Locked, false, LayerScope.AllLayers), _ => true);
    var anyErase = LayerPick.Topmost(rp, sp.Strokes, DrawStepKind.Strokes, x => x.LayerKey, null, _ => true);
    Check("58.10 check 2 - with every stroke under the pointer, the eraser preview and the "
          + "click both name the TOP drawn layer's stroke (F)",
          ReferenceEquals(anyClick, stOf[lF.Key]) && ReferenceEquals(anyErase, stOf[lF.Key]));
    lF.Hidden = true;
    var rpNoF = DrawPlan.For(sp);
    var eraseNoF = LayerPick.Topmost(rpNoF, sp.Strokes, DrawStepKind.Strokes, x => x.LayerKey, null, _ => true);
    var clickNoF = LayerPick.Topmost(rpNoF, sp.Strokes, DrawStepKind.Strokes, x => x.LayerKey,
        x => LayerPick.Catchable(sp, x.LayerKey, x.Locked, false, LayerScope.AllLayers), _ => true);
    Check("58.10 check 2 - hide F: the preview falls to L (drawn, locked), the click to V "
          + "(the lock refuses L); neither ever names H or Z",
          ReferenceEquals(eraseNoF, stOf[lL.Key]) && ReferenceEquals(clickNoF, stOf[0]),
          $"preview={(eraseNoF == null ? "none" : keyName[eraseNoF.LayerKey])}, click={(clickNoF == null ? "none" : keyName[clickNoF.LayerKey])}");
    lF.Hidden = false;

    // Reorder so the list is NOT the stack: V's stroke is last in the list but
    // V goes to the top. The preview used to take the first hit in list order.
    var order = new NotePage();
    PageLayers.Materialise(order);
    var top = PageLayers.Add(order, "top");
    var lowFirst = new PenStroke { LayerKey = top.Key, Color = "#1" };
    var highLast = new PenStroke { LayerKey = 0, Color = "#2" };
    order.Strokes.Add(lowFirst); order.Strokes.Add(highLast);
    PageLayers.Move(order, top.Key, 0);          // "top" to the bottom: base (key 0) is now above
    var orderPlan = DrawPlan.For(order);
    Check("58.10 check 2 - the eraser preview names the VISUALLY topmost stroke, not the "
          + "first in list order",
          ReferenceEquals(LayerPick.Topmost(orderPlan, order.Strokes, DrawStepKind.Strokes,
                                            x => x.LayerKey, null, _ => true), highLast));
    order.Strokes.Reverse();                     // now the top one is FIRST in the list
    Check("58.10 check 2 - nor the last in list order: reversed, it still names the same stroke",
          ReferenceEquals(LayerPick.Topmost(orderPlan, order.Strokes, DrawStepKind.Strokes,
                                            x => x.LayerKey, null, _ => true), highLast));

    // The inconsistency the check named: HitStrokeForClick tested step < 0 only
    // inside `if (multi)`. A ONE-layer page whose layer is at 0%:
    var one = new NotePage();
    var only = PageLayers.Materialise(one)[0];
    only.Opacity = 0f;
    var oneSt = new PenStroke { Color = "#1" }; oneSt.Points.Add(new StrokePoint(1, 1, 0.5f));
    var oneSh = new ShapeElement();
    one.Strokes.Add(oneSt); one.Shapes.Add(oneSh); one.Texts.Add(new TextElement());
    var onePlan = DrawPlan.For(one);
    var oneLasso = LassoAll(one);
    Check("58.10 A - ONE layer at 0% (the `if (multi)` case): the click, the lasso, the "
          + "eraser preview, the eyedropper and the press grab all refuse",
          onePlan.LayerCount == 1
          && LayerPick.Topmost(onePlan, one.Strokes, DrawStepKind.Strokes, x => x.LayerKey,
                 x => LayerPick.Catchable(one, x.LayerKey, x.Locked, false, LayerScope.AllLayers), _ => true) == null
          && LayerPick.Topmost(onePlan, one.Strokes, DrawStepKind.Strokes, x => x.LayerKey, null, _ => true) == null
          && LayerPick.Topmost(onePlan, one.Shapes, DrawStepKind.Shapes, x => x.LayerKey, null, _ => true) == null
          && oneLasso.St.Count == 0 && oneLasso.Sh.Count == 0 && oneLasso.Tx.Count == 0);
    only.Opacity = 1f;
    var onePlan1 = DrawPlan.For(one);
    var oneSt2 = new PenStroke { Color = "#2" }; one.Strokes.Add(oneSt2);
    Check("58.10 A - and at 100% the one-layer page picks as it always did: the last hit "
          + "in the list, and the lasso takes everything",
          ReferenceEquals(LayerPick.Topmost(onePlan1, one.Strokes, DrawStepKind.Strokes, x => x.LayerKey,
                              null, _ => true), oneSt2)
          && LassoAll(one).Tx.Count == 1 && LassoAll(one).Sh.Count == 1);
}

// ---------------------------------------------------------------------------
// 18. 58.10 RULING B - THE WET STROKE STAYS ON TOP WHILE PAINTING.
// ---------------------------------------------------------------------------
// The scratch's height is the plan's WetPaint step, and whether anything is
// composited at a paint step is DrawPlan.PaintAt - the one flag InkSurface's
// DrawRegion asks (DrawPaint draws exactly the sources it is handed). What is
// not reachable here: that InkSurface passes OilGestureActive truthfully, and
// that every gesture end settles the brush (SettleOilGesture) - read, compiled.
{
    NotePage Page(int layers, bool hideTop)
    {
        var p = new NotePage();
        if (layers > 0)
        {
            PageLayers.Materialise(p);
            for (int i = 1; i < layers; i++) PageLayers.Add(p);
            if (hideTop) PageLayers.All(p)[^1].Hidden = true;
        }
        return p;
    }
    var cases = new (string Name, NotePage P)[]
    {
        ("no Layers array", Page(0, false)), ("one layer", Page(1, false)),
        ("two layers", Page(2, false)), ("five layers", Page(5, false)), ("five, top hidden", Page(5, true)),
    };
    bool onTopEverywhere = true, onceEverywhere = true, settledOnce = true, goneAfter = true, neverBoth = true;
    var detail = new List<string>();
    foreach (var (name, p) in cases)
    {
        var pl = DrawPlan.For(p);
        var steps = pl.Steps;
        int lastInk = -1, firstText = int.MaxValue;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Kind is DrawStepKind.Shapes or DrawStepKind.Strokes) lastInk = i;
            if (steps[i].Kind == DrawStepKind.Texts) firstText = Math.Min(firstText, i);
        }
        var wetAt = Enumerable.Range(0, steps.Count)
            .Where(i => (DrawPlan.PaintAt(steps[i].Kind, true) & PaintSource.Wet) != 0).ToList();
        var settledAt = Enumerable.Range(0, steps.Count)
            .Where(i => (DrawPlan.PaintAt(steps[i].Kind, true) & PaintSource.Settled) != 0).ToList();
        var wetAfterEnd = Enumerable.Range(0, steps.Count)
            .Where(i => (DrawPlan.PaintAt(steps[i].Kind, false) & PaintSource.Wet) != 0).ToList();
        onceEverywhere &= wetAt.Count == 1 && wetAt[0] == pl.WetPaintStep
                          && steps.Count(s => s.Kind == DrawStepKind.WetPaint) == 1;
        onTopEverywhere &= wetAt.Count == 1 && wetAt[0] > lastInk && wetAt[0] < firstText;
        settledOnce &= settledAt.Count == 1 && settledAt[0] == 0 && steps[0].Kind == DrawStepKind.Paint
                       && Enumerable.Range(0, steps.Count).Count(i =>
                              (DrawPlan.PaintAt(steps[i].Kind, false) & PaintSource.Settled) != 0) == 1;
        goneAfter &= wetAfterEnd.Count == 0;
        neverBoth &= steps.All(s => DrawPlan.PaintAt(s.Kind, true) != (PaintSource.Settled | PaintSource.Wet));
        detail.Add($"{name}: settled@{string.Join(",", settledAt)} wet@{string.Join(",", wetAt)} lastInk@{lastInk}");
    }
    string d = string.Join("; ", detail);
    Check("58.10 B - while the gesture is live the wet scratch is composited ONCE, at the "
          + "WetPaint step", onceEverywhere, d);
    Check("58.10 B - and that step is ABOVE every layer's shapes and strokes (and below "
          + "text, which is XAML over the canvas), in every layer arrangement", onTopEverywhere, d);
    Check("58.10 B - settled paint is unchanged: composited once, at step 0, below every "
          + "layer (58.2)", settledOnce, d);
    Check("58.10 B - no step composites the settled tiles AND the scratch, so the scratch "
          + "cannot be drawn twice", neverBoth);
    Check("58.10 B - once the gesture has ended, no step draws the scratch at all - it "
          + "cannot be left on top", goneAfter);
}

// ---------------------------------------------------------------------------
// 19. 58.10 CHECK 1 - InkCacheStep's cost per invalidated REGION.
// ---------------------------------------------------------------------------
// DrawRegion runs once per region, ~120 in a pass. The multi-layer answer walks
// every stroke and shape; 58.4 asked it per region. DrawPlan counts the
// elements it walks (InkCacheElementVisits), so the cost is COUNTED, not timed.
{
    const int Regions = 120, StrokeCount = 2500;
    NotePage Big(int layers)
    {
        var p = new NotePage();
        if (layers > 1) { PageLayers.Materialise(p); for (int i = 1; i < layers; i++) PageLayers.Add(p); }
        var keys = PageLayers.All(p).Select(l => l.Key).ToArray();
        for (int i = 0; i < StrokeCount; i++)
            p.Strokes.Add(new PenStroke { LayerKey = keys[i % keys.Length], Color = "#1" });
        for (int i = 0; i < 20; i++) p.Shapes.Add(new ShapeElement { LayerKey = keys[0] });   // bottom only
        return p;
    }
    // Counted REGION BY REGION: what the first region of the pass walked, and
    // the most any LATER region of the same pass walked.
    long PerPass(DrawPlan pl, NotePage p, long pass, out bool sameAnswers, out long firstRegion, out long laterMax)
    {
        long before = pl.InkCacheElementVisits;
        int first = pl.InkCacheStep(p, pass);
        firstRegion = pl.InkCacheElementVisits - before;
        sameAnswers = true;
        laterMax = 0;
        for (int r = 1; r < Regions; r++)
        {
            long v0 = pl.InkCacheElementVisits;
            sameAnswers &= pl.InkCacheStep(p, pass) == first;
            laterMax = Math.Max(laterMax, pl.InkCacheElementVisits - v0);
        }
        return pl.InkCacheElementVisits - before;
    }

    var one = Big(1); var onePlan = DrawPlan.For(one);
    long oneWork = PerPass(onePlan, one, 1, out bool oneSame, out long oneFirst, out long oneLater);
    var five = Big(5); var fivePlan = DrawPlan.For(five);
    long fiveWork = PerPass(fivePlan, five, 1, out bool fiveSame, out long fiveFirst, out long fiveLater);
    int n5 = five.Strokes.Count + five.Shapes.Count;
    Console.WriteLine($"COST per region: 1 layer first={oneFirst} later(max)={oneLater}; "
                      + $"5 layers first={fiveFirst} later(max)={fiveLater}");

    // What 58.4 paid: the walk on EVERY region of the pass.
    var rawPlan = DrawPlan.For(five);
    for (int r = 0; r < Regions; r++) rawPlan.InkCacheStep(five);
    long rawWork = rawPlan.InkCacheElementVisits;

    string cost = $"1 layer: {oneWork} elements walked per {Regions}-region pass = {oneWork / (double)Regions:0.##}/region; "
                + $"5 layers: {fiveWork} per pass = {fiveWork / (double)Regions:0.##}/region "
                + $"(58.4's per-region walk: {rawWork} per pass = {rawWork / Regions}/region); page = {n5} elements";
    Console.WriteLine("COST " + cost);
    Check("58.10 check 1 - one layer: a whole pass walks NO elements (O(1) per region, as on main)",
          oneWork == 0 && oneFirst == 0 && oneLater == 0 && oneSame, cost);
    Check("58.10 check 1 - five layers: a whole pass of 120 regions walks the page ONCE, so "
          + "every region after the first is O(1)",
          fiveWork == n5 && fiveFirst == n5 && fiveLater == 0 && fiveSame,
          $"first region {fiveFirst}, each later region at most {fiveLater}; " + cost);
    Check("58.10 check 1 - and 58.4's per-region cost is what that replaces: 120 walks per pass",
          rawWork == (long)Regions * n5, cost);
    Check("58.10 check 1 - the memoised answer is the raw answer",
          fivePlan.InkCacheStep(five, 1) == DrawPlan.For(five).InkCacheStep(five));

    // Freshness: an edit lands in a LATER pass, and that pass walks again.
    var mid = PageLayers.All(five)[2].Key;
    five.Shapes.Add(new ShapeElement { LayerKey = mid });   // a shape between layers' strokes
    int staleOrFresh = fivePlan.InkCacheStep(five, 2);
    Check("58.10 check 1 - the memo is per pass: the next pass after a shape lands between "
          + "two layers' strokes walks afresh and answers -1 (per-stroke path)",
          staleOrFresh == -1 && fivePlan.InkCacheStep(five, 1 + 1) == -1
          && DrawPlan.For(five).InkCacheStep(five) == -1);
}

// ---------------------------------------------------------------------------
// 20. 58.10 CHECK 3 - the thumbnail stamp moves when an element changes layer.
// ---------------------------------------------------------------------------
{
    NotePage StampPage()
    {
        var p = new NotePage { Background = "#FAF7F0" };
        var s = new PenStroke { Color = "#223344", Size = 3 };
        s.Points.Add(new StrokePoint(10, 20, 0.5f)); s.Points.Add(new StrokePoint(90, 70, 0.5f));
        p.Strokes.Add(s);
        p.Shapes.Add(new ShapeElement { X = 5, Y = 6, W = 70, H = 40, Color = "#1B5FC1" });
        p.Texts.Add(new TextElement { X = 30, Y = 40, Width = 200, Rtf = "{\\rtf1 hi}" });
        return p;
    }
    // Recorded from the stamp BEFORE LayerKey was mixed in (the pure move of
    // ThumbnailCache.Stamp into ThumbnailStamp.cs), so these prove that a page
    // with no Layers array, or one layer, keeps the key it always had and no
    // cached PNG of an existing page is invalidated by 58.10.
    // (stamp code checked identical to HEAD 126d1ec's ThumbnailCache.Stamp
    // before these were taken)
    const string GoldImplicit = "b1ec38de41aba562";
    const string GoldOneLayer = "12e75f09d7380bf3";
    var implicitPage = StampPage();
    string sImplicit = ThumbnailStamp.Of(implicitPage);
    var onePage = StampPage(); PageLayers.Materialise(onePage);
    string sOne = ThumbnailStamp.Of(onePage);
    Console.WriteLine($"STAMP implicit={sImplicit} oneLayer={sOne}");
    Check("58.10 check 3 - a page with no Layers array keeps its old stamp", sImplicit == GoldImplicit, sImplicit);
    Check("58.10 check 3 - a page with one layer keeps its old stamp", sOne == GoldOneLayer, sOne);
    implicitPage.Strokes[0].LayerKey = 7;   // an orphan: same bucket, same picture
    Check("58.10 check 3 - with one bucket an element's key does not change the picture, "
          + "and does not change the stamp", ThumbnailStamp.Of(implicitPage) == sImplicit);

    var two = StampPage(); PageLayers.Materialise(two); var upper = PageLayers.Add(two);
    string sTwo = ThumbnailStamp.Of(two);
    two.Strokes[0].LayerKey = upper.Key;
    string sStrokeUp = ThumbnailStamp.Of(two);
    two.Strokes[0].LayerKey = 0;
    two.Shapes[0].LayerKey = upper.Key;
    string sShapeUp = ThumbnailStamp.Of(two);
    two.Shapes[0].LayerKey = 0;
    two.Texts[0].LayerKey = upper.Key;
    string sTextUp = ThumbnailStamp.Of(two);
    two.Texts[0].LayerKey = 0;
    Check("58.10 check 3 - two layers: moving the STROKE to the other layer changes the stamp",
          sStrokeUp != sTwo, $"{sTwo} -> {sStrokeUp}");
    Check("58.10 check 3 - moving the SHAPE, or the TEXT, changes it too",
          sShapeUp != sTwo && sTextUp != sTwo && sShapeUp != sStrokeUp);
    Check("58.10 check 3 - and moving them back restores it (the key is content, not history)",
          ThumbnailStamp.Of(two) == sTwo);
}

// ---------------------------------------------------------------------------
// 21. 58.10 CHECKS 5 AND 6 - the resolver alone, and the empty layer list.
// ---------------------------------------------------------------------------
{
    string Thrown(Action a)
    {
        try { a(); return "none"; }
        catch (Exception ex) { return ex.GetType().Name; }
    }
    string empty = Thrown(() => new DrawPlan(Array.Empty<Layer>()));
    string nul = Thrown(() => new DrawPlan(null!));
    string bucketsEmpty = Thrown(() => new LayerBuckets(new List<Layer>()));
    Check("58.10 check 6 - an EMPTY layer list is rejected at construction with "
          + "ArgumentException, not an IndexOutOfRange later", empty == nameof(ArgumentException), empty);
    Check("58.10 check 6 - null is ArgumentNullException, and the resolver on its own "
          + "rejects empty the same way",
          nul == nameof(ArgumentNullException) && bucketsEmpty == nameof(ArgumentException), $"{nul}, {bucketsEmpty}");
    var single = new DrawPlan(new[] { new Layer { Key = 5 } });
    Check("58.10 check 6 - every non-empty list is total: one layer with no base key "
          + "resolves any key to bucket 0 and draws",
          single.BucketOf(0) == 0 && single.BucketOf(99) == 0 && single.IsDrawn(123)
          && single.StepOf(DrawStepKind.Strokes, 42) >= 0 && single.InkCacheStep(new NotePage()) >= 0);

    // Check 5: InOrder resolves through LayerBuckets and the plan through the
    // same struct, so they cannot disagree - including duplicate keys and an
    // orphan on a page with no base layer.
    var odd = new NotePage { Layers = new List<Layer> { new() { Key = 3 }, new() { Key = 4 }, new() { Key = 3 } } };
    odd.Strokes.Add(new PenStroke { LayerKey = 3, Color = "#1" });
    odd.Strokes.Add(new PenStroke { LayerKey = 4, Color = "#2" });
    odd.Strokes.Add(new PenStroke { LayerKey = 77, Color = "#3" });
    var oddPlan = DrawPlan.For(odd);
    var oddBuckets = PageLayers.InOrder(odd).ToList();
    bool oddAgree = Enumerable.Range(0, oddBuckets.Count)
        .All(b => oddBuckets[b].Strokes.All(s => oddPlan.BucketOf(s.LayerKey) == b));
    Check("58.10 check 5 - InOrder (resolver only, no plan) and the plan agree on a "
          + "duplicated key and an orphan with no base layer",
          oddAgree && oddBuckets[0].Strokes.Count == 2 && oddBuckets[2].Strokes.Count == 0,
          string.Join(",", oddBuckets.Select(b => b.Strokes.Count)));
}

// ---------------------------------------------------------------------------
// 22. 58.11 - THE CLICK'S OWN GATE CARRIES THE FACT (a round-2 check finding).
// ---------------------------------------------------------------------------
// LayerPick.Topmost refuses an undrawn layer through plan.IsDrawn BEFORE it
// asks the admit gate (Catchable -> CanSelect), so a regression in CanSelect
// alone left every click check in section 17 green. Here the plan is held
// still - built while every layer was drawn - and the layers are hidden or
// taken to 0% afterwards, so only the click's own admit gate can refuse.
{
    var gp = new NotePage();
    PageLayers.Materialise(gp);
    var gH = PageLayers.Add(gp, "H");
    var gZ = PageLayers.Add(gp, "Z");
    var gV = PageLayers.Add(gp, "V");
    PenStroke St(int k) { var s = new PenStroke { LayerKey = k, Color = "#1" }; s.Points.Add(new StrokePoint(1, 1, 0.5f)); gp.Strokes.Add(s); return s; }
    var sH = St(gH.Key); var sZ = St(gZ.Key); var sV = St(gV.Key);
    var drawnPlan = DrawPlan.For(gp);                 // every layer drawn
    gH.Hidden = true;
    gZ.Opacity = 0f;
    bool ClickVia(DrawPlan pl, PenStroke s) => ReferenceEquals(LayerPick.Topmost(pl, gp.Strokes, DrawStepKind.Strokes,
        x => x.LayerKey, x => LayerPick.Catchable(gp, x.LayerKey, x.Locked, false, LayerScope.AllLayers),
        x => ReferenceEquals(x, s)), s);
    Check("58.11 CanSelect via the click - Catchable itself refuses the hidden AND the 0% layer "
          + "(asked directly, no plan)",
          !LayerPick.Catchable(gp, gH.Key, false, false, LayerScope.AllLayers)
          && !LayerPick.Catchable(gp, gZ.Key, false, false, LayerScope.AllLayers)
          && LayerPick.Catchable(gp, gV.Key, false, false, LayerScope.AllLayers));
    Check("58.11 CanSelect via the click - with the plan held at 'all drawn', the CLICK still "
          + "refuses the hidden and the 0% layer's stroke: the gate alone carries the fact",
          !ClickVia(drawnPlan, sH) && !ClickVia(drawnPlan, sZ),
          $"hidden click={ClickVia(drawnPlan, sH)}, 0% click={ClickVia(drawnPlan, sZ)}");
    Check("58.11 CanSelect via the click - control: the same held plan still clicks the visible "
          + "layer's stroke", ClickVia(drawnPlan, sV));
}

// ---------------------------------------------------------------------------
// 23. 58.11 RULING R1 - A HIDDEN OR 0% ACTIVE LAYER REFUSES NEW CONTENT.
// ---------------------------------------------------------------------------
// The gate every creation path asks (InkSurface.CanCreateOnActiveLayer is a
// call to LayerGate.RefusesNewContent plus the event that raises the message),
// the message verbatim, the one-tap Show, and paste's own rule. What is not
// reachable here: that each creation path calls the gate before it creates -
// that was read and compiled (58.11.2 lists every path).
{
    var cp = new NotePage();
    PageLayers.Materialise(cp);                        // key 0, "Layer 1", visible
    var cH = PageLayers.Add(cp, "Sketch"); cH.Hidden = true;
    var cZ = PageLayers.Add(cp, "Notes"); cZ.Opacity = 0f;
    var cF = PageLayers.Add(cp, "Faint"); cF.Opacity = 0.4f;
    var cN = PageLayers.Add(cp, "Odd"); cN.Opacity = float.NaN;
    Layer? RefusesWith(int active) { PageLayers.SetActive(cp, active); return LayerGate.RefusesNewContent(cp); }
    Check("58.11 R1 - the hidden active layer refuses, and names itself",
          ReferenceEquals(RefusesWith(cH.Key), cH));
    Check("58.11 R1 - the 0% active layer refuses exactly as the hidden one does (one fact, "
          + "IsVisible), and so does an opacity that is not a number",
          ReferenceEquals(RefusesWith(cZ.Key), cZ) && ReferenceEquals(RefusesWith(cN.Key), cN));
    Check("58.11 R1 - a visible active layer, and a faint (40%) one, take new content",
          RefusesWith(0) == null && RefusesWith(cF.Key) == null);
    Check("58.11 R1 - the gate and the fact agree on every layer: refused exactly when "
          + "PageLayers.IsVisible is false",
          PageLayers.All(cp).All(l => (RefusesWith(l.Key) != null) == !PageLayers.IsVisible(l)));
    var implicitPage = new NotePage();
    implicitPage.ActiveLayer = 42;                     // names nothing: resolves to the base
    Check("58.11 R1 - a page with no Layers array never refuses (its implicit base layer is "
          + "always visible), whatever ActiveLayer says",
          LayerGate.RefusesNewContent(implicitPage) == null && new NotePage().Layers == null
          && LayerGate.RefusesNewContent(new NotePage()) == null);

    // The message, verbatim - these are the exact strings the status line shows.
    string mHidden = LayerGate.RefusalMessage(cp, new[] { cH }, paste: false);
    string mZero = LayerGate.RefusalMessage(cp, new[] { cZ }, paste: false);
    string mPaste = LayerGate.RefusalMessage(cp, new[] { cH }, paste: true);
    string mPaste2 = LayerGate.RefusalMessage(cp, new[] { cH, cZ }, paste: true);
    Check("58.11 R1 - the message names the layer and says it is hidden, plainly",
          mHidden == "The active layer (Sketch) is hidden, so nothing was added.", mHidden);
    Check("58.11 R1 - a 0% layer is named as 0% (the panel's switch shows it ON, so 'hidden' "
          + "would contradict the panel)",
          mZero == "The active layer (Notes) is at 0% opacity, so nothing was added.", mZero);
    Check("58.11 R1 - paste's messages, one layer and several",
          mPaste == "The layer this pastes onto (Sketch) is hidden, so nothing was pasted."
          && mPaste2 == "The layers this pastes onto are hidden, so nothing was pasted.", mPaste + " / " + mPaste2);
    bool noSymbol = new[] { mHidden, mZero, mPaste, mPaste2, LayerGate.ShowAction, LayerGate.ShownMessage(cp, cH) }
        .All(m => m.All(ch => ch < 0x2000));         // no emoji, no pictographs, no dingbats
    Check("58.11 R1 - no message and no action label carries a symbol or emoji; the action "
          + "is 'Show it'", noSymbol && LayerGate.ShowAction == "Show it");

    // The one-tap action.
    var sH = new Layer { Key = 7, Hidden = true, Opacity = 0.6f };
    var sZ = new Layer { Key = 8, Opacity = 0f };
    var sN = new Layer { Key = 9, Opacity = float.NaN };
    var sB = new Layer { Key = 10, Hidden = true, Opacity = 0f };
    var sF = new Layer { Key = 11, Opacity = 0.4f };
    bool chH = LayerGate.Show(sH), chZ = LayerGate.Show(sZ), chN = LayerGate.Show(sN),
         chB = LayerGate.Show(sB), chF = LayerGate.Show(sF);
    Check("58.11 R1 - Show unhides a hidden layer and KEEPS its own opacity (60%)",
          chH && !sH.Hidden && sH.Opacity == 0.6f && PageLayers.IsVisible(sH));
    Check("58.11 R1 - Show lifts 0% (and not-a-number) to 100%, and a layer both hidden and 0% "
          + "gets both",
          chZ && sZ.Opacity == 1f && chN && sN.Opacity == 1f && chB && !sB.Hidden && sB.Opacity == 1f
          && PageLayers.IsVisible(sZ) && PageLayers.IsVisible(sN) && PageLayers.IsVisible(sB));
    Check("58.11 R1 - Show leaves a visible 40% layer alone and says nothing changed",
          !chF && sF.Opacity == 0.4f && !sF.Hidden);
    Check("58.11 R1 - after Show, the gate lets content in",
          LayerGate.Show(cH) && RefusesWith(cH.Key) == null);
    cH.Hidden = true;

    // Paste keeps each element's own key (18.10): it refuses on the layers
    // those keys resolve to, not on the active one.
    PageLayers.SetActive(cp, cH.Key);                  // active is hidden...
    var pasteOk = LayerGate.RefusesContentOn(cp, new[] { 0, cF.Key });
    var pasteBad = LayerGate.RefusesContentOn(cp, new[] { cZ.Key, 0, cH.Key, cZ.Key, 999 });
    Check("58.11 R1 paste - elements from visible layers paste even while the ACTIVE layer is "
          + "hidden (paste does not use the active layer)", pasteOk.Count == 0);
    Check("58.11 R1 paste - elements from hidden and 0% layers are refused, each layer named "
          + "once, bottom first; an unknown key resolves to the (visible) base layer",
          pasteBad.Count == 2 && ReferenceEquals(pasteBad[0], cH) && ReferenceEquals(pasteBad[1], cZ),
          string.Join(",", pasteBad.Select(l => l.Name)));

    // Which pen presses land on the layer. Oil paint lands on no layer (58.2).
    var vectorPens = Enum.GetValues<PenType>().Where(p => p != PenType.Oil).ToList();
    Check("58.11 R1 pen - every vector pen's press lands on the active layer (so is gated), "
          + "and so does any pen with the ruler, oil included",
          vectorPens.All(p => GestureRules.PenPressLandsOnLayer(p, false))
          && Enum.GetValues<PenType>().All(p => GestureRules.PenPressLandsOnLayer(p, true)));
    Check("58.11 R1 pen - a free oil stroke lands on NO layer (paint is below every layer) and "
          + "is not gated", !GestureRules.PenPressLandsOnLayer(PenType.Oil, false));
}

// ---------------------------------------------------------------------------
// 24. 58.11 RULING R2 - HIDING A LAYER, OR 0%, DROPS THE SELECTION ON IT.
// ---------------------------------------------------------------------------
// LayerGate.DropInvisible is the filter InkSurface.DropInvisibleSelection runs
// over _selected (+_selectedSet), _selShapes (+_selShapeSet) and _selTexts;
// StaysSelected is what it asks of the active shape. That LayersChanged calls
// it on every visibility change was read and compiled.
{
    var rp2 = new NotePage();
    PageLayers.Materialise(rp2);
    var rH = PageLayers.Add(rp2, "H");
    var rZ = PageLayers.Add(rp2, "Z");
    var rL = PageLayers.Add(rp2, "L"); rL.Locked = true;
    var rF = PageLayers.Add(rp2, "F"); rF.Opacity = 0.4f;
    var keys = new[] { 0, rH.Key, rZ.Key, rL.Key, rF.Key };
    var strokes = keys.Select(k => new PenStroke { LayerKey = k, Color = "#1" }).ToList();
    var shapes = keys.Select(k => new ShapeElement { LayerKey = k }).ToList();
    var texts = keys.Select(k => new TextElement { LayerKey = k }).ToList();
    var selSt = strokes.ToList(); var selStSet = new HashSet<PenStroke>(strokes);
    var selSh = shapes.ToList(); var selShSet = new HashSet<ShapeElement>(shapes);
    var selTx = texts.ToList();
    int none = LayerGate.DropInvisible(rp2, selSt, s => s.LayerKey, selStSet)
             + LayerGate.DropInvisible(rp2, selSh, s => s.LayerKey, selShSet)
             + LayerGate.DropInvisible(rp2, selTx, t => t.LayerKey);
    Check("58.11 R2 - with every layer drawn nothing is dropped (a locked layer's selection is "
          + "not this ruling's business)", none == 0 && selSt.Count == 5 && selSh.Count == 5 && selTx.Count == 5);

    rH.Hidden = true;                                  // the Hidden switch
    rZ.Opacity = 0f;                                   // the opacity slider to 0
    int dropped = LayerGate.DropInvisible(rp2, selSt, s => s.LayerKey, selStSet)
                + LayerGate.DropInvisible(rp2, selSh, s => s.LayerKey, selShSet)
                + LayerGate.DropInvisible(rp2, selTx, t => t.LayerKey);
    int[] Keys<T>(List<T> l, Func<T, int> k) => l.Select(k).ToArray();
    int[] expect = { 0, rL.Key, rF.Key };
    Check("58.11 R2 - hiding one layer and taking another to 0% drops exactly their strokes, "
          + "shapes and texts: 6 dropped, the rest kept in order",
          dropped == 6 && Keys(selSt, s => s.LayerKey).SequenceEqual(expect)
          && Keys(selSh, s => s.LayerKey).SequenceEqual(expect) && Keys(selTx, t => t.LayerKey).SequenceEqual(expect),
          $"dropped={dropped}, strokes={string.Join(",", Keys(selSt, s => s.LayerKey))}");
    Check("58.11 R2 - the mirror sets lose the same elements (hit tests read the sets)",
          selStSet.SetEquals(selSt) && selShSet.SetEquals(selSh));
    Check("58.11 R2 - the active shape on the hidden layer, and on the 0% one, may not stay; "
          + "on the locked and 40% layers it may",
          !LayerGate.StaysSelected(rp2, rH.Key) && !LayerGate.StaysSelected(rp2, rZ.Key)
          && LayerGate.StaysSelected(rp2, rL.Key) && LayerGate.StaysSelected(rp2, rF.Key)
          && LayerGate.StaysSelected(rp2, 0));
    Check("58.11 R2 - StaysSelected IS the one fact on every layer",
          keys.All(k => LayerGate.StaysSelected(rp2, k) == PageLayers.IsVisible(rp2, k)));
    rZ.Opacity = 0.1f;                                 // back above 0: nothing reappears on its own
    int again = LayerGate.DropInvisible(rp2, selSt, s => s.LayerKey, selStSet);
    Check("58.11 R2 - showing the layer again does not re-select anything (dropped is dropped)",
          again == 0 && selSt.Count == 3);
}

// ---------------------------------------------------------------------------
// 25. 58.11 RULING R3 - THE ERASER ERASES ONLY VISIBLE INK.
// ---------------------------------------------------------------------------
// LayerPick.Erasable is the test every element loop in InkSurface.EraseAt asks
// (shapes in both modes, strokes in Object mode, strokes in Point mode before
// the style is chosen - so hard, soft, slice and nudge alike). The preview is
// LayerPick.Topmost with no gate. The erase and the preview must agree.
{
    var ep = new NotePage();
    PageLayers.Materialise(ep);
    var eH = PageLayers.Add(ep, "H"); eH.Hidden = true;
    var eZ = PageLayers.Add(ep, "Z"); eZ.Opacity = 0f;
    var eL = PageLayers.Add(ep, "L"); eL.Locked = true;
    var eF = PageLayers.Add(ep, "F"); eF.Opacity = 0.4f;
    var eKeys = new[] { 0, eH.Key, eZ.Key, eL.Key, eF.Key };
    foreach (var k in eKeys)
    {
        var s = new PenStroke { LayerKey = k, Color = "#1" }; s.Points.Add(new StrokePoint(5, 5, 0.5f));
        ep.Strokes.Add(s);
        ep.Shapes.Add(new ShapeElement { LayerKey = k });
    }
    var eplan = DrawPlan.For(ep);
    // The erase, modelled as EraseAt's element walk with its geometry replaced
    // by "touches everything": what survives Erasable is what the pass removes.
    var erased = ep.Strokes.Where(s => LayerPick.Erasable(eplan, s.LayerKey)).Select(s => s.LayerKey).ToList();
    var erasedShapes = ep.Shapes.Where(s => LayerPick.Erasable(eplan, s.LayerKey)).Select(s => s.LayerKey).ToList();
    Check("58.11 R3 - a pass over every layer erases the visible, locked and 40% layers' strokes "
          + "and shapes, and NOTHING on the hidden or 0% layer",
          erased.SequenceEqual(new[] { 0, eL.Key, eF.Key }) && erasedShapes.SequenceEqual(new[] { 0, eL.Key, eF.Key }),
          $"strokes erased on keys {string.Join(",", erased)}");
    bool agree = ep.Strokes.All(s =>
        LayerPick.Erasable(eplan, s.LayerKey) ==
        ReferenceEquals(LayerPick.Topmost(eplan, ep.Strokes, DrawStepKind.Strokes, x => x.LayerKey, null,
                                          x => ReferenceEquals(x, s)), s));
    Check("58.11 R3 - the preview and the erase AGREE on every stroke: the preview can name a "
          + "stroke exactly when the erase would remove it", agree);
    Check("58.11 R3 - Erasable is the plan's drawn fact, which is IsVisible, on every layer",
          eKeys.All(k => LayerPick.Erasable(eplan, k) == eplan.IsDrawn(k) && eplan.IsDrawn(k) == PageLayers.IsVisible(ep, k)));
    var eOne = new NotePage();
    PageLayers.Materialise(eOne)[0].Opacity = 0f;
    Check("58.11 R3 - a ONE-layer page at 0% erases nothing; at 100% everything",
          !LayerPick.Erasable(DrawPlan.For(eOne), 0)
          && LayerPick.Erasable(DrawPlan.For(new NotePage()), 0));
}

// ---------------------------------------------------------------------------
// 26. 58.11 RULING R4 - CTRL+Z MID-STROKE CANCELS THE STROKE, TOUCHES NO HISTORY.
// ---------------------------------------------------------------------------
// GestureRules.StrokeInProgress and OnHistoryKey are what InkSurface.Undo and
// Redo ask first; OilOutcome(Undo/Redo) is what the oil brush does. The
// pen-repair bridge's put-back runs through the REAL UndoRedoManager. What is
// not reachable: OilBrush.Discard (Win2D) and the wiring - read and compiled.
{
    // Every combination of the five inputs.
    var r4Rows = new List<(bool down, bool pen, bool wet, bool adj, bool oil)>();
    foreach (var a in new[] { false, true }) foreach (var b in new[] { false, true })
    foreach (var c in new[] { false, true }) foreach (var d in new[] { false, true })
    foreach (var e in new[] { false, true }) r4Rows.Add((a, b, c, d, e));
    Check("58.11 R4 - a VECTOR stroke under the pen (pointer down, pen gesture, wet ink) is in "
          + "progress, so undo CANCELS it and runs no history",
          GestureRules.OnHistoryKey(GestureRules.StrokeInProgress(true, true, true, false, false)) == HistoryKeyOutcome.CancelStroke);
    Check("58.11 R4 - so is a hold-snapped shape being adjusted, and a live OIL brush",
          GestureRules.StrokeInProgress(true, true, false, true, false)
          && GestureRules.StrokeInProgress(false, false, false, false, true)
          && GestureRules.OnHistoryKey(true) == HistoryKeyOutcome.CancelStroke);
    Check("58.11 R4 - with no stroke under the pen undo runs as always: nothing down; a "
          + "selection drag reached with the pen (gesture re-routed to Mouse); a press with no ink",
          GestureRules.OnHistoryKey(GestureRules.StrokeInProgress(false, false, false, false, false)) == HistoryKeyOutcome.RunHistory
          && !GestureRules.StrokeInProgress(true, false, true, false, false)
          && !GestureRules.StrokeInProgress(true, true, false, false, false));
    Check("58.11 R4 - the whole table: in progress exactly when oil is live, or a pen gesture is "
          + "down holding wet ink or a snapped shape (32 rows)",
          r4Rows.All(w => GestureRules.StrokeInProgress(w.down, w.pen, w.wet, w.adj, w.oil)
                        == (w.oil || (w.down && w.pen && (w.wet || w.adj)))));
    Check("58.11 R4 - undo AND redo mid-oil-stroke DISCARD the scratch (not commit it, which was "
          + "round 2), and end the pen gesture",
          GestureRules.OilOutcome(GestureEnd.Undo) == OilEnd.Discard && GestureRules.OilOutcome(GestureEnd.Redo) == OilEnd.Discard
          && GestureRules.EndsPenGesture(GestureEnd.Undo) && GestureRules.EndsPenGesture(GestureEnd.Redo));

    // History left alone: the pen-repair bridge took the previous stroke's
    // entry off the top at pen-down (TryDiscardTop); cancelling puts it back.
    var hp = new NotePage();
    var mgr = new UndoRedoManager();
    var s1 = new PenStroke { Color = "#1" };
    var s2 = new PenStroke { Color = "#2" };
    var sX = new PenStroke { Color = "#3" };
    mgr.Push(new AddStrokeAction(s1), hp);
    mgr.Push(new AddStrokeAction(sX), hp);
    mgr.Undo(hp);                                      // redo stack: [sX]
    // (a new stroke committed now would clear it; the bridge only follows a
    // commit, so the realistic redo is empty - this proves PutBack keeps it.)
    var before = mgr.History.ToList();
    var top = mgr.PeekUndo;
    bool took = mgr.TryDiscardTop(a => a is AddStrokeAction asa && ReferenceEquals(asa.Stroke, s1));
    hp.Strokes.Remove(s1);                             // RemoveStroke(resume)
    mgr.PutBack(top!, hp);                             // RestoreResumedStroke on the cancel
    Check("58.11 R4 bridge - cancelling a RESUMED stroke puts the previous stroke's own undo "
          + "entry back: the same entry, the stroke on the page, the redo stack intact",
          took && ReferenceEquals(mgr.PeekUndo, top) && mgr.History.SequenceEqual(before)
          && hp.Strokes.Count == 1 && ReferenceEquals(hp.Strokes[0], s1) && mgr.CanRedo
          && mgr.PeekRedo is AddStrokeAction redoTop && ReferenceEquals(redoTop.Stroke, sX),
          $"history {string.Join("/", mgr.History)}; redo {(mgr.CanRedo ? "kept" : "EMPTIED")}");
    mgr.Redo(hp);
    Check("58.11 R4 bridge - and a redo afterwards still brings back what it would have",
          hp.Strokes.Contains(sX) && !mgr.CanRedo);
}

// ---------------------------------------------------------------------------
// 27. 58.11 - EVERY GESTURE END ENDS THE OIL BRUSH, EXACTLY ONCE.
// ---------------------------------------------------------------------------
// The round-2 check's [major]: a second pointer taking over to PAN left the
// brush live - its scratch on top of all ink, and the next VECTOR stroke
// painted as oil. The sequences below are the calls InkSurface makes, in
// order, read from the source (58.11.5); what each call does is the real
// GestureRules.EndOil. What is not reachable: that InkSurface makes those
// calls - read and compiled.
{
    var every = Enum.GetValues<GestureEnd>();
    Check("58.11 end table - every GestureEnd ends a live brush: none answers KeepPainting "
          + $"({every.Length} rows)",
          every.All(w => GestureRules.OilOutcome(w) is OilEnd.Commit or OilEnd.Discard or OilEnd.Drop),
          string.Join(" ", every.Select(w => $"{w}={GestureRules.OilOutcome(w)}")));
    Check("58.11 end table - and each answers what 58.11.5's table says: Undo/Redo discard, "
          + "device loss drops, the other nine commit",
          every.All(w => GestureRules.OilOutcome(w) == (w switch
          {
              GestureEnd.Undo or GestureEnd.Redo => OilEnd.Discard,
              GestureEnd.DeviceLoss => OilEnd.Drop,
              _ => OilEnd.Commit,
          })) && every.Length == 12);
    Check("58.11 end table - the pen gesture that carried the brush is ended by every end "
          + "except the three that ARE that gesture ending (lift, pointer lost, reset)",
          every.All(w => GestureRules.EndsPenGesture(w)
                         == !(w is GestureEnd.Lift or GestureEnd.PointerLost or GestureEnd.Reset)));
    Check("58.11 end table - a brush that is not live has nothing to end, whatever the reason",
          every.All(w => GestureRules.EndOil(false, w) == OilEnd.Nothing));

    // THE PAN TAKEOVER, by each of its three routes.
    Check("58.11 pan takeover - all three routes are TakeoverPan: a middle-button press (a "
          + "literal at its site), the mouse in Grab mode, the Pan tool",
          GestureRules.MouseModeTakeover(MouseMode.Grab) == GestureEnd.TakeoverPan
          && GestureRules.TakeoverFor(ToolType.Pan) == GestureEnd.TakeoverPan);
    Check("58.11 takeovers - an eraser press is TakeoverErase, every other tool and mouse mode "
          + "TakeoverOther",
          GestureRules.TakeoverFor(ToolType.Eraser) == GestureEnd.TakeoverErase
          && Enum.GetValues<ToolType>().Where(t => t is not (ToolType.Pan or ToolType.Eraser))
                 .All(t => GestureRules.TakeoverFor(t) == GestureEnd.TakeoverOther)
          && Enum.GetValues<MouseMode>().Where(m => m != MouseMode.Grab)
                 .All(m => GestureRules.MouseModeTakeover(m) == GestureEnd.TakeoverOther));

    // Replays one gesture's sequence of end sites against a brush.
    (int commits, int discards, int drops, bool liveAfter, bool penEnded) Run(params GestureEnd[] sites)
    {
        bool live = true; int c = 0, d = 0, x = 0; bool pen = false;
        foreach (var w in sites)
        {
            var o = GestureRules.EndOil(live, w);
            if (o is OilEnd.Nothing or OilEnd.KeepPainting) continue;
            if (o == OilEnd.Commit) c++; else if (o == OilEnd.Discard) d++; else if (o == OilEnd.Drop) x++;
            live = false;
            pen |= GestureRules.EndsPenGesture(w);
        }
        return (c, d, x, live, pen);
    }
    string Show((int c, int d, int x, bool live, bool pen) r) => $"commits={r.c} discards={r.d} drops={r.x} live={r.live} penEnded={r.pen}";
    // Middle-button / Grab / Pan tool: TakeOverGesture at the press, then the
    // pan's release takes CommitGesture's _mousePanning early return, which
    // calls EndOilGesture(Reset); then the pen's own lift is ignored (pointer
    // id mismatch) - no site.
    var pan = Run(GestureEnd.TakeoverPan, GestureEnd.Reset);
    Check("58.11 pan takeover - the press COMMITS the brush once, ends the pen gesture, and the "
          + "pan's release finds nothing left to end: no brush live afterwards",
          pan.commits == 1 && pan.discards == 0 && !pan.liveAfter && pan.penEnded, Show(pan));
    var erase = Run(GestureEnd.TakeoverErase, GestureEnd.Lift, GestureEnd.Reset);
    Check("58.11 erase takeover - committed at the eraser's PRESS, before its first EraseAt, so "
          + "its release has no scratch to commit back over the rubbed area",
          erase.commits == 1 && !erase.liveAfter && erase.penEnded, Show(erase));
    var tool = Run(GestureEnd.ToolSwitch, GestureEnd.Reset);
    Check("58.11 tool switch - SetTool commits once and ends the pen gesture (its ResetGesture "
          + "is the second, empty, end)", tool.commits == 1 && !tool.liveAfter && tool.penEnded, Show(tool));
    var undo = Run(GestureEnd.Undo, GestureEnd.Reset);
    Check("58.11 undo - one discard, no commit (nothing reaches the undo stack)",
          undo.discards == 1 && undo.commits == 0 && !undo.liveAfter && undo.penEnded, Show(undo));
    var lift = Run(GestureEnd.Lift, GestureEnd.Reset);
    Check("58.11 lift - one commit, and the pen gesture is not ended twice (the lift IS it ending)",
          lift.commits == 1 && !lift.liveAfter && !lift.penEnded, Show(lift));
    var dev = Run(GestureEnd.DeviceLoss, GestureEnd.Reset);
    Check("58.11 device loss - dropped once, nothing committed", dev.drops == 1 && dev.commits == 0 && !dev.liveAfter, Show(dev));
    bool allOnce = every.All(w => { var r = Run(w, w, GestureEnd.Reset, GestureEnd.Lift); return r.commits + r.discards + r.drops == 1 && !r.liveAfter; });
    Check("58.11 exactly once - for every end, however many end sites follow it, the brush is "
          + "ended by exactly one of them", allOnce);
}

// ---------------------------------------------------------------------------
// 28. 58.11 - SHAPE RECOGNITION'S HOLD IGNORES AN OIL GESTURE ENTIRELY.
// ---------------------------------------------------------------------------
// InkSurface.HoldTick asks GestureRules.HoldMaySnap first. The round-2 check:
// holding still mid-oil-stroke turned the wet points into a vector shape
// while the brush stayed live, and the lift committed BOTH the shape and the
// raster; design finding O2 - the next gesture with any pen was drawn as oil.
{
    var combos = new List<bool[]>();
    for (int m = 0; m < 32; m++) combos.Add(Enumerable.Range(0, 5).Select(b => (m & (1 << b)) != 0).ToArray());
    Check("58.11 hold - with the oil brush live the hold NEVER snaps, whatever the other five "
          + "inputs (32 rows)",
          combos.All(c => !GestureRules.HoldMaySnap(c[0], c[1], c[2], c[3], c[4], oilActive: true)));
    Check("58.11 hold - control: a vector stroke with recognition on, pen gesture, no shape yet, "
          + "no ruler and wet ink DOES snap",
          GestureRules.HoldMaySnap(true, true, false, false, true, oilActive: false));
    Check("58.11 hold - and the vector rules are as before: recognition off, a ruler, a shape "
          + "already snapped, no wet ink or a non-pen gesture each stop it",
          !GestureRules.HoldMaySnap(false, true, false, false, true, false)
          && !GestureRules.HoldMaySnap(true, true, false, true, true, false)
          && !GestureRules.HoldMaySnap(true, true, true, false, true, false)
          && !GestureRules.HoldMaySnap(true, true, false, false, false, false)
          && !GestureRules.HoldMaySnap(true, false, false, false, true, false));
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
