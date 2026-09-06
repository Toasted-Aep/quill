// The three types the linked shipping files need from outside their own
// assembly, and NOTHING else. Every colour decision stays in the linked source.

namespace Windows.UI
{
    /// <summary>Stand-in for the WinRT struct. Byte channels, same constructor
    /// shape, same field names - the linked files only ever read A/R/G/B and
    /// call FromArgb.</summary>
    public readonly struct Color : IEquatable<Color>
    {
        public byte A { get; init; }
        public byte R { get; init; }
        public byte G { get; init; }
        public byte B { get; init; }

        public static Color FromArgb(byte a, byte r, byte g, byte b) =>
            new() { A = a, R = r, G = g, B = b };

        public bool Equals(Color o) => A == o.A && R == o.R && G == o.G && B == o.B;
        public override bool Equals(object? o) => o is Color c && Equals(c);
        public override int GetHashCode() => (A << 24) | (R << 16) | (G << 8) | B;
        public static bool operator ==(Color x, Color y) => x.Equals(y);
        public static bool operator !=(Color x, Color y) => !x.Equals(y);
        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
    }
}

namespace Quill.Models
{
    /// <summary>Only the two fields PaperTextures.Ground reads.</summary>
    public sealed class NotePage
    {
        public string? Paper { get; set; }
        public string? Background { get; set; }
    }
}

namespace Quill.Controls
{
    /// <summary>PagePlate.Ground(NotePage) calls this. The harness works from
    /// explicit ground colours and never goes through that overload, so this
    /// only has to exist and to be honest about what it is.</summary>
    public static class PaperTextures
    {
        public static Windows.UI.Color Ground(Quill.Models.NotePage? page) =>
            throw new NotSupportedException(
                "PanelProof drives PagePlate from explicit grounds; the page overload is not shimmed.");
    }
}
