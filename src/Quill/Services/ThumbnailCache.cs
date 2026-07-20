using Quill.Models;

namespace Quill.Services;

/// <summary>
/// Disk-backed page thumbnails for the gallery, living in a thumbs/ folder next
/// to library.json. The cache key carries a content stamp, so an edited page
/// lands on a different key, re-renders once, and the stale file is swept away.
/// Rendering always happens on a worker thread — the gallery never waits.
/// </summary>
public static class ThumbnailCache
{
    private static string Dir => Path.Combine(LibraryStore.Dir, "thumbs");

    // In-process memo so re-opening the gallery does not even hit the disk.
    private static readonly Dictionary<string, byte[]> Mem = new();
    private static readonly object Gate = new();

    private static ulong MixNum(ulong h, long v)
    {
        for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; }
        return h;
    }

    // Deliberately NOT string.GetHashCode(): that is randomised per process, so
    // a disk key built from it would miss on every launch.
    private static ulong MixStr(ulong h, string? s)
    {
        if (s == null) return MixNum(h, -1);
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>Cheap stamp over everything that changes the picture.</summary>
    public static string Stamp(NotePage page)
    {
        ulong h = 14695981039346656037UL;
        h = MixStr(h, page.Background);
        h = MixNum(h, page.Strokes.Count);
        h = MixNum(h, page.Shapes.Count);
        h = MixNum(h, page.Texts.Count);
        foreach (var s in page.Strokes)
        {
            h = MixNum(h, s.Points.Count);
            h = MixNum(h, (long)(s.Size * 16));
            h = MixStr(h, s.Color);
            if (s.Points.Count == 0) continue;
            var a = s.Points[0];
            var b = s.Points[^1];
            h = MixNum(h, (long)(a.X * 8)); h = MixNum(h, (long)(a.Y * 8));
            h = MixNum(h, (long)(b.X * 8)); h = MixNum(h, (long)(b.Y * 8));
        }
        foreach (var sh in page.Shapes)
        {
            h = MixNum(h, (int)sh.Kind);
            h = MixNum(h, (long)(sh.X * 8)); h = MixNum(h, (long)(sh.Y * 8));
            h = MixNum(h, (long)(sh.W * 8)); h = MixNum(h, (long)(sh.H * 8));
            h = MixStr(h, sh.Color);
        }
        foreach (var t in page.Texts)
        {
            h = MixNum(h, (long)(t.X * 8)); h = MixNum(h, (long)(t.Y * 8));
            h = MixNum(h, (long)(t.Width * 8));
            h = MixStr(h, t.Rtf);
        }
        return h.ToString("x16");
    }

    /// <summary>
    /// Returns PNG bytes for the page, or null when there is nothing worth
    /// showing (empty page, render failure, disk trouble) — callers keep their
    /// own fallback in that case.
    /// </summary>
    public static Task<byte[]?> GetAsync(NotePage page, int w, int h, bool cropToContent)
    {
        // The stamp walks the page's lists, so take it on the caller's thread
        // (the UI thread) where those lists are not being mutated underneath us.
        string shape = $"{page.Id:N}-{w}x{h}{(cropToContent ? "c" : "")}";
        string key = $"{shape}-{Stamp(page)}";

        lock (Gate)
        {
            if (Mem.TryGetValue(key, out var hit)) return Task.FromResult<byte[]?>(hit);
        }

        return Task.Run<byte[]?>(() =>
        {
            string path = Path.Combine(Dir, key + ".png");
            try
            {
                if (File.Exists(path))
                {
                    var cached = File.ReadAllBytes(path);
                    if (cached.Length > 0) { Remember(key, cached); return cached; }
                }
            }
            catch { }

            byte[]? bytes;
            try { bytes = Controls.InkSurface.RenderPageThumbnail(page, w, h, cropToContent); }
            catch { bytes = null; }
            if (bytes == null) return null;

            Remember(key, bytes);
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(path, bytes);
                Sweep(shape, key);
            }
            catch { }   // a thumbnail that cannot be cached is still a thumbnail
            return bytes;
        });
    }

    private static void Remember(string key, byte[] bytes)
    {
        lock (Gate)
        {
            if (Mem.Count > 256) Mem.Clear();
            Mem[key] = bytes;
        }
    }

    // Drop earlier stamps for this page/size — the page changed, they are dead.
    private static void Sweep(string shape, string keepKey)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, shape + "-*.png"))
                if (!Path.GetFileNameWithoutExtension(f).Equals(keepKey, StringComparison.Ordinal))
                    File.Delete(f);
        }
        catch { }
    }
}
