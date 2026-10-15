// The one type the linked shipping files need from outside their own assembly.
// Same stand-in and same reason as tools/LayerRoundTrip/Shim.cs: LibraryStore's
// delete paths call ThumbnailCache.Forget, the real cache renders through Win2D
// and cannot be linked, and this harness has no cache directory, so doing
// nothing IS what it does here.
namespace Quill.Services
{
    internal static class ThumbnailCache
    {
        public static void Forget(System.Guid pageId) { }
        public static void Forget(System.Collections.Generic.IEnumerable<System.Guid> pageIds) { }
    }
}
