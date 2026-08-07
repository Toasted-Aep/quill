using Windows.UI;

namespace Quill.Helpers;

/// <summary>
/// Subtractive (paint-like) colour mixing - UI-SPEC-V3 K.12.
///
/// WHY NOT AN RGB LERP. Averaging two sRGB colours models two LIGHTS added
/// together, not two PAINTS stirred together. Paint is subtractive: each pigment
/// absorbs part of the spectrum and the mixture reflects only what BOTH still
/// reflect. An RGB (or even a linear-light) average of blue and yellow is a
/// neutral grey; a real mixture is green (docs/ARTMODE-RESEARCH.md, "Why RGB
/// mixing is wrong": "blue and yellow make gray instead of green").
///
/// HOW THIS WORKS. Three steps, no solver at runtime.
///
///   1. DECOMPOSE. Linear sRGB is split into non-negative amounts of seven
///      pigment primaries - white, cyan, magenta, yellow, red, green, blue - by
///      peeling off the neutral first and then the one secondary and the one
///      primary that can still remain. The split is exact and unique, and every
///      weight is >= 0, which is what keeps the reflectance curve physical.
///   2. UPSAMPLE. The curve is the weighted sum of those primaries' reflectance
///      spectra (36 bands, 380..730 nm at 10 nm), <see cref="Primaries"/>.
///   3. MIX. R_mix(l) = R_a(l)^(1-t) * R_b(l)^t - a weighted geometric mean,
///      i.e. an arithmetic mean of LOG reflectance, which is what makes it
///      behave like stacked absorption rather than added light. Then integrate
///      against the CIE observer (<see cref="RenderR"/> and friends) and back to
///      sRGB.
///
/// WHY THE PRIMARIES ARE PIGMENT-SHAPED AND NOT "SMOOTHEST". The obvious
/// upsampling is the smoothest curve that reproduces the colour exactly
/// (minimise squared second differences subject to the CIE integral matching the
/// target). That was tried first AND IT DOES NOT WORK HERE, for a reason worth
/// recording so it is not tried again: the smoothest metamer of sRGB's blue
/// primary is a narrow block from 380 to 460 nm with essentially no green-band
/// content, so blue + yellow has almost nothing to reflect in common and lands
/// on a desaturated steel blue (#6C8397) - visibly the "blue and yellow make
/// gray" failure this whole feature exists to avoid. Exact rendering is what
/// forbids the green: sRGB blue has a linear G of exactly 0, and any reflectance
/// in the green band adds G that nothing else can cancel once the curve is held
/// at or above zero. Widening the curve under an exact-render constraint is
/// therefore not available - the constraint is what has to give.
///
/// So the primaries here are instead authored the way a real pigment behaves - a
/// broad band with soft (smoothstep) shoulders, and in blue's case a deliberate
/// low plateau across the green - and the small colorimetric difference between
/// that pigment-plausible curve and the exact sRGB target is carried alongside as
/// a linear residual. Because the residual is defined as "target minus what the
/// curve renders" and is carried with the same weights as the curve,
/// <see cref="Mix"/>(a, b, 0) returns EXACTLY a and Mix(a, b, 1) EXACTLY b, for
/// every colour, with no round-trip drift - verified over 5000 random pairs.
///
/// WHAT IT PRODUCES at t = 0.5:
///   blue + yellow    #3DA06B   a real green - the acceptance test
///   blue -> yellow   ramps #0000FF, #006FA5, #3DA06B, #90CD43, #FFFF00
///   COPIC B29 + Y15  #4CA558   leaf green
///   red + yellow     #F88231   orange
///   blue + red       #7F0063   purple
///   red + green      #956D00   olive - correct for paint, NOT a bug
///   blue + white     #4B92FF   still blue, not cyan
///   white + black    #656565   mid grey
///   grey + grey      #808080   exact
///
/// LICENCE. Nothing here is ported. The CIE observer tables are measurement, the
/// primaries were authored and fitted for this file, and the mixing law is the
/// classical weighted geometric mean of reflectance. Mixbox is the other
/// well-known option and is deliberately NOT used: it is CC BY-NC, so it cannot
/// ship here.
/// </summary>
public static class PigmentMix
{
    private const int Bands = 36;

    // Reflectance can never be zero (a pigment always scatters a little) and a
    // zero would send the geometric mean to zero for the whole mixture. 0.02 is
    // the value that keeps white+black at a believable mid grey rather than
    // collapsing it to near-black.
    private const float Floor = 0.02f;

    /// <summary>The seven pigment primaries' reflectance spectra, 36 bands each
    /// (380..730 nm at 10 nm), row-major in the order white, cyan, magenta,
    /// yellow, red, green, blue - the same order <see cref="Decompose"/> builds
    /// its weights in. Authored as smoothstep-edged bands and fitted so the
    /// mixtures listed in the class remarks come out right.</summary>
    private static readonly float[] Primaries =
    {
        // white
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        // cyan
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 0.9767f,
        0.8174f, 0.5680f, 0.3005f, 0.1400f, 0.1400f, 0.1296f,
        0.1037f, 0.0700f, 0.0363f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        // magenta
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        0.8960f, 0.6480f, 0.3520f, 0.1040f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.1040f, 0.3520f, 0.6480f,
        0.8960f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        // yellow
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0280f, 0.2160f, 0.5000f, 0.7840f, 0.9720f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        // red
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0280f, 0.2160f, 0.5000f, 0.7840f,
        0.9720f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        // green
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0280f, 0.2160f, 0.5000f, 0.7840f,
        0.9720f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 0.9128f, 0.6995f, 0.4320f, 0.1826f, 0.0233f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        // blue
        1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f, 1.0000f,
        1.0000f, 1.0000f, 1.0000f, 0.9720f, 0.7840f, 0.5000f,
        0.2160f, 0.1400f, 0.1400f, 0.1400f, 0.1400f, 0.1376f,
        0.1211f, 0.0938f, 0.0619f, 0.0316f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
        0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f, 0.0200f,
    };

    /// <summary>The inverse leg: linear sRGB = Render . R. Rows R, G, B; each 36
    /// long. This is the CIE 1931 observer under an equal-energy illuminant,
    /// chromatically adapted to D65 and multiplied through the XYZ-to-sRGB
    /// matrix, so a perfect reflector (R == 1 everywhere) renders exactly
    /// (1, 1, 1).</summary>
    private static readonly float[] RenderR =
    {
        -0.000321f, -0.000596f, 0.000059f, 0.003377f, 0.006656f, 0.006863f, 0.012589f, 0.003835f,
        -0.011887f, -0.023440f, -0.032144f, -0.045077f, -0.060206f, -0.075036f, -0.085699f, -0.080945f,
        -0.056544f, -0.018189f, 0.031324f, 0.086329f, 0.140108f, 0.184857f, 0.213383f, 0.216589f,
        0.192587f, 0.151585f, 0.106142f, 0.066253f, 0.036865f, 0.018250f, 0.008001f, 0.003078f,
        0.001020f, 0.000278f, 0.000055f, 0.000002f,
    };

    private static readonly float[] RenderG =
    {
        0.000055f, 0.000029f, -0.000517f, -0.002896f, -0.008433f, -0.015890f, -0.020277f, -0.016285f,
        -0.007394f, 0.004668f, 0.019130f, 0.036505f, 0.058391f, 0.086933f, 0.118274f, 0.138790f,
        0.143041f, 0.137062f, 0.121888f, 0.100045f, 0.073643f, 0.045752f, 0.020205f, 0.001523f,
        -0.008075f, -0.010387f, -0.008479f, -0.005220f, -0.002404f, -0.000665f, 0.000115f, 0.000314f,
        0.000267f, 0.000166f, 0.000086f, 0.000039f,
    };

    private static readonly float[] RenderB =
    {
        0.000723f, 0.002215f, 0.006585f, 0.022190f, 0.070885f, 0.150557f, 0.188317f, 0.193078f,
        0.180478f, 0.138826f, 0.085174f, 0.046367f, 0.023019f, 0.007409f, -0.003935f, -0.011120f,
        -0.014612f, -0.015874f, -0.015544f, -0.014163f, -0.012038f, -0.009484f, -0.006871f, -0.004596f,
        -0.002905f, -0.001781f, -0.001096f, -0.000693f, -0.000448f, -0.000288f, -0.000177f, -0.000102f,
        -0.000054f, -0.000027f, -0.000012f, -0.000005f,
    };

    /// <summary>Mixes <paramref name="a"/> with <paramref name="b"/> as PAINT.
    /// <paramref name="t"/> is how much of <paramref name="b"/> goes in: 0 returns
    /// a unchanged, 1 returns b unchanged, 0.5 is an equal mixture. Alpha follows
    /// the same ratio linearly (it is coverage, not pigment).</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        float w = (float)Math.Clamp(t, 0, 1);
        if (w <= 0f) return a;
        if (w >= 1f) return b;

        Span<float> ra = stackalloc float[Bands];
        Span<float> rb = stackalloc float[Bands];
        var resA = Decompose(a, ra);
        var resB = Decompose(b, rb);

        Span<float> mix = stackalloc float[Bands];
        for (int i = 0; i < Bands; i++)
            // R_a^(1-w) * R_b^w, i.e. the arithmetic mean of log reflectance.
            mix[i] = MathF.Exp((1 - w) * MathF.Log(ra[i]) + w * MathF.Log(rb[i]));

        var (r, g, bl) = Render(mix);
        r += (1 - w) * resA.R + w * resB.R;
        g += (1 - w) * resA.G + w * resB.G;
        bl += (1 - w) * resA.B + w * resB.B;

        byte alpha = (byte)Math.Clamp(Math.Round(a.A * (1 - w) + b.A * w), 0, 255);
        return Color.FromArgb(alpha, Encode(r), Encode(g), Encode(bl));
    }

    /// <summary>Fills <paramref name="curve"/> with the colour's pigment
    /// reflectance and returns the linear-RGB residual the pigment basis could not
    /// reach. Splitting linear RGB into the seven primaries is exact: peel off the
    /// neutral, then the one secondary and the one primary that can still be
    /// non-zero once a channel has been driven to zero.</summary>
    private static (float R, float G, float B) Decompose(Color c, Span<float> curve)
    {
        float lr = Decode(c.R), lg = Decode(c.G), lb = Decode(c.B);
        float r = lr, g = lg, b = lb;

        float white = MathF.Min(r, MathF.Min(g, b));
        r -= white; g -= white; b -= white;
        // At least one of r/g/b is now zero, so at most one of these is non-zero.
        float cyan = MathF.Min(g, b), magenta = MathF.Min(r, b), yellow = MathF.Min(r, g);
        r -= magenta + yellow; g -= cyan + yellow; b -= cyan + magenta;

        Span<float> w = stackalloc float[7]
        {
            white, cyan, magenta, yellow,
            MathF.Max(r, 0f), MathF.Max(g, 0f), MathF.Max(b, 0f),
        };

        for (int i = 0; i < Bands; i++)
        {
            float v = 0f;
            for (int p = 0; p < 7; p++) v += w[p] * Primaries[p * Bands + i];
            curve[i] = Math.Clamp(v, Floor, 1f);
        }
        var (rr, rg, rb) = Render(curve);
        return (lr - rr, lg - rg, lb - rb);
    }

    private static (float R, float G, float B) Render(ReadOnlySpan<float> curve)
    {
        float r = 0, g = 0, b = 0;
        for (int i = 0; i < Bands; i++)
        {
            float v = curve[i];
            r += RenderR[i] * v;
            g += RenderG[i] * v;
            b += RenderB[i] * v;
        }
        return (r, g, b);
    }

    // sRGB transfer function, both directions.
    private static float Decode(byte v)
    {
        float c = v / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static byte Encode(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        float s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(s * 255f), 0, 255);
    }
}
