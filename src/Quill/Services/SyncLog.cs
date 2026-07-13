using System.Text;
using System.Text.Json;
using Quill.Models;

namespace Quill.Services;

/// <summary>
/// Staged collaboration (docs/COLLABORATION-PLAN.md):
/// Stage 0 — operation log: every save diffs the library against an in-memory
/// shadow and appends element-level upsert/delete ops to a per-device
/// oplog.[deviceId].jsonl next to the library (crash recovery + change history).
/// Stage 1 — synced-folder sharing: on a timer, ops appended by OTHER devices
/// (their oplog files arriving via OneDrive/Dropbox/any synced folder) are
/// applied to the local library, element-level last-writer-wins by apply order.
/// Per-actor read cursors live in %LOCALAPPDATA% so they never sync.
/// </summary>
public static class SyncLog
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private sealed class Op
    {
        public string K { get; set; } = "";     // nb | sec | pg | st | sh | tx | cm
        public Guid Id { get; set; }
        public Guid Parent { get; set; }        // section->notebook, page->section, element->page
        public string? J { get; set; }          // full entity JSON (null = delete)
        public long N { get; set; }             // per-device lamport
        public string A { get; set; } = "";     // actor (device id)
        public long Ts { get; set; }
    }

    private sealed class Cursors
    {
        public Dictionary<string, long> Offsets { get; set; } = new();   // actor -> byte offset
        public long Lamport { get; set; }
    }

    // entity-key -> content hash; what this device believes was last persisted
    private static readonly Dictionary<string, long> _shadow = new();
    private static Cursors _cursors = new();
    private static string? _deviceId;
    private static readonly object _lock = new();

    private static string CursorPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quill", "synccursors.json");

    public static string DeviceId
    {
        get
        {
            if (_deviceId != null) return _deviceId;
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quill", "deviceid.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path)) _deviceId = File.ReadAllText(path).Trim();
                if (string.IsNullOrEmpty(_deviceId))
                {
                    _deviceId = Guid.NewGuid().ToString("N")[..12];
                    File.WriteAllText(path, _deviceId);
                }
            }
            catch { _deviceId ??= "local"; }
            return _deviceId!;
        }
    }

    private static string OplogPath => Path.Combine(LibraryStore.Dir, $"oplog.{DeviceId}.jsonl");

    private static long Fnv(string s)
    {
        unchecked
        {
            long h = (long)14695981039346656037UL;
            foreach (var c in s) { h ^= c; h *= 1099511628211; }
            return h;
        }
    }

    // page metadata WITHOUT elements or per-device view state
    private static string PageMetaJson(NotePage p) => JsonSerializer.Serialize(new
    { p.Id, p.Name, p.CreatedTicks, p.Background, p.Grid, p.GridSpacing, p.PenRowVisible, p.Width, p.Height, p.AudioFile, p.AudioStartTicks }, Opts);

    private static string NbMetaJson(Notebook n) => JsonSerializer.Serialize(new { n.Id, n.Name, n.Color, n.CoverEmoji, n.Folder }, Opts);
    private static string SecMetaJson(Section s) => JsonSerializer.Serialize(new { s.Id, s.Name }, Opts);

    private static IEnumerable<(string Key, string Kind, Guid Id, Guid Parent, string Json)> Entities(Library lib)
    {
        foreach (var nb in lib.Notebooks)
        {
            yield return ($"nb:{nb.Id}", "nb", nb.Id, Guid.Empty, NbMetaJson(nb));
            foreach (var sec in nb.Sections)
            {
                yield return ($"sec:{sec.Id}", "sec", sec.Id, nb.Id, SecMetaJson(sec));
                foreach (var pg in sec.Pages)
                {
                    yield return ($"pg:{pg.Id}", "pg", pg.Id, sec.Id, PageMetaJson(pg));
                    foreach (var s in pg.Strokes) yield return ($"st:{s.Id}", "st", s.Id, pg.Id, JsonSerializer.Serialize(s, Opts));
                    foreach (var s in pg.Shapes) yield return ($"sh:{s.Id}", "sh", s.Id, pg.Id, JsonSerializer.Serialize(s, Opts));
                    foreach (var t in pg.Texts) yield return ($"tx:{t.Id}", "tx", t.Id, pg.Id, JsonSerializer.Serialize(t, Opts));
                    foreach (var c in pg.Comments) yield return ($"cm:{c.Id}", "cm", c.Id, pg.Id, JsonSerializer.Serialize(c, Opts));
                }
            }
        }
    }

    /// <summary>Prime the shadow from the freshly loaded library WITHOUT
    /// emitting ops — only edits made after launch are logged.</summary>
    public static void Initialize(Library lib)
    {
        lock (_lock)
        {
            _shadow.Clear();
            foreach (var (key, _, _, _, json) in Entities(lib)) _shadow[key] = Fnv(json);
            try
            {
                if (File.Exists(CursorPath))
                    _cursors = JsonSerializer.Deserialize<Cursors>(File.ReadAllText(CursorPath)) ?? new Cursors();
            }
            catch { _cursors = new Cursors(); }
        }
    }

    /// <summary>Stage 0: called after each library save — append ops for
    /// everything that changed since the previous save.</summary>
    public static void OnSaved(Library lib)
    {
        lock (_lock)
        {
            var seen = new HashSet<string>();
            var sb = new StringBuilder();
            long ts = DateTime.UtcNow.Ticks;
            foreach (var (key, kind, id, parent, json) in Entities(lib))
            {
                seen.Add(key);
                long h = Fnv(json);
                if (_shadow.TryGetValue(key, out long prev) && prev == h) continue;
                _shadow[key] = h;
                sb.AppendLine(JsonSerializer.Serialize(new Op { K = kind, Id = id, Parent = parent, J = json, N = ++_cursors.Lamport, A = DeviceId, Ts = ts }, Opts));
            }
            foreach (var gone in _shadow.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var parts = gone.Split(':');
                _shadow.Remove(gone);
                sb.AppendLine(JsonSerializer.Serialize(new Op { K = parts[0], Id = Guid.Parse(parts[1]), J = null, N = ++_cursors.Lamport, A = DeviceId, Ts = ts }, Opts));
            }
            if (sb.Length == 0) return;
            try
            {
                File.AppendAllText(OplogPath, sb.ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(CursorPath)!);
                File.WriteAllText(CursorPath, JsonSerializer.Serialize(_cursors, Opts));
            }
            catch { }
        }
    }

    /// <summary>Stage 1: apply unseen ops from OTHER devices' logs in the same
    /// (synced) folder. Returns the ids of pages that changed.</summary>
    public static HashSet<Guid> MergeForeign(Library lib)
    {
        var changedPages = new HashSet<Guid>();
        lock (_lock)
        {
            List<string> files;
            try { files = Directory.GetFiles(LibraryStore.Dir, "oplog.*.jsonl").ToList(); }
            catch { return changedPages; }
            bool cursorsDirty = false;
            foreach (var file in files)
            {
                var actor = Path.GetFileNameWithoutExtension(file).Split('.').Last();
                if (actor == DeviceId) continue;
                long offset = _cursors.Offsets.TryGetValue(actor, out var o) ? o : 0;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length <= offset) continue;
                    fs.Seek(offset, SeekOrigin.Begin);
                    using var rd = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = rd.ReadLine()) != null)
                    {
                        try
                        {
                            var op = JsonSerializer.Deserialize<Op>(line);
                            if (op != null && Apply(lib, op) is { } pgId) changedPages.Add(pgId);
                        }
                        catch { }
                    }
                    _cursors.Offsets[actor] = fs.Length;
                    cursorsDirty = true;
                }
                catch { }
            }
            if (cursorsDirty)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CursorPath)!);
                    File.WriteAllText(CursorPath, JsonSerializer.Serialize(_cursors, Opts));
                }
                catch { }
                // remote state is now local state: refresh the shadow so the next
                // save doesn't re-emit everything just merged
                _shadow.Clear();
                foreach (var (key, _, _, _, json) in Entities(lib)) _shadow[key] = Fnv(json);
            }
        }
        return changedPages;
    }

    private static NotePage? FindPage(Library lib, Guid id) =>
        lib.Notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Pages).FirstOrDefault(p => p.Id == id);

    private static Guid? Apply(Library lib, Op op)
    {
        switch (op.K)
        {
            case "nb":
            {
                var nb = lib.Notebooks.FirstOrDefault(n => n.Id == op.Id);
                if (op.J == null) { if (nb != null) lib.Notebooks.Remove(nb); return null; }
                var meta = JsonSerializer.Deserialize<Notebook>(op.J);
                if (meta == null) return null;
                if (nb == null) { nb = new Notebook { Id = op.Id }; nb.Sections.Clear(); lib.Notebooks.Add(nb); }
                nb.Name = meta.Name; nb.Color = meta.Color; nb.CoverEmoji = meta.CoverEmoji; nb.Folder = meta.Folder;
                return null;
            }
            case "sec":
            {
                var nb = lib.Notebooks.FirstOrDefault(n => n.Id == op.Parent) ?? lib.Notebooks.FirstOrDefault();
                if (nb == null) return null;
                var sec = lib.Notebooks.SelectMany(n => n.Sections).FirstOrDefault(s => s.Id == op.Id);
                if (op.J == null)
                {
                    if (sec != null) foreach (var n in lib.Notebooks) n.Sections.Remove(sec);
                    return null;
                }
                var meta = JsonSerializer.Deserialize<Section>(op.J);
                if (meta == null) return null;
                if (sec == null) { sec = new Section { Id = op.Id }; sec.Pages.Clear(); nb.Sections.Add(sec); }
                sec.Name = meta.Name;
                return null;
            }
            case "pg":
            {
                var pg = FindPage(lib, op.Id);
                if (op.J == null)
                {
                    if (pg != null) foreach (var s in lib.Notebooks.SelectMany(n => n.Sections)) s.Pages.Remove(pg);
                    return op.Id;
                }
                var meta = JsonSerializer.Deserialize<NotePage>(op.J);
                if (meta == null) return null;
                if (pg == null)
                {
                    var sec = lib.Notebooks.SelectMany(n => n.Sections).FirstOrDefault(s => s.Id == op.Parent)
                              ?? lib.Notebooks.SelectMany(n => n.Sections).FirstOrDefault();
                    if (sec == null) return null;
                    pg = new NotePage { Id = op.Id };
                    sec.Pages.Add(pg);
                }
                pg.Name = meta.Name; pg.Background = meta.Background; pg.Grid = meta.Grid;
                pg.GridSpacing = meta.GridSpacing; pg.PenRowVisible = meta.PenRowVisible;
                pg.Width = meta.Width; pg.Height = meta.Height;
                pg.AudioFile = meta.AudioFile; pg.AudioStartTicks = meta.AudioStartTicks;
                return op.Id;
            }
            case "st": case "sh": case "tx": case "cm":
            {
                var pg = FindPage(lib, op.Parent);
                if (pg == null) return null;
                switch (op.K)
                {
                    case "st":
                        pg.Strokes.RemoveAll(s => s.Id == op.Id);
                        if (op.J != null && JsonSerializer.Deserialize<PenStroke>(op.J) is { } st)
                        {
                            pg.Strokes.Add(st);
                            pg.Strokes.Sort((a, b) => a.CreatedTicks.CompareTo(b.CreatedTicks));
                        }
                        break;
                    case "sh":
                        pg.Shapes.RemoveAll(s => s.Id == op.Id);
                        if (op.J != null && JsonSerializer.Deserialize<ShapeElement>(op.J) is { } sh) pg.Shapes.Add(sh);
                        break;
                    case "tx":
                        pg.Texts.RemoveAll(t => t.Id == op.Id);
                        if (op.J != null && JsonSerializer.Deserialize<TextElement>(op.J) is { } tx) pg.Texts.Add(tx);
                        break;
                    case "cm":
                        pg.Comments.RemoveAll(c => c.Id == op.Id);
                        if (op.J != null && JsonSerializer.Deserialize<PageComment>(op.J) is { } cm) pg.Comments.Add(cm);
                        break;
                }
                return op.Parent;
            }
        }
        return null;
    }
}
