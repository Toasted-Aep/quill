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
        try
        {
            if (File.Exists(FilePath))
            {
                var lib = JsonSerializer.Deserialize<Library>(File.ReadAllText(FilePath), Opts);
                if (lib != null && lib.Notebooks.Count > 0) return lib;
            }
        }
        catch
        {
            // corrupted file: keep a backup, start fresh
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
        }
        return Seed();
    }

    public static void Save(Library lib)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(lib, Opts));
        }
        catch
        {
            // never crash the app on save
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
