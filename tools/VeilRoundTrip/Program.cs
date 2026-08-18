// CONCEPTS-REF 16.7 item 1, PROVED.
//
//   "It is a RENDER-TIME effect and must never touch stored colour... The page's
//    own colours have to come back exactly when the attachment is deselected, and
//    a user who saves in this state must not find their drawing greyed on reload.
//    This is the one way to turn a visual nicety into data loss."
//
// A claim about persistence is settled by persisting, so this does exactly the
// sequence the promise is about - SELECT, SAVE, DESELECT, RELOAD, DIFF - and does
// it against the real things:
//
//   * the real models          (src/Quill/Models/NoteModels.cs, compiled in)
//   * the real serialiser      (src/Quill/Services/LibraryStore.cs, compiled in)
//   * the real selection state (src/Quill/Services/SelectionSubject.cs)
//   * the real fade            (Veil.g.cs, extracted VERBATIM from InkSurface.cs
//                               by tools/selection_present_check.py, which fails
//                               if the extraction ever stops matching)
//   * the real easing          (src/Quill/Helpers/Motion.cs - 190 / 130 on the
//                               (0.12,0.9) -> (0.2,1.0) curve)
//
// The one thing it cannot be is the app: InkSurface is a WinUI control and Win2D
// wants a device, so the DRAW CALL is stood in for by the statement the draw path
// actually executes -
//
//     var color = Veil(ColorUtil.Parse(s.Color), exempt);
//
// - run over every stored colour on the page, at every frame of the fade. That is
// the whole of the mechanism; tools/selection_present_check.py separately proves
// by dataflow that there is no OTHER path out of Veil, and that no veiled value
// ever reaches ColorUtil.ToHex, which is the only Color -> string bridge the app
// has and therefore the only door from a drawn colour to a stored one.
//
// ISOLATION. LibraryStore reads QUILL_DATA_FOLDER before anything else and an
// isolated instance touches nothing outside it (LibraryStore.EnvFolder's own
// remarks). It is set before the first call here, and the run ABORTS rather than
// continues if the resolved path is not inside the temp folder this process made.
// Nothing here can reach the user's Documents\Quill\library.json.

using System.Text.Json;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Quill.Tools;
using Windows.UI;

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
string scratch = Path.Combine(Path.GetTempPath(), "quill-veil-roundtrip", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("QUILL_DATA_FOLDER", scratch);

string file = LibraryStore.FilePath;
if (!file.StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("ABORT: LibraryStore resolved to " + file);
    Console.Error.WriteLine("       which is not inside " + scratch);
    Console.Error.WriteLine("       Refusing to run: this test must never touch a real library.");
    return 2;
}
Check("the library under test is isolated in a temp folder", true, file);

// ---------------------------------------------------------------------------
// 1. A page with colours worth losing.
// ---------------------------------------------------------------------------
// Deliberately includes a colour that is ALREADY near the veil grey but not equal
// to it, so "the file no longer contains #8E8E8E" is a real test rather than one
// that would pass on a page which never had a grey to begin with.
string[] inkHexes = { "#2B6CB0", "#D97757", "#141413", "#FAF9F5", "#8F8F8F", "#00A37A" };

var page = new NotePage { Name = "Veil round trip", Background = "#FFFDF7" };
for (int i = 0; i < inkHexes.Length; i++)
{
    page.Strokes.Add(new PenStroke
    {
        Color = inkHexes[i],
        Size = 2f + i,
        Sens = 0.25f * (i % 4),
        Opacity = 0.4f + 0.1f * i,
        Pen = (PenType)(i % 3),
        Points = { new StrokePoint(10 + i, 20 + i, 0.5f), new StrokePoint(60 + i, 80 + i, 0.7f) },
    });
}
var attachment = new ShapeElement
{
    Kind = ShapeKind.Image,
    ImagePath = Path.Combine(scratch, "assets", "photo.png"),
    X = 120, Y = 140, W = 320, H = 240,
    Color = "#3A3A3A",
};
page.Shapes.Add(attachment);
page.Shapes.Add(new ShapeElement { Kind = ShapeKind.Rect, X = 10, Y = 10, W = 90, H = 60, Color = "#B4530A" });
page.Texts.Add(new TextElement
{
    X = 40, Y = 400, Width = 300,
    Rtf = @"{\rtf1\ansi{\colortbl ;\red20\green20\blue19;}\cf1 handwriting under the photo\par}",
});

var lib = new Library();
var nb = new Notebook { Name = "Proof", Color = "#D97757" };
var sec = new Section { Name = "Section" };
sec.Pages.Add(page);
nb.Sections.Add(sec);
lib.Notebooks.Add(nb);

// A census of every stored colour on the page: what must survive, byte for byte.
static Dictionary<string, string> Census(NotePage p)
{
    var d = new Dictionary<string, string> { ["page.Background"] = p.Background };
    for (int i = 0; i < p.Strokes.Count; i++) d[$"stroke[{i}].Color"] = p.Strokes[i].Color;
    for (int i = 0; i < p.Shapes.Count; i++) d[$"shape[{i}].Color"] = p.Shapes[i].Color;
    for (int i = 0; i < p.Texts.Count; i++) d[$"text[{i}].Rtf"] = p.Texts[i].Rtf;
    return d;
}

// ---------------------------------------------------------------------------
// 2. BASELINE: save, and remember what is on disk.
// ---------------------------------------------------------------------------
LibraryStore.EnableSaving();
LibraryStore.Save(lib);
LibraryStore.Flush();

var baseline = Census(page);
string baselineJson = File.ReadAllText(file);
Check("a baseline library was written", File.Exists(file), $"{baselineJson.Length} bytes");
Check("the baseline holds every colour the page was built with",
      inkHexes.All(h => baselineJson.Contains(h, StringComparison.OrdinalIgnoreCase)));
Check("the baseline does not already contain the veil grey (so its absence "
      + "later means something)",
      !baselineJson.Contains("8E8E8E", StringComparison.OrdinalIgnoreCase));

// ---------------------------------------------------------------------------
// 3. SELECT THE ATTACHMENT - through the real SelectionState.
// ---------------------------------------------------------------------------
// The same subject InkSurface.PublishSelection builds for a single image: no pen
// size, no stabiliser, opacity kept, not recolourable, and the one that matters
// here - FadesPage.
SelectionState.Set(new SelectionSubject
{
    Kind = SubjectKind.Attachment,
    Count = 1,
    HasPenSize = false,
    HasStability = false,
    HasOpacity = true,
    CanRecolour = false,
    FadesPage = true,
});
Check("16.3 - the selected attachment greys size, stability and colour and keeps opacity",
      SelectionState.Current is { HasPenSize: false, HasStability: false,
                                  HasOpacity: true, CanRecolour: false });
Check("16.7 - and it is the subject that fades the page", SelectionState.Current.FadesPage);

// ---------------------------------------------------------------------------
// 4. RUN THE FADE, frame by frame, over every stored colour.
// ---------------------------------------------------------------------------
var veil = new RealVeil();
var seen = new List<Color>();
double alphaDrift = 0;
Color finalGrey = default, attachmentDrawn = default;

double t = 0;
for (int frame = 0; frame < 400 && t < 1; frame++)
{
    t = Motion.Step(t, 1, 16.7, /* Motion.OpenMs */ 190);
    veil.Drive(t, false);
    foreach (var s in page.Strokes)
    {
        // THE STATEMENT THE DRAW PATH RUNS. A local in, a local out.
        var color = Veiled(veil, s.Color, exempt: false);
        seen.Add(color);
        alphaDrift = Math.Max(alphaDrift, Math.Abs(color.A - ColorUtil.Parse(s.Color).A));
    }
    // 16.7 item 3: the attachment is the subject and holds full contrast.
    attachmentDrawn = Veiled(veil, attachment.Color, exempt: true);
}
finalGrey = Veiled(veil, page.Strokes[0].Color, exempt: false);

Check("16.7 item 2 - the fade reaches full grey on Quill's own 190 ms",
      Math.Abs(t - 1) < 1e-9, $"t = {t}");
Check("16.7 - fully faded ink is exactly #8E8E8E",
      finalGrey.R == 0x8E && finalGrey.G == 0x8E && finalGrey.B == 0x8E,
      $"#{finalGrey.R:X2}{finalGrey.G:X2}{finalGrey.B:X2}");
Check("16.7 - ALPHA is never touched, so a translucent stroke does not go opaque "
      + "as it greys",
      alphaDrift == 0, $"max drift {alphaDrift}");
Check("16.7 item 3 - the attachment itself does not fade",
      attachmentDrawn == ColorUtil.Parse(attachment.Color),
      $"#{attachmentDrawn.R:X2}{attachmentDrawn.G:X2}{attachmentDrawn.B:X2}");

// ---------------------------------------------------------------------------
// 5. SAVE WHILE FADED. This is the frame the promise is about.
// ---------------------------------------------------------------------------
LibraryStore.Save(lib);
LibraryStore.Flush();
string fadedJson = File.ReadAllText(file);

Check("16.7 item 1 - a save taken at FULL FADE is byte-identical to the baseline",
      fadedJson == baselineJson,
      fadedJson == baselineJson ? $"{fadedJson.Length} bytes, identical"
                                : FirstDiff(baselineJson, fadedJson));
Check("16.7 item 1 - the veil grey appears nowhere in the saved library",
      !fadedJson.Contains("8E8E8E", StringComparison.OrdinalIgnoreCase));
Check("16.7 item 1 - the model's own colour strings are untouched by the fade",
      Census(page).SequenceEqual(baseline));

// ---------------------------------------------------------------------------
// 6. DESELECT, and fade back.
// ---------------------------------------------------------------------------
SelectionState.Clear();
Check("deselecting leaves nothing selected", !SelectionState.Current.Any);
Check("deselecting stops the page fading", !SelectionState.Current.FadesPage);

double back = t;
for (int frame = 0; frame < 400 && back > 0; frame++)
{
    back = Motion.Step(back, 0, 16.7, /* Motion.CloseMs */ 130);
    veil.Drive(back, false);
}
Check("16.7 item 2 - the fade runs BACK to zero on Quill's own 130 ms",
      back == 0, $"t = {back}");

// The last frame is the one that puts the page's colours back, so it is the one
// worth being exact about: at t == 0, Veil is the identity on every colour the
// page carries - the same object it was handed, not a re-mix that happens to be
// close.
bool identity = true;
foreach (var hex in inkHexes.Concat(new[] { page.Background, attachment.Color }))
{
    var want = ColorUtil.Parse(hex);
    var got = Veiled(veil, hex, exempt: false);
    if (got != want) identity = false;
}
Check("16.7 - at the end of the fade back, Veil is the IDENTITY on every colour "
      + "the page carries", identity);

// ---------------------------------------------------------------------------
// 7. SAVE AGAIN AND RELOAD FROM DISK.
// ---------------------------------------------------------------------------
LibraryStore.Save(lib);
LibraryStore.Flush();

var reloaded = LibraryStore.Load();
Check("the library reloads", !LibraryStore.LoadFailed, LibraryStore.LoadError ?? "no error");

var back2 = reloaded.Notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Pages.FirstOrDefault();
Check("the page came back", back2 != null);
if (back2 != null)
{
    var after = Census(back2);
    var drift = baseline.Where(kv => !after.TryGetValue(kv.Key, out var v) || v != kv.Value)
                        .Select(kv => $"{kv.Key}: {kv.Value} -> {(after.TryGetValue(kv.Key, out var v2) ? v2 : "GONE")}")
                        .ToList();
    Check("16.7 item 1 - EVERY stored colour survives select -> save -> deselect "
          + "-> reload, byte for byte",
          drift.Count == 0 && after.Count == baseline.Count,
          drift.Count == 0 ? $"{baseline.Count} colours compared, all identical"
                           : string.Join("; ", drift));
}

// ---------------------------------------------------------------------------
// 8. AN EXPORT IS NEVER FADED.
// ---------------------------------------------------------------------------
veil.Drive(1, exportChromeless: true);
bool exportClean = inkHexes.All(h => Veiled(veil, h, exempt: false) == ColorUtil.Parse(h));
Check("16.7 - a capture taken while an attachment is selected comes out in the "
      + "page's own colours", exportClean && !veil.IsVeiling);

// ---------------------------------------------------------------------------
// 9. WITH TWO ATTACHMENTS, both selected ones hold contrast.
// ---------------------------------------------------------------------------
// The exemption InkSurface asks is "is this element part of the selection?", not
// "is this element an image?", so the answer falls straight out: a second,
// UNSELECTED attachment fades with the rest of the page.
veil.Drive(1, exportChromeless: false);
var secondAttachment = new ShapeElement { Kind = ShapeKind.Image, Color = "#3A3A3A" };
var selectedDrawn = Veiled(veil, attachment.Color, exempt: true);
var unselectedDrawn = Veiled(veil, secondAttachment.Color, exempt: false);
Check("two attachments: the SELECTED one holds contrast",
      selectedDrawn == ColorUtil.Parse(attachment.Color));
Check("two attachments: the UNSELECTED one fades with the page",
      unselectedDrawn.R == 0x8E && unselectedDrawn.G == 0x8E && unselectedDrawn.B == 0x8E);

// ---------------------------------------------------------------------------
foreach (var l in log) Console.WriteLine(l);
Console.WriteLine();
try { Directory.Delete(scratch, recursive: true); } catch { }

if (failures > 0)
{
    Console.WriteLine($"FAILED {failures} check(s).");
    return 1;
}
Console.WriteLine($"OK - {log.Count} checks held. 16.7 item 1 is a measurement, not a claim.");
return 0;

// ---------------------------------------------------------------------------

static Color Veiled(RealVeil v, string storedHex, bool exempt) =>
    v.Apply(ColorUtil.Parse(storedHex), exempt);

static string FirstDiff(string a, string b)
{
    int i = 0;
    while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
    return $"first difference at byte {i}: "
         + $"...{a.Substring(Math.Max(0, i - 30), Math.Min(60, a.Length - Math.Max(0, i - 30)))}..."
         + $" vs ...{b.Substring(Math.Max(0, i - 30), Math.Min(60, b.Length - Math.Max(0, i - 30)))}...";
}
