using System.Text.Json;
using LectureInk.Models;

namespace LectureInk.Services;

public static class LibraryStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LectureInk");

    public static string FilePath => Path.Combine(Dir, "library.json");

    public static Library Load()
    {
        // Try the primary file, then the last known-good backup. Each read is
        // isolated so a CORRUPT primary (parse throws) still falls through to
        // the ".bak" recovery instead of short-circuiting to a fresh seed.
        return TryRead(FilePath, preserveCorrupt: true)
            ?? TryRead(FilePath + ".bak", preserveCorrupt: false)
            ?? Seed();
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

    public static void Save(Library lib)
    {
        string json;
        try { json = JsonSerializer.Serialize(lib, Opts); }
        catch { return; } // unserializable model: never crash the app on save

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
}
