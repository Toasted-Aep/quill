using System.Text.Json;
using System.Text.Json.Serialization;
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
        // Full mirror of every library-level SETTING (see SettingFields).
        // library.json is a 53 MB whole-file overwrite, so any instance that has
        // it open rewrites the settings block from the snapshot it loaded and
        // silently reverts changes made anywhere else. Settings therefore live
        // here too, in a file small enough to rewrite the moment one changes.
        // null = never mirrored, which SEEDS from the library instead of
        // overwriting it (so adding this could not wipe existing settings).
        public Dictionary<string, JsonElement>? Settings { get; set; }
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
        public bool StartMaximised { get; set; } = true;
        /// <summary>Pre-rename key. Nullable, null-getter, never written back —
        /// see <see cref="Library.StartFullscreen"/> for why it has to be all
        /// three.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? StartFullscreen
        {
            get => null;
            set { if (value is bool v) StartMaximised = v; }
        }
    }

    private static AppSettings? _settings;

    // Test seam. The save-path harness redirects the FIXED anchor into a scratch
    // folder so settings.json, the library and the trash can all be exercised for
    // real without going anywhere near the user's actual notes. Nothing in the app
    // ever assigns this: it is private, has no setter, no env var and no config
    // key, and is reachable only by reflection from the harness.
    private static string? _anchorOverride = null;

    /// <summary>The FIXED anchor: where settings.json lives, and the default home
    /// for the library.
    ///
    /// <para><b>QUILL_DATA_FOLDER moves this too, and that is the point.</b> The
    /// variable used to redirect only <see cref="Dir"/>, so an isolated test
    /// process still read AND REWROTE the real user's
    /// <c>Documents\Quill\settings.json</c> — its window geometry, its theme
    /// mirror, all of it — every time it ran. That is a live file belonging to
    /// somebody who is not running the test. Isolation that leaves one foot in
    /// the user's folder is not isolation, so the anchor follows the variable
    /// and an isolated instance now touches nothing outside its own folder.
    /// DataFolder inside an isolated settings.json is simply ignored, because
    /// <see cref="Dir"/> checks the variable first.</para></summary>
    private static string AnchorDir =>
        _anchorOverride ?? EnvFolder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Quill");
    private static string SettingsPath => Path.Combine(AnchorDir, "settings.json");

    // Pre-rename anchor (the app used to be called LectureInk) — adopted once,
    // then kept as a read fallback so no notes are ever lost by the rename.
    private static string OldAnchorDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LectureInk");
    private static string OldSettingsPath => Path.Combine(OldAnchorDir, "settings.json");

    /// <summary>True when a settings file EXISTS but neither it nor its backup
    /// could be parsed. settings.json records DataFolder — i.e. WHERE the library
    /// lives — so silently falling back to defaults would point the app at an
    /// empty Documents\Quill and let it seed a fresh library there while the real
    /// notes sit in a folder nothing references any more. Load fails closed on it.</summary>
    public static bool SettingsUnreadable { get; private set; }

    public static AppSettings Settings
    {
        get
        {
            if (_settings != null) return _settings;
            // Same-lineage first: settings.json, then the ".bak" SaveSettings
            // rotates beside it.
            bool sawFile = false;
            foreach (var p in new[] { SettingsPath, SettingsPath + ".bak" })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    sawFile = true;
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p));
                    if (s != null) { _settings = s; break; }
                }
                catch { /* try the backup, then decide below */ }
            }
            // The pre-rename anchor holds a DIFFERENT lineage's settings, with its
            // OWN DataFolder. Adopting it on a genuine first run is the migration
            // it was written for; adopting it because the current settings.json is
            // sitting right there and merely failed to parse would silently point
            // the app at another folder — exactly the cross-location trap the
            // library load path closes. So only migrate when nothing is here.
            // ...and NOT when QUILL_DATA_FOLDER is redirecting us (#2.0). This
            // read is the reason five scratch runs looked isolated and were not:
            // an empty scratch folder has no settings.json, so it adopted the
            // REAL user's Documents\LectureInk\settings.json - which happens to
            // carry ImportedLegacy=true, so the merge below was skipped and the
            // leak stayed hidden. Seed a settings.json (run 16 did, for the
            // theme) and the adoption stops, the flag reverts to false, and the
            // merge runs. An isolated instance must read no file of the user's.
            if (_settings == null && !sawFile && !IsIsolated)
            {
                try
                {
                    if (File.Exists(OldSettingsPath))
                    {
                        // adopt the old settings (incl. any custom storage folder)
                        _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(OldSettingsPath));
                        if (_settings != null) { SaveSettings(); return _settings; }
                    }
                }
                catch { }
            }
            // A file was there and nothing came out of it: do NOT quietly become
            // a first run. Defaults here would relocate the whole library.
            if (_settings == null && sawFile) SettingsUnreadable = true;
            return _settings ??= new AppSettings();
        }
    }

    private static readonly object _settingsLock = new();

    /// <summary>Persists the anchor settings. This used to be a plain
    /// File.WriteAllText — the ONE remaining write in the app that opened a file
    /// the app cannot do without for truncation, with no flush, no backup and no
    /// retry. A crash or a second instance mid-write emptied it, DataFolder was
    /// lost, and the next launch seeded a brand-new library in the default folder.
    /// Same temp + Flush(true) + File.Replace path as the library now.</summary>
    public static void SaveSettings()
    {
        if (SettingsUnreadable) return;   // never overwrite a file we failed to read
        lock (_settingsLock)
        {
            string json;
            try { json = JsonSerializer.Serialize(Settings, Opts); }
            catch (Exception ex) { LogProblem($"settings.json could not be serialised, not written: {ex.Message}"); return; }
            try { Directory.CreateDirectory(AnchorDir); } catch { }
            Exception? lastEx = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { WriteAtomic(SettingsPath, json); return; }
                catch (Exception ex) { lastEx = ex; if (attempt < 2) Thread.Sleep(80 * (attempt + 1)); }
            }
            LogProblem($"settings.json could not be written after 3 attempts, it keeps its old content: {lastEx?.GetType().Name}: {lastEx?.Message}");
            // never leave a stale ".tmp" behind; settings.json keeps its old content
            try { if (File.Exists(SettingsPath + ".tmp")) File.Delete(SettingsPath + ".tmp"); } catch { }
        }
    }

    // The central, user-configurable storage folder (default Documents\Quill).
    // An out-of-process override, checked before the saved setting. Automated
    // tests need an isolated library WITHOUT rewriting the user's settings.json
    // - doing that repointed the real app at a throwaway folder twice, and the
    // user opened Quill to an empty gallery both times. Set QUILL_DATA_FOLDER
    // for the child process instead; nothing on disk changes.
    private static string? EnvFolder
    {
        get
        {
            try
            {
                var v = Environment.GetEnvironmentVariable("QUILL_DATA_FOLDER");
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch { return null; }
        }
    }

    public static string Dir
    {
        get
        {
            var e = EnvFolder;
            if (e != null) return e;
            var f = Settings.DataFolder;
            return !string.IsNullOrWhiteSpace(f) ? f! : AnchorDir;
        }
    }

    /// <summary>True when QUILL_DATA_FOLDER is redirecting this process, which
    /// is this app's one signal for "an isolated instance, not the user's".
    ///
    /// <para>Exists so that state kept OUTSIDE the data folder can follow the
    /// isolation too. <see cref="SyncLog"/> keeps its per-device read cursors in
    /// %LOCALAPPDATA% on purpose, and <see cref="Save"/> writes them on every
    /// save — so a headless harness was rewriting the real user's cursors from
    /// its own empty copy, which is the roadmap's replay risk arriving by the
    /// front door. Isolation that leaves one foot in the user's folder is not
    /// isolation; the same sentence already justifies the anchor following this
    /// variable (see <see cref="AnchorDir"/>).</para></summary>
    public static bool IsIsolated => EnvFolder != null;

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
        // An isolated instance has no lineage older than its own folder. Leaving
        // the legacy paths in here would also make `anySource` true for an EMPTY
        // scratch folder, so Load would report "found a library and could not
        // read it" instead of seeding - a harness that cannot start.
        if (IsIsolated) yield break;
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
            // Touch the settings getter FIRST. SettingsUnreadable starts false and is
            // only ever set by that getter, so checking it before anything has read
            // settings.json made this whole guard dead code - the worker thread got
            // here ten lines before the first read, saw false, and sailed past.
            _ = Settings;

            // Where the library lives comes out of settings.json. If that file is
            // there but unreadable we do not know the answer, and guessing means
            // opening the DEFAULT folder, finding nothing, seeding a fresh library
            // and autosaving it — with the user's real notes intact but orphaned in
            // a folder nothing points at. Fail closed exactly like a bad library.
            if (SettingsUnreadable)
            {
                LoadFailed = true;
                LoadError = $"Quill could not read its settings file\n{SettingsPath}\n" +
                            "It records which folder your notebooks are stored in, so Quill has " +
                            "stopped rather than risk creating an empty library somewhere else.\n\n" +
                            "Restore it from settings.json.bak next to it, or delete it if you have " +
                            "never changed the storage folder.";
                return new Library();
            }
            MigrateFromLegacyIfNeeded();
            bool anySource = SourcePaths().Any(File.Exists);

            // Arm the foreign-write guard with the state we are ACTUALLY reading,
            // so the very first save of the session is checked against it too
            // (#52 — two instances used to overwrite each other's notes silently).
            try
            {
                _lastOwnWriteUtc = File.Exists(FilePath)
                    ? File.GetLastWriteTimeUtc(FilePath)
                    : DateTime.MinValue;
            }
            catch { _lastOwnWriteUtc = DateTime.MinValue; }

            // Same-lineage recovery only: library.json, then the ".bak" that
            // Save rotates beside it.
            bool primaryExists = File.Exists(FilePath);
            var lib = TryRead(FilePath, preserveCorrupt: true)
                ?? TryRead(FilePath + ".bak", preserveCorrupt: false);

            // The pre-rename / legacy locations hold a DIFFERENT (older) library.
            // Reaching for them is right on a genuine first run at this path, but
            // catastrophic when library.json is sitting right there and merely
            // failed to parse: the user would be shown stale notes, saving would
            // be enabled, and the next autosave would overwrite the real file
            // with them. So only migrate when there is nothing here at all.
            // ...and never under isolation, where "nothing here at all" means an
            // empty scratch folder, not a user who upgraded (#2.0).
            if (lib == null && !primaryExists && !IsIsolated)
            {
                lib = TryRead(Path.Combine(OldAnchorDir, "library.json"), preserveCorrupt: false)
                    ?? TryRead(Path.Combine(OldAnchorDir, "library.json.bak"), preserveCorrupt: false)
                    ?? TryRead(LegacyFilePath, preserveCorrupt: false)
                    ?? TryRead(LegacyFilePath + ".bak", preserveCorrupt: false);
            }

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
            //
            // Gated on IsIsolated as well (#2.0). This is the path run 16 caught:
            // it is flag-driven, not File.Exists-driven, so pre-seeding the
            // scratch library.json - the gate that stops MigrateFromLegacyIfNeeded
            // - does not stop it, and three of the user's real notebooks were
            // merged into a scratch gallery that was supposed to be empty. The
            // flag is deliberately NOT set to true here under isolation: nothing
            // was imported, and recording that it was would be a lie the folder
            // carries forward.
            if (!IsIsolated && !Settings.ImportedLegacy)
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
        // Dir prefers QUILL_DATA_FOLDER, but SettingsPath is always the real
        // anchor - so in a test process this would persist newFolder into the
        // USER'S settings.json while writing the library into the env folder,
        // leaving newFolder empty. The next real launch would then find nothing
        // there, seed a fresh library and autosave it over the top. The env
        // override exists to keep tests away from the user's data; it must not
        // become another route into it.
        if (EnvFolder != null)
        {
            WriteError = "Storage folder cannot be changed while QUILL_DATA_FOLDER is set.";
            return;
        }
        try
        {
            Directory.CreateDirectory(newFolder);
            // The destination may already hold a DIFFERENT library, and the Save
            // below is about to write straight over it — the one write in the app
            // that legitimately replaces somebody else's whole library. Keep a copy.
            var dest = Path.Combine(newFolder, "library.json");
            if (File.Exists(dest))
                try
                {
                    File.Copy(dest, Path.Combine(newFolder,
                        $"library.replaced-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                }
                catch { }
            Settings.DataFolder = newFolder;
            SaveSettings();
            // Re-arm the foreign-write guard against the NEW path. It was holding
            // the old folder's timestamp, which says nothing about this file, so
            // the next save would compare across two unrelated libraries.
            try { _lastOwnWriteUtc = File.Exists(dest) ? File.GetLastWriteTimeUtc(dest) : DateTime.MinValue; }
            catch { _lastOwnWriteUtc = DateTime.MinValue; }
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
            // An isolated instance (QUILL_DATA_FOLDER) has nothing to migrate
            // FROM: the legacy folders are the real user's (#2.0). This gate used
            // to be File.Exists(FilePath) alone, which a harness satisfied by
            // pre-seeding a library - fine until a SECOND import path turned up
            // that a seeded library does not gate.
            if (IsIsolated) return;

            // prefer the pre-rename Documents\LectureInk library, then the old
            // hidden AppData location
            string srcDir;
            if (File.Exists(Path.Combine(OldAnchorDir, "library.json"))) srcDir = OldAnchorDir;
            else if (File.Exists(LegacyFilePath)) srcDir = LegacyDir;
            else return;                                // nothing to migrate

            var srcFile = Path.Combine(srcDir, "library.json");
            Directory.CreateDirectory(Dir);
            // Copy through a temp and rename. A half-finished File.Copy straight to
            // FilePath leaves a truncated library.json, and Load now (correctly)
            // refuses to fall back to the legacy folder once the primary exists —
            // so a torn copy would lock the user out of a library that is sitting
            // right there, intact, in the old location.
            var seedTmp = FilePath + ".migrating";
            File.Copy(srcFile, seedTmp, true);
            File.Move(seedTmp, FilePath);
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
            var text = File.ReadAllText(path);
            var lib = JsonSerializer.Deserialize<Library>(text, Opts);
            if (lib == null) return null;
            // A page size carried over from an earlier session (an extreme value, an
            // overflow once converted to world units) is repaired in memory, loudly,
            // so it can neither draw wrongly nor stop the first save.
            NoteRepairs(RepairCheap(lib), "on load");
            if (lib.Notebooks.Count > 0) return lib;
            // Zero notebooks is AMBIGUOUS: either the user really deleted the
            // last one, or the file is a stub ("{}", "null", a truncated write)
            // that merely deserialises to defaults. Treating both as a failure
            // locked the user out of their own app after deleting the last
            // notebook. Only a document that actually carries a "Notebooks"
            // array is a deliberate empty library; anything else still falls
            // through to the backups so a stub can never mask real notes.
            return HasNotebooksArray(text) ? lib : null;
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

    /// <summary>True when the JSON really is a library document carrying a
    /// Notebooks array — as opposed to a stub that just happens to deserialise
    /// into a Library with all-default values.</summary>
    private static bool HasNotebooksArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("Notebooks", out var el) &&
                   el.ValueKind == JsonValueKind.Array;
        }
        catch { return false; }
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

    // =======================================================================
    // NON-FINITE NUMBERS  (cloud data-safety audit, finding 1)
    // =======================================================================
    // System.Text.Json refuses to WRITE NaN or Infinity. Save used to swallow that
    // exception with a bare "return" - no WriteError, no log - so one bad double
    // anywhere in the model (the Artboard box's "∞" parsed as +Infinity; a
    // NotebookPanel height of NaN after a tap on its grip) stopped EVERY later
    // save, the close save and the op log included, for the rest of the session,
    // with nothing on screen. Now:
    //   * the cheap, always-on repairs (page sizes, the library's own settings
    //     doubles) run before every serialise, so the common cases never reach
    //     the catch at all;
    //   * a serialise failure is logged, repaired by a reflection walk over the
    //     WHOLE model (only ever run on the failure path), and retried;
    //   * if it still cannot be serialised, WriteError says so - never silence.
    // A repair is reported through <see cref="RepairNotice"/> and crash.log, with
    // the field's path, so the owner is told and the file stays savable.

    /// <summary>Non-null once this session has repaired invalid numbers in the
    /// model (on load or on save). Sticky: unlike <see cref="WriteError"/>, which the
    /// next successful write clears, a repair is information the user has to be
    /// able to see after the save that did it has already succeeded.</summary>
    public static string? RepairNotice { get; private set; }

    private static string? _serializeError;

    private static readonly Dictionary<string, DateTime> _loggedAt = new();

    /// <summary>For a catch on a path that runs every autosave: the same problem is
    /// written once, then again no sooner than five minutes later, so a standing
    /// fault cannot fill crash.log at one line per 1.5 s.</summary>
    private static void LogProblemOnce(string key, string msg)
    {
        lock (_loggedAt)
        {
            if (_loggedAt.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(5)) return;
            _loggedAt[key] = DateTime.UtcNow;
        }
        LogProblem(msg);
    }

    private static void LogProblem(string msg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // ASCII on purpose: under ICU even the invariant culture spells infinity with
    // the infinity sign, which would put a character in crash.log that the next
    // reader cannot grep for.
    private static string Fmt(double v) =>
        double.IsNaN(v) ? "NaN" :
        double.IsPositiveInfinity(v) ? "Infinity" :
        double.IsNegativeInfinity(v) ? "-Infinity" :
        v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly System.Reflection.PropertyInfo[] LibraryDoubleProps =
        typeof(Library).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(double) && p.CanRead && p.CanWrite &&
                        p.GetIndexParameters().Length == 0)
            .ToArray();

    /// <summary>The library's own double SETTINGS (panel size, window placement,
    /// eraser size...) back to the shipped default when not finite. Cheap: about
    /// fifteen properties. Also run by <see cref="PersistSettings"/>, which
    /// serialises them into settings.json.</summary>
    private static void RepairLibraryDoubles(Library lib, List<string> into)
    {
        Library? fresh = null;
        foreach (var p in LibraryDoubleProps)
        {
            double v = (double)p.GetValue(lib)!;
            if (double.IsFinite(v)) continue;
            fresh ??= new Library();
            double d = (double)p.GetValue(fresh)!;
            if (!double.IsFinite(d)) d = 0;
            p.SetValue(lib, d);
            into.Add($"Library.{p.Name} {Fmt(v)} -> {Fmt(d)}");
        }
    }

    /// <summary>The always-on repairs: page sizes and the library's settings
    /// doubles. Walks pages, never strokes, so it is free next to the serialise.</summary>
    private static List<string> RepairCheap(Library lib)
    {
        var r = new List<string>();
        RepairLibraryDoubles(lib, r);
        foreach (var nb in lib.Notebooks)
        {
            if (nb?.Sections == null) continue;
            foreach (var sec in nb.Sections)
            {
                if (sec?.Pages == null) continue;
                foreach (var pg in sec.Pages)
                {
                    if (pg == null) continue;
                    var what = PageSizes.Repair(pg);
                    if (what != null) r.Add($"{nb.Name} / {sec.Name} / {pg.Name} ({pg.Id}): {what}");
                }
            }
        }
        return r;
    }

    /// <summary>Records and announces repairs: crash.log gets every line, the
    /// user gets <see cref="RepairNotice"/> naming the first. Does nothing for an
    /// empty list.</summary>
    private static void NoteRepairs(List<string> repairs, string when)
    {
        if (repairs.Count == 0) return;
        foreach (var line in repairs) LogProblem($"library repair ({when}): {line}");
        RepairNotice = $"Quill repaired {repairs.Count} invalid number(s) in your notes ({when}) " +
                       $"so they could be saved - for example {repairs[0]}. Details are in crash.log.";
    }

    /// <summary>The failure path of <see cref="Save"/>: log why, repair every
    /// non-finite number in the whole model, serialise again. Returns false (with
    /// WriteError set) only if the model STILL cannot be serialised.</summary>
    private static bool RecoverFromSerializeFailure(Library lib, Exception ex, List<string> repairs, out string json)
    {
        LogProblem($"library save: the library could not be serialised ({ex.GetType().Name}: {ex.Message}); " +
                   "repairing non-finite numbers and trying again");
        try { repairs.AddRange(RepairEverything(lib)); }
        catch (Exception walkEx) { LogProblem($"library save: the repair walk itself failed ({walkEx.GetType().Name}: {walkEx.Message})"); }
        PersistSettings(lib);
        try
        {
            json = JsonSerializer.Serialize(lib, Opts);
            _serializeError = null;
            return true;
        }
        catch (Exception ex2)
        {
            LogProblem($"library save: STILL could not be serialised after the repair ({ex2.GetType().Name}: {ex2.Message}). " +
                       "Nothing was written; library.json keeps its last good content.");
            _serializeError = "Quill could not save your notes: they contain data that cannot be written " +
                              $"({ex2.Message}). Nothing new has reached library.json; see crash.log.";
            json = "";
            return false;
        }
    }

    // ---- the whole-model walk (failure path only) --------------------------
    private enum NumKind { Double, Float, NullableDouble, NullableFloat, Struct, Child }

    /// <summary>A struct of the app's own (CanvasPoint: a vanishing point is a
    /// pair of doubles) that the walk must open up. Everything under System.* (Guid,
    /// DateTime, JsonElement, Nullable...) and every primitive or enum is left alone.</summary>
    private static bool IsWalkableStruct(Type t) =>
        t.IsValueType && !t.IsPrimitive && !t.IsEnum && !(t.Namespace ?? "").StartsWith("System", StringComparison.Ordinal);

    private sealed record NumProp(System.Reflection.PropertyInfo Info, NumKind Kind, object? Default);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, NumProp[]> NumProps = new();

    private static NumProp[] NumPropsOf(Type t) => NumProps.GetOrAdd(t, static type =>
    {
        object? fresh = null;
        try { fresh = Activator.CreateInstance(type); } catch { }
        var list = new List<NumProp>();
        foreach (var pi in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            // Settable, readable, not [JsonIgnore]d: exactly the properties the
            // serialiser writes AND this walk can repair. A computed getter has no
            // setter, so it is never called here.
            if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length != 0) continue;
            var ign = (JsonIgnoreAttribute?)Attribute.GetCustomAttribute(pi, typeof(JsonIgnoreAttribute));
            if (ign != null && ign.Condition == JsonIgnoreCondition.Always) continue;
            var pt = pi.PropertyType;
            NumKind kind;
            if (pt == typeof(double)) kind = NumKind.Double;
            else if (pt == typeof(float)) kind = NumKind.Float;
            else if (pt == typeof(double?)) kind = NumKind.NullableDouble;
            else if (pt == typeof(float?)) kind = NumKind.NullableFloat;
            else if (IsWalkableStruct(Nullable.GetUnderlyingType(pt) ?? pt)) kind = NumKind.Struct;
            else if (pt.IsValueType || pt == typeof(string)) continue;   // Guid, enums, JsonElement...
            else kind = NumKind.Child;
            object? def = null;
            if (kind != NumKind.Child && fresh != null)
                try { def = pi.GetValue(fresh); } catch { }
            list.Add(new NumProp(pi, kind, def));
        }
        return list.ToArray();
    });

    private sealed class NumberRepairWalk
    {
        private const int MaxReported = 40;
        private readonly List<(string Name, int Index)> _path = new();
        public readonly List<string> Repairs = new();
        public int Count;

        private string PathText(string tail)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (name, index) in _path)
            {
                if (name.Length > 0) { if (sb.Length > 0) sb.Append('.'); sb.Append(name); }
                else sb.Append('[').Append(index).Append(']');
            }
            if (tail.Length > 0) { if (sb.Length > 0) sb.Append('.'); sb.Append(tail); }
            return sb.ToString();
        }

        private void Record(string prop, double was, string now)
        {
            Count++;
            if (Repairs.Count < MaxReported) Repairs.Add($"{PathText(prop)} {Fmt(was)} -> {now}");
        }

        public void Walk(object? o, int depth = 0)
        {
            if (o == null || depth > 40) return;
            if (o is System.Collections.IDictionary dict)
            {
                foreach (var v in dict.Values)
                    if (v != null && v is not string && !v.GetType().IsValueType) Walk(v, depth + 1);
                return;
            }
            if (o is System.Collections.IList list) { WalkList(list, depth); return; }
            foreach (var p in NumPropsOf(o.GetType()))
            {
                switch (p.Kind)
                {
                    case NumKind.Double:
                    {
                        double v = (double)p.Info.GetValue(o)!;
                        if (double.IsFinite(v)) break;
                        double d = p.Default is double dd && double.IsFinite(dd) ? dd : 0;
                        p.Info.SetValue(o, d);
                        Record(p.Info.Name, v, Fmt(d));
                        break;
                    }
                    case NumKind.Float:
                    {
                        float v = (float)p.Info.GetValue(o)!;
                        if (float.IsFinite(v)) break;
                        float d = p.Default is float ff && float.IsFinite(ff) ? ff : 0f;
                        p.Info.SetValue(o, d);
                        Record(p.Info.Name, v, Fmt(d));
                        break;
                    }
                    case NumKind.NullableDouble:
                    {
                        if (p.Info.GetValue(o) is double v && !double.IsFinite(v))
                        {
                            p.Info.SetValue(o, null);
                            Record(p.Info.Name, v, "null");
                        }
                        break;
                    }
                    case NumKind.NullableFloat:
                    {
                        if (p.Info.GetValue(o) is float v && !float.IsFinite(v))
                        {
                            p.Info.SetValue(o, null);
                            Record(p.Info.Name, v, "null");
                        }
                        break;
                    }
                    case NumKind.Struct:
                    {
                        // A struct comes back from GetValue as a boxed COPY: repair the
                        // copy, then write it back, or the repair is thrown away.
                        var box = p.Info.GetValue(o);
                        if (box == null) break;
                        int before = Count;
                        _path.Add((p.Info.Name, -1));
                        Walk(box, depth + 1);
                        _path.RemoveAt(_path.Count - 1);
                        if (Count != before) p.Info.SetValue(o, box);
                        break;
                    }
                    default:
                    {
                        var child = p.Info.GetValue(o);
                        if (child == null) break;
                        _path.Add((p.Info.Name, -1));
                        Walk(child, depth + 1);
                        _path.RemoveAt(_path.Count - 1);
                        break;
                    }
                }
            }
        }

        private void WalkList(System.Collections.IList list, int depth)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it is double dv)
                {
                    if (double.IsFinite(dv)) continue;
                    _path.Add(("", i)); list[i] = 0.0; Record("", dv, "0"); _path.RemoveAt(_path.Count - 1);
                }
                else if (it is float fv)
                {
                    if (float.IsFinite(fv)) continue;
                    _path.Add(("", i)); list[i] = 0f; Record("", fv, "0"); _path.RemoveAt(_path.Count - 1);
                }
                else if (it != null && it is not string)
                {
                    if (it.GetType().IsValueType)
                    {
                        // List<CanvasPoint> (a perspective grid's vanishing points): the
                        // element is a boxed copy, repair it and write it back.
                        if (!IsWalkableStruct(it.GetType())) continue;
                        int before = Count;
                        _path.Add(("", i));
                        Walk(it, depth + 1);
                        _path.RemoveAt(_path.Count - 1);
                        if (Count != before) list[i] = it;
                    }
                    else
                    {
                        _path.Add(("", i));
                        Walk(it, depth + 1);
                        _path.RemoveAt(_path.Count - 1);
                    }
                }
            }
        }
    }

    /// <summary>Every non-finite double or float anywhere in the library back to
    /// the property's shipped default (0 when that is not finite either; a
    /// nullable is cleared to null), plus the cheap repairs. Slow on a 50 MB
    /// library, so only the serialise-failure path calls it.</summary>
    private static List<string> RepairEverything(Library lib)
    {
        var r = RepairCheap(lib);
        var walk = new NumberRepairWalk();
        walk.Walk(lib);
        r.AddRange(walk.Repairs);
        if (walk.Count > walk.Repairs.Count)
            r.Add($"... and {walk.Count - walk.Repairs.Count} more numbers repaired the same way");
        return r;
    }

    public static void Save(Library lib)
    {
        if (!_savingEnabled) return;
        var repairs = RepairCheap(lib);
        PersistSettings(lib);
        // Serialise on the caller's (UI) thread so the model can't mutate
        // mid-write, then push the actual file IO to a worker (#52).
        string json;
        try { json = JsonSerializer.Serialize(lib, Opts); _serializeError = null; }
        catch (Exception ex)
        {
            // Never crash the app on save - and never be SILENT about it either:
            // this catch used to be a bare "return", which turned one bad number
            // into a session in which nothing was ever saved again.
            if (!RecoverFromSerializeFailure(lib, ex, repairs, out json)) return;   // SAVE-RECOVERY
        }
        NoteRepairs(repairs, "on save");
        // Stage 0 op log: diff against the shadow and append change ops (#collab)
        try { SyncLog.OnSaved(lib); }
        catch (Exception ex)
        {
            LogProblemOnce("SyncLog.OnSaved", $"op log (SyncLog.OnSaved) failed, the save itself goes on: {ex.GetType().Name}: {ex.Message}");
        }
        // Chain, don't race: independent Task.Run writes serialise on _writeLock
        // but acquire it in arbitrary order, so an OLDER json could land after a
        // newer one and silently revert it. Chaining also makes Flush's single
        // wait cover every queued write instead of only the last one queued.
        _lastWrite = _lastWrite.ContinueWith(_ => WriteAll(json),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // Which Library properties are SETTINGS rather than note content. Everything
    // the user can change from the Settings panel (and the toolbars that write
    // back into the library) belongs here; notes, folders, recents and calculator
    // state stay in library.json alone.
    private static readonly string[] SettingFields =
    {
        "DefaultBackground", "DefaultGrid", "DefaultGridSpacing", "DefaultPaper",
        "Theme", "ThemeSource", "Language", "DefaultFont", "DefaultFontSize",
        "DefaultTextColor", "PenDock",
        "NotebookPanelW", "NotebookPanelH", "StartMaximised", "StartOnGallery",
        "AccentColor", "TouchMode", "Liquidness", "RecentColors", "CustomColors",
        "Palettes", "ColorUses",
        "LastEraserMode", "LastEraserStyle", "EraserSize", "GlowMode",
        "AccentFollow", "KeyPreset", "OledBlack", "AutosaveSeconds",
        "PenRepair", "PenRepairDots", "PenRepairBridge", "MotionBlur",
        "ShowCommentPins", "HiddenTools", "KeyOverrides", "Pens",
        "AiProvider", "AiModel", "AiEndpoint",
        "PanelFontScale", "PanelFontOverrides",
        "MeasureSystem", "MeasureUnit", "MeasureFormat", "MeasurePrecision",
        "ShowStrokeLength", "ShowSelectionScale", "CustomPageColor",
        "WinX", "WinY", "WinW", "WinH", "WinMaximized"
    };

    /// <summary>New name -> the name that key had in a settings.json written
    /// before it was renamed. The mirror is keyed by REFLECTION NAME, so a rename
    /// silently orphans the stored value: the new name misses, the setting falls
    /// back to whatever library.json holds, and since the mirror exists precisely
    /// because library.json can be a stale snapshot, that is the case where the
    /// user's choice is lost. <see cref="Library.StartFullscreen"/>'s legacy
    /// property covers library.json; this covers the mirror.
    ///
    /// <para>Read-only compatibility: <see cref="PersistSettings"/> builds a fresh
    /// dictionary from the CURRENT names, so the old key is gone from the file
    /// after the first save either way.</para></summary>
    private static readonly Dictionary<string, string> RenamedSettings = new()
    {
        ["StartMaximised"] = "StartFullscreen",
    };

    private static IEnumerable<System.Reflection.PropertyInfo> SettingProps()
    {
        foreach (var n in SettingFields)
        {
            var p = typeof(Library).GetProperty(n);
            if (p != null && p.CanRead && p.CanWrite) yield return p;
        }
    }

    /// <summary>Mirrors the library's settings into settings.json. Called on every
    /// save AND the moment a setting changes, so a settings change is durable
    /// without waiting for — or depending on — the debounced 53 MB library write.
    /// Writes only when a value actually changed, so a 1.5s autosave does not turn
    /// into a second file write.</summary>
    public static void PersistSettings(Library lib)
    {
        try
        {
            // The mirror serialises the same doubles; one NaN here used to stop
            // the mirror (and, via Save, the library) silently.
            var fixes = new List<string>();
            RepairLibraryDoubles(lib, fixes);
            NoteRepairs(fixes, "on save");
            var cur = Settings.Settings;
            var next = new Dictionary<string, JsonElement>();
            bool changed = cur == null;
            foreach (var p in SettingProps())
            {
                var el = JsonSerializer.SerializeToElement(p.GetValue(lib), Opts);
                next[p.Name] = el;
                if (!changed && (!cur!.TryGetValue(p.Name, out var old) ||
                                 old.GetRawText() != el.GetRawText())) changed = true;
            }
            if (!changed) return;
            Settings.Settings = next;
            SyncUiHints(lib);
            SaveSettings();
        }
        catch (Exception ex)
        {
            LogProblemOnce("PersistSettings", $"settings mirror (PersistSettings) failed, settings.json was not updated: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Applies the mirrored settings onto a freshly loaded library, so a
    /// library.json written by another instance from ITS stale snapshot cannot
    /// revert a setting. On the first run after the mirror was introduced nothing
    /// is stored yet, and then the library SEEDS the mirror rather than the other
    /// way round — adding this can never wipe settings the user already had.</summary>
    public static void ApplySettings(Library lib)
    {
        try
        {
            var stored = Settings.Settings;
            if (stored == null) { PersistSettings(lib); return; }
            foreach (var p in SettingProps())
            {
                if (!stored.TryGetValue(p.Name, out var el) &&
                    !(RenamedSettings.TryGetValue(p.Name, out var was) &&
                      stored.TryGetValue(was, out el))) continue;
                // a hand-edited or older settings.json must never break startup
                try { p.SetValue(lib, JsonSerializer.Deserialize(el.GetRawText(), p.PropertyType, Opts)); }
                catch { }
            }
        }
        catch { }
    }

    // The early-paint hints are read before library.json has parsed at all, so
    // they are kept in step whenever the mirror is written.
    private static void SyncUiHints(Library lib)
    {
        var h = Settings.Ui ??= new UiHints();
        h.Theme = lib.Theme; h.OledBlack = lib.OledBlack; h.Accent = lib.AccentColor;
        h.WinX = lib.WinX; h.WinY = lib.WinY; h.WinW = lib.WinW; h.WinH = lib.WinH;
        h.WinMaximized = lib.WinMaximized; h.StartMaximised = lib.StartMaximised;
    }

    /// <summary>Blocks briefly until the last queued write hits disk — called
    /// on app close so a fire-and-forget save can't be lost.</summary>
    public static void Flush()
    {
        // 53 MB plus a rolling snapshot does not always finish in 4s, and a
        // close that gives up early loses every change since the last save.
        try { _lastWrite.Wait(15000); } catch { }
    }

    /// <summary>Non-null when a foreign writer (a second Quill instance, a sync
    /// client, another machine) changed library.json under us. Their version was
    /// archived next to it before we wrote ours.</summary>
    public static string? ConflictWarning { get; private set; }

    // Conflict copies are 70 MB each; two instances ping-ponging once produced
    // twenty of them. One archive per 5 minutes is enough to preserve the other
    // writer's work without filling the disk.
    private static DateTime _lastConflictCopyUtc = DateTime.MinValue;

    private static void WriteAll(string json)
    {
        lock (_writeLock)
        {
            // Conflict guard: if another writer (sync client, second machine,
            // second instance) touched library.json since our last save,
            // preserve their version before overwriting it (#52).
            //
            // The baseline is armed by Load (see _lastOwnWriteUtc there): it used
            // to stay DateTime.MinValue until our FIRST write, so the first save
            // of a session — exactly the one that clobbers whatever the other
            // instance wrote while we were loading — skipped the check entirely.
            try
            {
                if (_lastOwnWriteUtc != DateTime.MinValue && File.Exists(FilePath) &&
                    File.GetLastWriteTimeUtc(FilePath) > _lastOwnWriteUtc.AddSeconds(2))
                {
                    if (DateTime.UtcNow - _lastConflictCopyUtc > TimeSpan.FromMinutes(5))
                    {
                        var archive = Path.Combine(Dir, $"library.conflict-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                        File.Copy(FilePath, archive, true);
                        _lastConflictCopyUtc = DateTime.UtcNow;
                        ConflictWarning =
                            "Another Quill instance (or a sync client) changed\n" +
                            $"{FilePath}\nwhile this window had it open. Their version was archived as\n" +
                            $"{Path.GetFileName(archive)}\nbefore this window's copy was written. " +
                            "Close the other instance — Quill cannot merge whole-library writes.";
                    }
                }
            }
            catch { }
            WriteCore(json);
            // Only re-arm the baseline when the write actually landed: after a
            // failed write the file on disk is still the foreign one, and
            // adopting its timestamp would silence the guard next time round.
            // The FILE outcome (_writeError), not the combined WriteError: a failed
            // serialise on the UI thread says nothing about this write, and reading it
            // here would skip the baseline and raise a false "another program
            // changed the file" warning on the next good save.
            if (_writeError == null)
                try { _lastOwnWriteUtc = File.GetLastWriteTimeUtc(FilePath); } catch { }
        }
    }

    /// <summary>Non-null when the last library write could not reach
    /// library.json. The work was parked in <see cref="PendingPath"/> instead —
    /// nothing was lost, but the live file is now behind.</summary>
    public static string? WriteError
    {
        // A failed SERIALISE (nothing reached the worker) is reported through the
        // same property the status bar already polls, and - unlike a file-write
        // failure, which the next good write clears - it is only cleared by the
        // next save that serialises.
        get => _writeError ?? _serializeError;
        private set => _writeError = value;
    }
    private static string? _writeError;

    /// <summary>Where a save goes when library.json cannot be replaced. A single
    /// stable name, so a locked file cannot fill the disk with 70 MB copies.</summary>
    public static string PendingPath => Path.Combine(Dir, "library.pending.json");

    // Temp file + flush + atomic replace. The destination is NEVER opened for
    // writing, so a failure or a crash at any point leaves the previous file
    // exactly as it was. Throws if it could not complete.
    private static void WriteAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        WriteTemp(tmp, json);
        PromoteTemp(tmp, path);
    }

    /// <summary>Writes the payload to a scratch file and forces it all the way to
    /// the platter. Nothing is promoted until this has returned, so a partially
    /// written temp can never become the live file.</summary>
    private static void WriteTemp(string tmp, string json)
    {
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs);
        sw.Write(json);
        sw.Flush();       // StreamWriter buffer + encoder -> the FileStream
        fs.Flush(true);   // to the physical disk, not just the OS cache
    }

    /// <summary>Swaps a finished temp in. Always with a backup name: Win32
    /// ReplaceFile only guarantees that the replaced file keeps its own name on a
    /// late failure when one is supplied — with a null backup a failure at the
    /// wrong moment can leave the destination gone entirely.</summary>
    private static void PromoteTemp(string tmp, string path)
    {
        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }

    private static void WriteCore(string json)
    {
        Exception? last = null;
        try { Directory.CreateDirectory(Dir); } catch (Exception ex) { last = ex; }

        // Serialise to disk ONCE. The retry loop used to call WriteAtomic, which
        // re-wrote the whole payload on every attempt — five full copies of a
        // 70 MB library per save while the file was locked, and another one to
        // park it. Only the promote can realistically fail, so only it is retried.
        var tmp = FilePath + ".tmp";
        bool haveTemp = false;
        try { WriteTemp(tmp, json); haveTemp = true; }
        catch (Exception ex) { last = ex; }

        // File.Replace legitimately fails when a sync client, antivirus or a
        // second instance momentarily holds library.json open, and that is
        // almost always over within a second — so retry briefly first.
        if (haveTemp)
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    PromoteTemp(tmp, FilePath);
                    WriteError = null;
                    // the live library is current again, so any parked copy is obsolete
                    foreach (var stale in new[] { PendingPath, PendingPath + ".bak", PendingPath + ".tmp" })
                        try { if (File.Exists(stale)) File.Delete(stale); } catch { }
                    TrySnapshot(json);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 3) Thread.Sleep(150 * (attempt + 1));
                }
            }

        // Still failing. The old fallback here was File.WriteAllText(FilePath, …),
        // which OPENS THE LIVE LIBRARY FOR TRUNCATION — a crash or a second
        // failure part-way through that write destroys the notes. Never do that:
        // park the save under a distinct name and report the failure instead.
        // library.json keeps its last good content either way.
        bool parked = false;
        try
        {
            // The temp is already written AND flushed, so parking it is a rename,
            // not another full-size write. MoveFileEx(REPLACE_EXISTING) swaps the
            // directory entry; it never opens the destination for truncation.
            if (haveTemp && File.Exists(tmp)) { File.Move(tmp, PendingPath, true); parked = true; }
        }
        catch (Exception ex) { last = ex; }
        if (!parked)
        {
            try { WriteAtomic(PendingPath, json); parked = true; }
            catch (Exception ex)
            {
                WriteError = $"Quill could not save to\n{FilePath}\nor to\n{PendingPath}\n\n{ex.Message}";
            }
        }
        if (parked)
            WriteError = $"Quill could not update\n{FilePath}\n" +
                         "(another program has it open). Your work was saved to\n" +
                         $"{PendingPath}\ninstead — close the other program, or rename that " +
                         $"file to library.json to recover it.\n\n{last?.Message}";
        // don't leave a stale ".tmp" (containing note data) behind.
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        try { if (File.Exists(PendingPath + ".tmp")) File.Delete(PendingPath + ".tmp"); } catch { }

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
            // A snapshot is a RECOVERY file, so it must never be a half-written one
            // that still looks restorable: same temp + flush + rename. The temp is
            // deliberately named so the "library-*.json" scan above cannot see it.
            var snapTmp = Path.Combine(BackupDir, "snapshot.writing.tmp");
            WriteTemp(snapTmp, json);
            File.Move(snapTmp, Path.Combine(BackupDir, name), true);

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
    /// <summary>The reason the last trash write failed, for the caller to show.</summary>
    public static string? TrashError { get; private set; }

    /// <summary>Persists the bin. Returns false if it could NOT reach the disk —
    /// callers must not commit a destructive change on a false.</summary>
    private static bool SaveTrash()
    {
        if (!_savingEnabled) { TrashError = "saving is not enabled yet"; return false; }
        if (_trash == null) { TrashError = "the trash bin is not loaded"; return false; }
        string json;
        try { json = JsonSerializer.Serialize(_trash, Opts); }
        catch (Exception ex) { TrashError = ex.Message; return false; }
        lock (_trashLock)
        {
            Exception? last = null;
            try { Directory.CreateDirectory(Dir); } catch (Exception ex) { last = ex; }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    // temp + flush + atomic replace; trash.json is never opened
                    // for truncation, so a failure leaves the old bin intact.
                    WriteAtomic(TrashPath, json);
                    TrashError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 2) Thread.Sleep(120 * (attempt + 1));
                }
            }
            // The old code fell back to File.WriteAllText(TrashPath, …) here,
            // truncating the live bin. Report the failure instead: the caller
            // aborts the delete, so nothing is lost.
            try { if (File.Exists(TrashPath + ".tmp")) File.Delete(TrashPath + ".tmp"); } catch { }
            TrashError = last?.Message ?? "the trash file could not be written";
            return false;
        }
    }

    /// <summary>Files an entry in the bin and makes sure it is ON DISK. Returns
    /// false (leaving the in-memory bin exactly as it was) if it could not be
    /// persisted, so the caller can abort rather than destroy the item.</summary>
    private static bool PushTrash(TrashEntry e)
    {
        var bin = Trash;
        List<TrashEntry> trimmed = new();
        lock (_trashLock)
        {
            bin.Items.Insert(0, e);                 // newest first
            // Hard cap so a bin left untended cannot grow without bound; oldest go first.
            while (bin.Items.Count > TrashBin.MaxItems)
            {
                trimmed.Add(bin.Items[bin.Items.Count - 1]);
                bin.Items.RemoveAt(bin.Items.Count - 1);
            }
        }
        if (SaveTrash()) return true;

        // roll the bin back so memory matches what is actually on disk
        lock (_trashLock)
        {
            bin.Items.Remove(e);
            for (int i = trimmed.Count - 1; i >= 0; i--) bin.Items.Add(trimmed[i]);
        }
        return false;
    }

    // A soft delete is only soft if the bin write actually lands. These used to
    // remove the item from the library FIRST and then push to the trash with a
    // SaveTrash that swallowed every error — so a locked or full disk turned a
    // "move to trash" into permanent destruction. The bin write now happens
    // first and the removal is committed only once it has reached the disk.

    /// <summary>Soft-deletes a notebook: files it in the bin with its original
    /// index so Restore can put it back, then removes it from the library.
    /// Returns false (leaving the notebook in place) if the bin write failed.</summary>
    public static bool DeleteNotebook(Library lib, Notebook nb)
    {
        int idx = lib.Notebooks.IndexOf(nb);
        if (idx < 0) return false;
        if (!PushTrash(new TrashEntry
        {
            Kind = TrashItemKind.Notebook,
            Name = nb.Name,
            OriginalIndex = idx,
            Notebook = nb
        })) return false;
        lib.Notebooks.RemoveAt(idx);
        PruneRecents(lib);   // its pages are gone from the tree now
        Save(lib);
        // 4.2: every page under this notebook just left the live tree for
        // certain (it is still intact inside the trash entry above, for
        // Restore, but no longer reachable from the gallery) - reclaim their
        // cached thumbs/ PNGs now rather than leaving them for ever. If the
        // notebook is later restored, GetAsync just re-renders on next ask.
        ThumbnailCache.Forget(nb.Sections.SelectMany(s => s.Pages).Select(p => p.Id));
        return true;
    }

    /// <summary>Soft-deletes a section, remembering its parent notebook and index.
    /// Returns false (leaving the section in place) if the bin write failed.</summary>
    public static bool DeleteSection(Library lib, Notebook parent, Section sec)
    {
        int idx = parent.Sections.IndexOf(sec);
        if (idx < 0) return false;
        if (!PushTrash(new TrashEntry
        {
            Kind = TrashItemKind.Section,
            Name = sec.Name,
            ParentNotebookId = parent.Id,
            OriginalIndex = idx,
            Section = sec
        })) return false;
        parent.Sections.RemoveAt(idx);
        PruneRecents(lib);
        Save(lib);
        // 4.2: same reasoning as DeleteNotebook - these pages are confirmed
        // gone from the live tree right now, not inferred from a page list
        // that could be stale.
        ThumbnailCache.Forget(sec.Pages.Select(p => p.Id));
        return true;
    }

    /// <summary>Soft-deletes a page, remembering its notebook, section and index.
    /// Returns false (leaving the page in place) if the bin write failed.</summary>
    public static bool DeletePage(Library lib, Notebook nb, Section sec, NotePage page)
    {
        int idx = sec.Pages.IndexOf(page);
        if (idx < 0) return false;
        if (!PushTrash(new TrashEntry
        {
            Kind = TrashItemKind.Page,
            Name = page.Name,
            ParentNotebookId = nb.Id,
            ParentSectionId = sec.Id,
            OriginalIndex = idx,
            Page = page
        })) return false;
        sec.Pages.RemoveAt(idx);
        PruneRecents(lib);
        Save(lib);
        // 4.2 ("thumbs/ keeps PNGs of deleted pages indefinitely"): this page
        // is confirmed gone from the live tree at this exact line - reclaim
        // its cached thumbnail now instead of leaving it in thumbs/ forever.
        ThumbnailCache.Forget(page.Id);
        return true;
    }

    /// <summary>Restores a bin entry to its original location. If the original
    /// parent is gone, it falls back to a sensible home (an existing container,
    /// or a freshly-made "Recovered" one) rather than dropping the item. Returns
    /// false if the entry is missing or already back in the tree.</summary>
    public static bool Restore(Library lib, Guid entryId)
    {
        if (!_savingEnabled) return false;   // nothing may be moved while writes are gated
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
            // The mirror image of the delete ordering, and it was missing: while a
            // restore is in flight the item exists ONLY in the bin. Dropping the
            // bin entry and then firing a library save that is merely QUEUED — and
            // that can end up parked in library.pending.json — leaves the item in
            // neither file on the next launch. So commit the library write and
            // confirm it landed before forgetting the bin copy; if it did not, the
            // entry stays put and the worst case is a harmless duplicate.
            Save(lib);
            Flush();
            if (WriteError == null)
            {
                lock (_trashLock) { bin.Items.Remove(e); }
                SaveTrash();
            }
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
        // persistence rides the caller's debounced save - a page flip must not
        // trigger a full multi-MB library write by itself
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
