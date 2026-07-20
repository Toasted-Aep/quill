using System.Text.Json;
using Quill.Models;

namespace Quill.Services;

public static class LibraryStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    // A small settings file lives at a FIXED anchor (Documents\Quill) and
    // records the chosen central storage folder, so every build/version reads and
    // writes the same notebooks (universal sync) (#settings).
    public sealed class AppSettings
    {
        public string? DataFolder { get; set; }
        public bool ImportedLegacy { get; set; }
        // Mirror of the few library fields the window needs BEFORE the library
        // has finished loading. Without them the window would paint light and
        // snap to dark, and open at the default size before jumping to the
        // remembered one (#roadmap: async library load, phase 2).
        public UiHints Ui { get; set; } = new();
    }

    public sealed class UiHints
    {
        public string Theme { get; set; } = "Dark";
        public bool OledBlack { get; set; }
        public string Accent { get; set; } = "#D97757";
        public double WinX { get; set; }
        public double WinY { get; set; }
        public double WinW { get; set; }
        public double WinH { get; set; }
        public bool WinMaximized { get; set; } = true;
        public bool StartFullscreen { get; set; } = true;
    }

    private static AppSettings? _settings;
    private static string AnchorDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Quill");
    private static string SettingsPath => Path.Combine(AnchorDir, "settings.json");

    // Pre-rename anchor (the app used to be called LectureInk) — adopted once,
    // then kept as a read fallback so no notes are ever lost by the rename.
    private static string OldAnchorDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LectureInk");
    private static string OldSettingsPath => Path.Combine(OldAnchorDir, "settings.json");

    public static AppSettings Settings
    {
        get
        {
            if (_settings != null) return _settings;
            try
            {
                if (File.Exists(SettingsPath))
                    _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                else if (File.Exists(OldSettingsPath))
                {
                    // adopt the old settings (incl. any custom storage folder)
                    _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(OldSettingsPath));
                    if (_settings != null) SaveSettings();
                }
            }
            catch { }
            return _settings ??= new AppSettings();
        }
    }

    public static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(AnchorDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, Opts));
        }
        catch { }
    }

    // The central, user-configurable storage folder (default Documents\Quill).
    public static string Dir
    {
        get
        {
            var f = Settings.DataFolder;
            return !string.IsNullOrWhiteSpace(f) ? f! : AnchorDir;
        }
    }

    // Old hidden location (from the LectureInk era) — migrated/imported once,
    // then kept as a read fallback. Deliberately NOT renamed to Quill.
    public static string LegacyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LectureInk");

    public static string FilePath => Path.Combine(Dir, "library.json");
    private static string LegacyFilePath => Path.Combine(LegacyDir, "library.json");

    // Async library load (#roadmap): App.OnLaunched starts deserialising on a
    // worker thread BEFORE the window is constructed, so JSON parsing overlaps
    // the XAML build instead of running after it. The window then joins the
    // already-running task instead of loading again.
    private static Task<Library>? _pending;
    public static void BeginLoad() { _pending ??= Task.Run(Load); }

    /// <summary>Phase 2 (#roadmap): the window is shown while this is still
    /// running and adopts the result when it arrives, so startup no longer
    /// blocks on JSON parsing.</summary>
    public static Task<Library> LoadAsync()
    {
        BeginLoad();
        return _pending!;
    }

    /// <summary>Drops the cached load so the error state's "Try again" really
    /// re-reads the disk (the user may have restored a backup meanwhile).</summary>
    public static void ResetPendingLoad() => _pending = null;

    /// <summary>True when a library file was present but nothing readable came
    /// out of it. The returned Library is then an EMPTY placeholder that must
    /// never reach disk — see <see cref="EnableSaving"/>.</summary>
    public static bool LoadFailed { get; private set; }
    public static string? LoadError { get; private set; }

    /// <summary>Set when the one-time legacy import actually merged something.
    /// Load runs before saving is enabled, so the write is deferred to the
    /// window instead of being silently dropped by the save gate.</summary>
    public static bool PendingImportSave { get; private set; }

    // Every path Load reads from, newest-first. Used to tell "first run"
    // (nothing exists) apart from "the file is there and we could not read it".
    private static IEnumerable<string> SourcePaths()
    {
        yield return FilePath;
        yield return FilePath + ".bak";
        yield return Path.Combine(OldAnchorDir, "library.json");
        yield return Path.Combine(OldAnchorDir, "library.json.bak");
        yield return LegacyFilePath;
        yield return LegacyFilePath + ".bak";
    }

    public static Library Load()
    {
        LoadFailed = false;
        LoadError = null;
        try
        {
            MigrateFromLegacyIfNeeded();
            bool anySource = SourcePaths().Any(File.Exists);

            var lib = TryRead(FilePath, preserveCorrupt: true)
                ?? TryRead(FilePath + ".bak", preserveCorrupt: false)
                ?? TryRead(Path.Combine(OldAnchorDir, "library.json"), preserveCorrupt: false)
                ?? TryRead(Path.Combine(OldAnchorDir, "library.json.bak"), preserveCorrupt: false)
                ?? TryRead(LegacyFilePath, preserveCorrupt: false)
                ?? TryRead(LegacyFilePath + ".bak", preserveCorrupt: false);

            if (lib == null)
            {
                // A library exists on disk but every copy failed to parse. Seeding
                // a fresh one here would look like an empty app that then autosaves
                // over the user's real notes, so report failure instead and leave
                // the save gate shut.
                if (anySource)
                {
                    LoadFailed = true;
                    LoadError = $"Quill found a library at\n{FilePath}\nbut could not read it or any of its backups.";
                    return new Library();
                }
                lib = Seed();   // genuine first run: nothing to lose
            }

            // One-time automatic recovery: pull in any notebooks that exist in the old
            // location but not in the current central library (this restores notebooks
            // that an earlier version left behind). Runs in the user's normal session
            // where the old location is fully visible.
            if (!Settings.ImportedLegacy)
            {
                var legacy = TryRead(LegacyFilePath, false) ?? TryRead(LegacyFilePath + ".bak", false);
                int added = legacy != null ? Merge(lib, legacy) : 0;
                Settings.ImportedLegacy = true;
                SaveSettings();
                if (added > 0) PendingImportSave = true;
            }
            return lib;
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            LoadError = ex.Message;
            return new Library();
        }
    }

    /// <summary>Copies every property of <paramref name="src"/> onto the live
    /// instance the window already holds. The UI is built around a single
    /// Library object — handlers, the op log and the calculator all capture it —
    /// so the loaded state is adopted in place rather than swapped in.</summary>
    public static void AdoptInPlace(Library target, Library src)
    {
        foreach (var p in typeof(Library).GetProperties())
            if (p.CanRead && p.CanWrite) p.SetValue(target, p.GetValue(src));
    }

    /// <summary>Loads a library from an arbitrary file (for the Settings "Import" action).</summary>
    public static Library? LoadFrom(string path) => TryRead(path, false);

    /// <summary>Adds every notebook (and folder) from <paramref name="source"/> that
    /// isn't already in <paramref name="target"/> (matched by Id). Returns how many
    /// notebooks were added.</summary>
    public static int Merge(Library target, Library source)
    {
        int added = 0;
        var have = new HashSet<Guid>(target.Notebooks.Select(n => n.Id));
        foreach (var nb in source.Notebooks)
        {
            if (have.Contains(nb.Id)) continue;
            // deep clone via JSON so the two libraries never share references
            var clone = JsonSerializer.Deserialize<Notebook>(JsonSerializer.Serialize(nb, Opts), Opts);
            if (clone != null) { target.Notebooks.Add(clone); have.Add(clone.Id); added++; }
        }
        foreach (var f in source.Folders)
            if (!target.Folders.Contains(f)) target.Folders.Add(f);
        return added;
    }

    /// <summary>Changes the central storage folder, copying the current library and
    /// backups into it. Returns the new folder path.</summary>
    public static void SetDataFolder(string newFolder, Library current)
    {
        try
        {
            Directory.CreateDirectory(newFolder);
            Settings.DataFolder = newFolder;
            SaveSettings();
            Save(current); // writes the library (and a snapshot) into the new folder
        }
        catch { }
    }

    // One-time copy of the old AppData library (and its backups) into the new
    // central folder. Copy-only: the originals are never deleted.
    private static void MigrateFromLegacyIfNeeded()
    {
        try
        {
            if (File.Exists(FilePath)) return;          // already on the new path

            // prefer the pre-rename Documents\LectureInk library, then the old
            // hidden AppData location
            string srcDir;
            if (File.Exists(Path.Combine(OldAnchorDir, "library.json"))) srcDir = OldAnchorDir;
            else if (File.Exists(LegacyFilePath)) srcDir = LegacyDir;
            else return;                                // nothing to migrate

            var srcFile = Path.Combine(srcDir, "library.json");
            Directory.CreateDirectory(Dir);
            File.Copy(srcFile, FilePath, false);
            if (File.Exists(srcFile + ".bak"))
                try { File.Copy(srcFile + ".bak", FilePath + ".bak", false); } catch { }

            var srcBackups = Path.Combine(srcDir, "backups");
            if (Directory.Exists(srcBackups))
            {
                Directory.CreateDirectory(BackupDir);
                foreach (var f in Directory.GetFiles(srcBackups, "library-*.json"))
                    try { File.Copy(f, Path.Combine(BackupDir, Path.GetFileName(f)), false); } catch { }
            }
            foreach (var f in Directory.GetFiles(srcDir, "library.recovery-*.json"))
                try { File.Copy(f, Path.Combine(Dir, Path.GetFileName(f)), false); } catch { }
        }
        catch { /* migration is best-effort; the legacy copy stays intact */ }
    }

    private static Library? TryRead(string path, bool preserveCorrupt)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lib = JsonSerializer.Deserialize<Library>(File.ReadAllText(path), Opts);
            return lib != null && lib.Notebooks.Count > 0 ? lib : null;
        }
        catch
        {
            // corrupted file: preserve it for inspection without clobbering the
            // good ".bak" that Save keeps, then let the caller fall back.
            if (preserveCorrupt)
                try { File.Copy(path, path + ".corrupt", true); } catch { }
            return null;
        }
    }

    // Last time WE wrote library.json — used to notice external changes (sync
    // clients, other machines) before overwriting them (#52).
    private static DateTime _lastOwnWriteUtc = DateTime.MinValue;
    private static readonly object _writeLock = new();

    private static Task _lastWrite = Task.CompletedTask;

    // Hard gate on every write of library.json. Startup shows the window before
    // the library has loaded, so until a window has adopted a REAL library there
    // is nothing in memory worth persisting — and after a failed load the gate
    // stays shut forever, which is what stops an autosave from replacing notes
    // we merely failed to parse (#roadmap).
    private static bool _savingEnabled;
    public static bool SavingEnabled => _savingEnabled;
    public static void EnableSaving() => _savingEnabled = true;

    public static void Save(Library lib)
    {
        if (!_savingEnabled) return;
        SyncHints(lib);
        // Serialise on the caller's (UI) thread so the model can't mutate
        // mid-write, then push the actual file IO to a worker (#52).
        string json;
        try { json = JsonSerializer.Serialize(lib, Opts); }
        catch { return; } // unserializable model: never crash the app on save
        // Stage 0 op log: diff against the shadow and append change ops (#collab)
        try { SyncLog.OnSaved(lib); } catch { }
        _lastWrite = Task.Run(() => WriteAll(json));
    }

    // Keep the startup hints in settings.json in step with the library. Written
    // only when something actually changed, so an autosave every 1.5s does not
    // turn into a second file write.
    private static void SyncHints(Library lib)
    {
        try
        {
            var h = Settings.Ui ??= new UiHints();
            if (h.Theme == lib.Theme && h.OledBlack == lib.OledBlack && h.Accent == lib.AccentColor &&
                h.WinX == lib.WinX && h.WinY == lib.WinY && h.WinW == lib.WinW && h.WinH == lib.WinH &&
                h.WinMaximized == lib.WinMaximized && h.StartFullscreen == lib.StartFullscreen) return;
            h.Theme = lib.Theme; h.OledBlack = lib.OledBlack; h.Accent = lib.AccentColor;
            h.WinX = lib.WinX; h.WinY = lib.WinY; h.WinW = lib.WinW; h.WinH = lib.WinH;
            h.WinMaximized = lib.WinMaximized; h.StartFullscreen = lib.StartFullscreen;
            SaveSettings();
        }
        catch { }
    }

    /// <summary>Blocks briefly until the last queued write hits disk — called
    /// on app close so a fire-and-forget save can't be lost.</summary>
    public static void Flush()
    {
        try { _lastWrite.Wait(4000); } catch { }
    }

    private static void WriteAll(string json)
    {
        lock (_writeLock)
        {
            // Conflict guard: if another writer (sync client, second machine)
            // touched library.json since our last save, preserve their version
            // before overwriting it (#52).
            try
            {
                if (_lastOwnWriteUtc != DateTime.MinValue && File.Exists(FilePath) &&
                    File.GetLastWriteTimeUtc(FilePath) > _lastOwnWriteUtc.AddSeconds(2))
                {
                    File.Copy(FilePath,
                        Path.Combine(Dir, $"library.conflict-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                }
            }
            catch { }
            WriteCore(json);
            try { _lastOwnWriteUtc = File.GetLastWriteTimeUtc(FilePath); } catch { }
        }
    }

    private static void WriteCore(string json)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Write to a temp file first, then swap it in atomically so an
            // interrupted write (crash / power loss) can never truncate the
            // live library. File.Replace also rotates the previous good copy
            // into ".bak" for recovery.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, FilePath + ".bak");
            else
                File.Move(tmp, FilePath);
        }
        catch
        {
            // atomic path failed (e.g. locked file): fall back to a direct
            // write so we still persist; still never crash the app on save.
            try { File.WriteAllText(FilePath, json); } catch { }
            // don't leave a stale ".tmp" (containing note data) behind.
            try { if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); } catch { }
        }

        TrySnapshot(json);
    }

    /// <summary>Folder of timestamped rolling backups, kept so a single bad save
    /// can never silently erase note history.</summary>
    public static string BackupDir => Path.Combine(Dir, "backups");

    // Keep periodic timestamped snapshots (throttled to one per 15 min, newest 12
    // retained). These are the safety net behind "library.json" + "library.json.bak".
    private static void TrySnapshot(string json)
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            var existing = new DirectoryInfo(BackupDir)
                .GetFiles("library-*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (existing.Count > 0 &&
                DateTime.UtcNow - existing[0].LastWriteTimeUtc < TimeSpan.FromMinutes(15))
                return; // throttle: don't snapshot on every debounced save

            var name = $"library-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            File.WriteAllText(Path.Combine(BackupDir, name), json);

            // keep the newest 12 snapshots (existing 11 + the new one)
            foreach (var old in existing.Skip(11))
            {
                try { old.Delete(); } catch { }
            }
        }
        catch { /* backups are best-effort; never disrupt a save */ }
    }

    private static Library Seed()
    {
        var lib = new Library();
        var nb = new Notebook { Name = "My Notebook" };
        var sec = new Section { Name = "Lecture 1" };
        sec.Pages.Add(new NotePage { Name = "Page 1", Background = "#FAF9F5" });
        nb.Sections.Add(sec);
        lib.Notebooks.Add(nb);
        return lib;
    }

    // =======================================================================
    // TRASH BIN (#trash)
    // A deleted notebook drags all of its strokes with it, and library.json is
    // already 53 MB and rewritten on every 1.5 s autosave — so the bin is a
    // SEPARATE document (trash.json, next to library.json) written only when
    // something is actually deleted, restored or purged. Retention lives on the
    // bin (TrashBin.RetentionDays, default 30) so it is trivial to change.
    // =======================================================================
    private static string TrashPath => Path.Combine(Dir, "trash.json");
    private static TrashBin? _trash;
    private static readonly object _trashLock = new();

    /// <summary>The lazily-loaded trash bin. Reads trash.json (then its ".bak")
    /// once; a missing or unreadable file yields an empty bin, never an error —
    /// losing the bin must never take the library down with it.</summary>
    public static TrashBin Trash => _trash ??= LoadTrash();

    private static TrashBin LoadTrash()
    {
        foreach (var p in new[] { TrashPath, TrashPath + ".bak" })
        {
            try
            {
                if (File.Exists(p))
                {
                    var bin = JsonSerializer.Deserialize<TrashBin>(File.ReadAllText(p), Opts);
                    if (bin != null) return bin;
                }
            }
            catch { /* try the backup, then fall through to an empty bin */ }
        }
        return new TrashBin();
    }

    // Crash-safe write of trash.json: temp file, atomic replace (rotating the
    // previous good copy into ".bak"), never an in-place overwrite. Gated on the
    // same save switch as the library so a failed library load can never cause a
    // trash write. Deletes are deliberate and infrequent, so this stays synchronous.
    private static void SaveTrash()
    {
        if (!_savingEnabled) return;
        if (_trash == null) return;
        string json;
        try { json = JsonSerializer.Serialize(_trash, Opts); }
        catch { return; }
        lock (_trashLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = TrashPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(TrashPath))
                    File.Replace(tmp, TrashPath, TrashPath + ".bak");
                else
                    File.Move(tmp, TrashPath);
            }
            catch
            {
                // atomic path failed (locked file): fall back to a direct write so
                // the deletion still persists, and never leave a stale ".tmp" of note data.
                try { File.WriteAllText(TrashPath, json); } catch { }
                try { if (File.Exists(TrashPath + ".tmp")) File.Delete(TrashPath + ".tmp"); } catch { }
            }
        }
    }

    private static void PushTrash(TrashEntry e)
    {
        var bin = Trash;
        lock (_trashLock)
        {
            bin.Items.Insert(0, e);                 // newest first
            // Hard cap so a bin left untended cannot grow without bound; oldest go first.
            while (bin.Items.Count > TrashBin.MaxItems)
                bin.Items.RemoveAt(bin.Items.Count - 1);
        }
        SaveTrash();
    }

    /// <summary>Soft-deletes a notebook: removes it from the library and files it
    /// in the bin with its original index so Restore can put it back.</summary>
    public static void DeleteNotebook(Library lib, Notebook nb)
    {
        int idx = lib.Notebooks.IndexOf(nb);
        if (idx < 0) return;
        lib.Notebooks.RemoveAt(idx);
        PushTrash(new TrashEntry
        {
            Kind = TrashItemKind.Notebook,
            Name = nb.Name,
            OriginalIndex = idx,
            Notebook = nb
        });
        PruneRecents(lib);   // its pages are gone from the tree now
        Save(lib);
    }

    /// <summary>Soft-deletes a section, remembering its parent notebook and index.</summary>
    public static void DeleteSection(Library lib, Notebook parent, Section sec)
    {
        int idx = parent.Sections.IndexOf(sec);
        if (idx < 0) return;
        parent.Sections.RemoveAt(idx);
        PushTrash(new TrashEntry
        {
            Kind = TrashItemKind.Section,
            Name = sec.Name,
            ParentNotebookId = parent.Id,
            OriginalIndex = idx,
            Section = sec
        });
        PruneRecents(lib);
        Save(lib);
    }

    /// <summary>Soft-deletes a page, remembering its notebook, section and index.</summary>
    public static void DeletePage(Library lib, Notebook nb, Section sec, NotePage page)
    {
        int idx = sec.Pages.IndexOf(page);
        if (idx < 0) return;
        sec.Pages.RemoveAt(idx);
        PushTrash(new TrashEntry
        {
            Kind = TrashItemKind.Page,
            Name = page.Name,
            ParentNotebookId = nb.Id,
            ParentSectionId = sec.Id,
            OriginalIndex = idx,
            Page = page
        });
        PruneRecents(lib);
        Save(lib);
    }

    /// <summary>Restores a bin entry to its original location. If the original
    /// parent is gone, it falls back to a sensible home (an existing container,
    /// or a freshly-made "Recovered" one) rather than dropping the item. Returns
    /// false if the entry is missing or already back in the tree.</summary>
    public static bool Restore(Library lib, Guid entryId)
    {
        var bin = Trash;
        TrashEntry? e;
        lock (_trashLock) { e = bin.Items.FirstOrDefault(x => x.Id == entryId); }
        if (e == null) return false;

        bool ok = e.Kind switch
        {
            TrashItemKind.Notebook => RestoreNotebook(lib, e),
            TrashItemKind.Section  => RestoreSection(lib, e),
            TrashItemKind.Page     => RestorePage(lib, e),
            _ => false
        };
        if (ok)
        {
            lock (_trashLock) { bin.Items.Remove(e); }
            SaveTrash();
            Save(lib);
        }
        return ok;
    }

    private static bool RestoreNotebook(Library lib, TrashEntry e)
    {
        var nb = e.Notebook;
        if (nb == null) return false;
        if (lib.Notebooks.Any(n => n.Id == nb.Id)) return false;   // already present
        int idx = Math.Clamp(e.OriginalIndex, 0, lib.Notebooks.Count);
        lib.Notebooks.Insert(idx, nb);
        return true;
    }

    private static bool RestoreSection(Library lib, TrashEntry e)
    {
        var sec = e.Section;
        if (sec == null) return false;
        var parent = lib.Notebooks.FirstOrDefault(n => n.Id == e.ParentNotebookId)
                     ?? EnsureRecoveryNotebook(lib);
        if (parent.Sections.Any(s => s.Id == sec.Id)) return false;
        int idx = Math.Clamp(e.OriginalIndex, 0, parent.Sections.Count);
        parent.Sections.Insert(idx, sec);
        return true;
    }

    private static bool RestorePage(Library lib, TrashEntry e)
    {
        var page = e.Page;
        if (page == null) return false;
        // Prefer the exact original section; then the section by id anywhere it may
        // have moved to; finally a recovery home so the page is never lost.
        Section? sec = lib.Notebooks.FirstOrDefault(n => n.Id == e.ParentNotebookId)
                          ?.Sections.FirstOrDefault(s => s.Id == e.ParentSectionId)
                       ?? lib.Notebooks.SelectMany(n => n.Sections)
                             .FirstOrDefault(s => s.Id == e.ParentSectionId)
                       ?? EnsureRecoverySection(lib);
        if (sec.Pages.Any(p => p.Id == page.Id)) return false;
        int idx = Math.Clamp(e.OriginalIndex, 0, sec.Pages.Count);
        sec.Pages.Insert(idx, page);
        return true;
    }

    private static Notebook EnsureRecoveryNotebook(Library lib)
    {
        var nb = lib.Notebooks.FirstOrDefault();
        if (nb != null) return nb;
        nb = new Notebook { Name = "Recovered" };
        lib.Notebooks.Add(nb);
        return nb;
    }

    private static Section EnsureRecoverySection(Library lib)
    {
        var nb = EnsureRecoveryNotebook(lib);
        var sec = nb.Sections.FirstOrDefault();
        if (sec != null) return sec;
        sec = new Section { Name = "Recovered" };
        nb.Sections.Add(sec);
        return sec;
    }

    /// <summary>Permanently removes one bin entry. Returns true if it existed.</summary>
    public static bool Purge(Guid entryId)
    {
        bool removed;
        lock (_trashLock) { removed = Trash.Items.RemoveAll(x => x.Id == entryId) > 0; }
        if (removed) SaveTrash();
        return removed;
    }

    /// <summary>Empties the bin permanently.</summary>
    public static void PurgeAll()
    {
        lock (_trashLock)
        {
            if (Trash.Items.Count == 0) return;
            Trash.Items.Clear();
        }
        SaveTrash();
    }

    /// <summary>Age-based auto-purge (default 30 days, see TrashBin.RetentionDays).
    /// Deliberately gated on the save switch AND a clean load: after a failed or
    /// empty library load the gate is shut, so a parse failure can never silently
    /// empty the user's trash. Call after the window has adopted a real library.
    /// RetentionDays &lt;= 0 disables age purging (MaxItems still caps the bin).</summary>
    public static int AutoPurgeExpired()
    {
        if (!_savingEnabled || LoadFailed) return 0;
        var bin = Trash;
        int days = bin.RetentionDays;
        if (days <= 0) return 0;
        long cutoff = DateTime.UtcNow.AddDays(-days).Ticks;
        int removed;
        lock (_trashLock) { removed = bin.Items.RemoveAll(x => x.DeletedTicks < cutoff); }
        if (removed > 0) SaveTrash();
        return removed;
    }

    // =======================================================================
    // RECENTLY OPENED (#recents)
    // Small enough to ride inside Library.Recents (a few KB at the cap) and thus
    // persisted with the library's own crash-safe write. Newest first, deduped
    // by page id, capped at RecentPage.MaxRecents, and pruned of dead pages.
    // =======================================================================

    /// <summary>Records a page open at the top of the recents list, de-duplicating
    /// by page id and capping the length. Names are cached so the gallery row can
    /// render without walking the tree.</summary>
    public static void RecordRecent(Library lib, Notebook nb, Section sec, NotePage page)
    {
        var list = lib.Recents ??= new();
        list.RemoveAll(r => r.PageId == page.Id);   // dedupe: an old entry moves to the top
        list.Insert(0, new RecentPage
        {
            PageId = page.Id,
            SectionId = sec.Id,
            NotebookId = nb.Id,
            PageName = page.Name,
            NotebookName = nb.Name,
            OpenedTicks = DateTime.UtcNow.Ticks
        });
        while (list.Count > RecentPage.MaxRecents)
            list.RemoveAt(list.Count - 1);
        Save(lib);
    }

    /// <summary>Drops recents whose page no longer exists anywhere in the library
    /// (deleted or purged). Returns how many were removed. Does not itself save —
    /// callers that mutate the tree (the Delete* methods) save right after; the
    /// load path prunes before saving is even enabled.</summary>
    public static int PruneRecents(Library lib)
    {
        var list = lib.Recents;
        if (list == null || list.Count == 0) return 0;
        var live = new HashSet<Guid>(
            lib.Notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Pages).Select(p => p.Id));
        return list.RemoveAll(r => !live.Contains(r.PageId));
    }
}
