using System.Numerics;
using Windows.UI;

namespace Quill.Helpers;

public static class ColorUtil
{
    public static Color Parse(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            byte a = 255;
            int i = 0;
            if (hex.Length == 8) { a = Convert.ToByte(hex.Substring(0, 2), 16); i = 2; }
            byte r = Convert.ToByte(hex.Substring(i, 2), 16);
            byte g = Convert.ToByte(hex.Substring(i + 2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(i + 4, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }
        catch
        {
            return Color.FromArgb(255, 26, 26, 26);
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool IsDark(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 100;
}

public static class GeometryUtil
{
    public static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    public static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
