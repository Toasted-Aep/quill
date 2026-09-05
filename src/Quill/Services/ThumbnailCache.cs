using Quill.Models;
using Windows.UI;

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
    public static Task<byte[]?> GetAsync(NotePage page, int w, int h, bool cropToContent, Color? bgOverride = null)
    {
        // The stamp walks the page's lists, so take it on the caller's thread
        // (the UI thread) where those lists are not being mutated underneath us.
        string shape = $"{page.Id:N}-{w}x{h}{(cropToContent ? "c" : "")}";
        // The override colour is part of the picture, so it rides in the key: a
        // recoloured notebook must re-render its cover. It sits after the stamp,
        // inside the shape prefix, so Sweep still reaps the old-colour file.
        string ov = bgOverride is Color oc ? $"o{oc.R:X2}{oc.G:X2}{oc.B:X2}" : "";
        string key = $"{shape}-{Stamp(page)}{ov}";

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
            try { bytes = Controls.InkSurface.RenderPageThumbnail(page, w, h, cropToContent, bgOverride); }
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

    /// <summary>4.2: reclaims every cached thumbnail file (every size, crop and
    /// background-override variant) for one page whose removal from the live
    /// library the CALLER has just positively confirmed — e.g. it was just
    /// spliced out of a Section's Pages list, or a sync op just deleted it.
    ///
    /// <para>Deliberately NOT a reconciliation sweep that diffs the thumbs/
    /// folder against "the current set of live page ids": building that set
    /// requires walking the whole library at exactly the right moment, and a
    /// stale or partial snapshot (mid-load, a page a caller forgot to include)
    /// would delete a live page's thumbnail — which then either regenerates
    /// (wasted work) or, if the render path is also having a bad day, silently
    /// does not (the exact hazard the roadmap named). Keying off the page id
    /// the caller already knows is gone removes that failure mode entirely:
    /// there is nothing to reconcile, so there is nothing to get stale.</para>
    ///
    /// <para>Safe to call for a page that turns out to have no cached
    /// thumbnail yet (never rendered) or none on disk any more — this is
    /// cache invalidation, not the deletion of record.</para></summary>
    public static void Forget(Guid pageId)
    {
        string prefix = pageId.ToString("N") + "-";
        lock (Gate)
        {
            foreach (var k in Mem.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                Mem.Remove(k);
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, prefix + "*.png"))
                try { File.Delete(f); } catch { }
        }
        catch { }   // Dir may not exist yet — nothing to reclaim either way
    }

    /// <summary>Convenience for a notebook/section delete, which drags every
    /// page under it out of the live tree at once.</summary>
    public static void Forget(IEnumerable<Guid> pageIds)
    {
        foreach (var id in pageIds) Forget(id);
    }
}
