// The one type the linked shipping files need from outside their own assembly,
// and NOTHING else.
//
// Item 4.2 gave LibraryStore.DeleteNotebook/DeleteSection/DeletePage a call to
// ThumbnailCache.Forget, so LibraryStore.cs no longer compiles on its own. The
// real ThumbnailCache cannot be linked here: it calls
// Controls.InkSurface.RenderPageThumbnail, which is the Win2D surface.
//
// Forget() removes a page's cached PNGs from disk. This harness has no cache
// directory, so doing nothing IS what it does here - the stand-in is honest
// rather than merely convenient. Nothing in these round-trips asserts on
// thumbnails; if that ever changes, this file is the thing to notice.
namespace Quill.Services
{
    internal static class ThumbnailCache
    {
        public static void Forget(System.Guid pageId) { }
        public static void Forget(System.Collections.Generic.IEnumerable<System.Guid> pageIds) { }
    }
}
