using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Compaction — a log only ever needs the LATEST op per element, so once a log
/// passes a size threshold it is rewritten keeping one op per id (tombstones
/// included: dropping them would resurrect deleted elements on peers that never
/// saw the delete). Surviving ops keep their original lamport order, so replay
/// semantics are unchanged. The rewrite goes to a temp file in the same
/// directory and is swapped in with File.Replace — the live log is never
/// truncated in place. Each rewrite bumps a generation counter carried in a
/// header line; peers whose stored byte offset predates that generation reset to
/// offset 0 and filter by the per-actor lamport high-water mark instead.
/// </summary>
public static class SyncLog
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private const string HeaderKind = "_h";
    // Compact once a log passes this; checked after an append, not every save.
    private const long CompactThresholdBytes = 2L * 1024 * 1024;

    private sealed class Op
    {
        public string K { get; set; } = "";     // nb | sec | pg | st | sh | tx | cm | _h (header)
        public Guid Id { get; set; }
        public Guid Parent { get; set; }        // section->notebook, page->section, element->page
        public string? J { get; set; }          // full entity JSON (null = delete)
        public long N { get; set; }             // per-device lamport
        public string A { get; set; } = "";     // actor (device id)
        public long Ts { get; set; }
        // header only; omitted from ordinary ops so compaction doesn't inflate every line
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long G { get; set; }             // compaction generation
    }

    private sealed class Cursors
    {
        public Dictionary<string, long> Offsets { get; set; } = new();   // actor -> byte offset
        // actor -> highest lamport applied from that actor. Survives compaction of
        // the peer's log, which byte offsets do not.
        public Dictionary<string, long> Seen { get; set; } = new();
        // actor -> compaction generation the stored offset belongs to
        public Dictionary<string, long> Gens { get; set; } = new();
        public long Lamport { get; set; }
    }

    // don't re-attempt compaction on every save once a log is legitimately large
    private static long _compactFloor = CompactThresholdBytes;

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
            CompactIfNeeded();
        }
    }

    /// <summary>Reads the generation from a log's header line (0 = never compacted).
    /// Leaves the stream position undefined; callers seek afterwards.</summary>
    private static long ReadGeneration(FileStream fs)
    {
        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            using var rd = new StreamReader(fs, Encoding.UTF8, false, 1024, leaveOpen: true);
            var first = rd.ReadLine();
            if (string.IsNullOrEmpty(first)) return 0;
            var op = JsonSerializer.Deserialize<Op>(first);
            return op != null && op.K == HeaderKind ? op.G : 0;
        }
        catch { return 0; }
    }

    /// <summary>Rewrite our own log keeping only the latest op per element once it
    /// grows past the threshold. Crash-safe: temp file in the same directory,
    /// flushed to disk, then atomically swapped in. The live log is never opened
    /// for truncation, so an interrupted compaction loses nothing.</summary>
    private static void CompactIfNeeded()
    {
        var path = OplogPath;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < _compactFloor) return;
        }
        catch { return; }

        long gen;
        var survivors = new Dictionary<string, Op>();
        int total = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            gen = ReadGeneration(fs);
            fs.Seek(0, SeekOrigin.Begin);
            using var rd = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                Op? op;
                try { op = JsonSerializer.Deserialize<Op>(line); } catch { continue; }
                if (op == null || op.K == HeaderKind) continue;
                total++;
                survivors[$"{op.K}:{op.Id}"] = op;   // later op wins; tombstones are kept
            }
        }
        catch { return; }

        // nothing to collapse — back off so we don't rescan a big log every save
        if (survivors.Count >= total)
        {
            try { _compactFloor = new FileInfo(path).Length + CompactThresholdBytes; } catch { }
            return;
        }

        var tmp = path + ".tmp";
        try
        {
            using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(outFs, new UTF8Encoding(false)))
            {
                w.NewLine = "\r\n";
                w.WriteLine(JsonSerializer.Serialize(new Op { K = HeaderKind, A = DeviceId, G = gen + 1, Ts = DateTime.UtcNow.Ticks }, Opts));
                // original lamport order preserved, so replay semantics are unchanged
                foreach (var op in survivors.Values.OrderBy(o => o.N))
                    w.WriteLine(JsonSerializer.Serialize(op, Opts));
                w.Flush();
                outFs.Flush(true);   // to disk, not just to the OS cache
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            _compactFloor = CompactThresholdBytes;
        }
        catch
        {
            // swap failed (log locked by a sync client, disk full, ...). The live
            // log is untouched and still authoritative; drop the temp and retry
            // after the next threshold crossing.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { _compactFloor = new FileInfo(path).Length + CompactThresholdBytes; } catch { }
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
                long seen = _cursors.Seen.TryGetValue(actor, out var sn) ? sn : 0;
                long knownGen = _cursors.Gens.TryGetValue(actor, out var g) ? g : 0;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    long gen = ReadGeneration(fs);
                    if (gen != knownGen)
                    {
                        // the peer compacted: our byte offset points into a file that
                        // no longer exists. Re-read from the top and let the lamport
                        // high-water mark decide what is genuinely new — a compacted
                        // log holds only each element's latest op, so every op with
                        // N <= seen has already been applied.
                        offset = 0;
                    }
                    else if (fs.Length <= offset) continue;
                    fs.Seek(offset, SeekOrigin.Begin);
                    using var rd = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = rd.ReadLine()) != null)
                    {
                        try
                        {
                            var op = JsonSerializer.Deserialize<Op>(line);
                            if (op == null || op.K == HeaderKind) continue;
                            if (op.N <= seen) continue;
                            if (Apply(lib, op) is { } pgId) changedPages.Add(pgId);
                            seen = op.N;
                        }
                        catch { }
                    }
                    _cursors.Offsets[actor] = fs.Length;
                    _cursors.Seen[actor] = seen;
                    _cursors.Gens[actor] = gen;
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
