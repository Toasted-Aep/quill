using Windows.Foundation;

namespace Quill.Helpers;

/// <summary>
/// A measurement channel for the two surfaces that cannot be measured from
/// outside the process.
///
/// <para>Reference 10.2 item 8 - "the COPIC wheel is still not centred on the
/// dial" - has now been raised three times, and every previous attempt was
/// judged by eye. It cannot be judged any other way from a screenshot: the
/// dial's colour dot is an Ellipse whose Fill a UIA client cannot read, and the
/// wheel's centre is a float inside a Win2D drawing pass with no element to
/// hang a bounding rectangle off. Both of them therefore state their own
/// position here, in the SAME coordinate space, and the acceptance test is that
/// the two lines carry the same numbers.</para>
///
/// <para>Off unless <c>QUILL_GEOM_PROBE</c> names a file, resolved once for the
/// same reason PageTheme resolves its own: these run inside paint paths.</para>
/// </summary>
public static class GeometryProbe
{
    private static readonly string? Path =
        System.Environment.GetEnvironmentVariable("QUILL_GEOM_PROBE") is { Length: > 0 } p ? p : null;

    public static bool On => Path != null;

    public static void Write(string tag, string body)
    {
        if (Path == null) return;
        try { System.IO.File.AppendAllText(Path, tag + " " + body + System.Environment.NewLine); }
        catch { }
    }

    public static void Point(string tag, Point p, string extra = "") =>
        Write(tag, $"x={p.X:F2} y={p.Y:F2}" + (extra.Length > 0 ? " " + extra : ""));
}
