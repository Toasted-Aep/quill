using System;
using Windows.UI;

namespace Quill.Services;

/// <summary>
/// The one place the shell's colours come from.
///
/// Quill's chrome does not have a light theme and a dark theme. It has a theme
/// derived from the PAGE BACKGROUND: a blue page produces blue chrome, a kraft
/// page produces brown chrome, a near-black page produces slate chrome. Light
/// and dark fall out of that as a consequence of the ground's luminance, not as
/// a setting the user picks.
///
/// Every new surface reads these statics. Nothing in new chrome reads
/// Settings.Theme directly - that flag now only chooses whether the derivation
/// runs from the page (ThemeSource = "Page") or from a fixed ground the user
/// pinned (ThemeSource = "Manual").
///
/// Derivation is documented in docs/CONCEPTS-REF-2026-08-07.md section 6, with
/// the five observed Concepts proof points in section 7. Those five cases are
/// the acceptance test: change the maths and they must still come out right.
/// </summary>
public static class PageTheme
{
    /// <summary>The page's ground colour - the paper base, or the flat colour
    /// for Blueprint / Brown Paper / Darkprint. Everything else derives from it.</summary>
    public static Color Ground { get; private set; } = Color.FromArgb(255, 0xFA, 0xFA, 0xFA);

    /// <summary>True when the ground is dark enough that chrome must invert.
    /// Threshold is relative luminance 0.5, which puts Blueprint (0.21) and
    /// Brown Paper (0.20) on the dark side exactly as the reference shows.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Fill for the dial's inner disc, the pen row, chips and any
    /// raised element sitting directly on the page. Carries the ground's hue.</summary>
    public static Color Surface { get; private set; }

    /// <summary>One step further from the ground than <see cref="Surface"/>.
    /// Section heading bands, selected chips, popover backdrops.</summary>
    public static Color SurfaceAlt { get; private set; }

    /// <summary>Primary text and icons on <see cref="Surface"/>.</summary>
    public static Color OnSurface { get; private set; }

    /// <summary>Secondary text - captions, subtitles, inactive labels.</summary>
    public static Color OnSurfaceMuted { get; private set; }

    /// <summary>Hairline dividers, sector separators, unselected swatch rings.</summary>
    public static Color Outline { get; private set; }

    /// <summary>Floating window fill: Settings, Export, Brushes, Objects. Unlike
    /// <see cref="Surface"/> this is near-neutral - the reference panels are a
    /// flat #F7F7F7 or #141414 regardless of the page's hue.</summary>
    public static Color Panel { get; private set; }

    /// <summary>Links and primary buttons. The user's accent, untouched by the
    /// page - it is their choice, not the paper's.</summary>
    public static Color Accent { get; set; } = Color.FromArgb(255, 0xD9, 0x77, 0x57);

    /// <summary>How much of the ground's colour a PANEL carries.
    ///
    /// <para>Panels used to be neutral by design - section 6 had them flat
    /// whatever the page's hue, unlike Surface which carries it. The user asked
    /// for the opposite: cream on a warm paper, and the equivalent elsewhere.
    /// Light panels take most of the ground's a/b, because that is where cream
    /// lives and where a tint reads easily at all; dark panels take far less, so
    /// a dark surface only hints at its page instead of becoming a coloured
    /// slab.</para></summary>
    private const double LightPanelChroma = 0.85;
    private const double DarkPanelChroma = 0.35;

    /// <summary>The light panel band, in L*.
    ///
    /// <para>The user supplied a swatch - a near-white neutral, about #F5F5F5,
    /// L* 96 - and asked for the light grey to be that colour. The band was
    /// 90..97, which still left the greyer papers visibly below white. At
    /// 95..97.5 a typical paper lands on the supplied value, a mid-light ground
    /// on about #F1F1F1 and pure white on about #F9F9F9 - narrow, but not flat,
    /// so the papers stay distinguishable from one another.</para></summary>
    private const double LightPanelFloor = 95.0;
    private const double LightPanelRange = 2.5;

    /// <summary>Raised whenever the ground changes and every surface must repaint.</summary>
    public static event Action? Changed;

    static PageTheme() => Apply(Ground);

    /// <summary>Point every surface at a new page ground. Cheap and idempotent;
    /// <see cref="Changed"/> only fires when the ground actually moved.</summary>
    public static void SetGround(Color ground)
    {
        if (ground.R == Ground.R && ground.G == Ground.G && ground.B == Ground.B) return;
        Apply(ground);
        Changed?.Invoke();
    }

    private static void Apply(Color g)
    {
        Ground = g;
        IsDark = Luminance(g) < 0.5;

        var (L, a, b) = ToLab(g);
        // A near-white page needs a DARKER raised surface; anything else needs a
        // lighter one. Without the split, paper would get a white-on-white disc.
        double sl = L > 80 ? L - 15 : L + 18;
        Surface = FromLab(sl, a * 0.55, b * 0.55);
        SurfaceAlt = FromLab(L > 80 ? sl - 4 : sl + 4, a * 0.55, b * 0.55);

        OnSurface = IsDark ? Color.FromArgb(255, 0xF2, 0xF2, 0xF2)
                           : Color.FromArgb(255, 0x14, 0x14, 0x14);
        OnSurfaceMuted = WithAlpha(OnSurface, 140);
        Outline = WithAlpha(OnSurface, 36);
        // A RAMP, not a switch. Panel used to be one of two constants, so an
        // ivory page and a pure white one produced an identical panel and so did
        // a near-black page and a merely dark one. It now tracks the ground:
        // white -> L* 97, black -> L* 8, everything between interpolated on
        // relative luminance. Luminance rather than L* because that is the axis
        // IsDark is decided on, so the panel's shade and the text's colour can
        // never disagree about which side of the middle a page sits.
        //
        // The clamps are a legibility floor, not taste: they hold the panel away
        // from the text that will sit on it, which is at its worst exactly at
        // the middle where IsDark flips.
        //
        // Panels also CARRY THE GROUND'S HUE now. They were neutral by design -
        // section 6 had them flat whatever the page's colour, unlike Surface -
        // and the user asked for the opposite: cream on a warm paper, and the
        // equivalent elsewhere. Light panels take most of the ground's a/b,
        // because that is where cream lives and where a tint reads easily; dark
        // panels take less, so a dark surface hints at its page rather than
        // becoming a coloured slab.
        double gy = Luminance(g);
        if (IsDark)
        {
            // A ground that is very nearly black gets a panel that IS black -
            // the user asked for OLED black there specifically, so Darkprint and
            // a pinned black both land on #000000 rather than easing toward it.
            // The step at 0.05 is the point of the rule, not a rough edge, and
            // black keeps ZERO chroma: black asked for is black, not near-black
            // wearing a cast.
            Panel = gy <= 0.05
                ? Color.FromArgb(255, 0, 0, 0)
                : FromLab(10.0 + 16.0 * Math.Min(1.0, gy / 0.5),
                          a * DarkPanelChroma, b * DarkPanelChroma);
        }
        else
        {
            // L* 90..97 across the top half of the luminance range. The previous
            // 84..97 put Heavyweight on #D1D1D1 - grey rather than off-white -
            // and the user asked for the light end to be lighter. The papers
            // stay distinguishable from one another inside the narrower band,
            // and the carried chroma is what turns a warm paper's panel cream.
            double t = Math.Clamp((gy - 0.5) / 0.5, 0.0, 1.0);
            Panel = FromLab(LightPanelFloor + LightPanelRange * t,
                            a * LightPanelChroma, b * LightPanelChroma);
        }
        Probe();
    }

    /// <summary>A one-line hex dump of the whole derived palette, for the
    /// acceptance pass over the section 7 proof points. "It looks right" is not
    /// a measurement of a colour, and the surfaces that carry these are drawn
    /// into a Win2D canvas and into WinUI popups, neither of which a UIA client
    /// can read a brush out of - so the palette reports itself instead.</summary>
    public static string Describe() =>
        $"ground={Hex(Ground)} isDark={(IsDark ? 1 : 0)} lum={Luminance(Ground):F4} " +
        $"surface={Hex(Surface)} surfaceAlt={Hex(SurfaceAlt)} onSurface={Hex(OnSurface)} " +
        $"onSurfaceMuted={Hex(OnSurfaceMuted)} outline={Hex(Outline)} panel={Hex(Panel)} accent={Hex(Accent)}";

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    // Off unless QUILL_THEME_PROBE names a file. Resolved once: this runs inside
    // Apply, which runs on every page turn and on every frame of a background
    // drag, and an environment read per frame is not free.
    private static readonly string? ProbePath =
        Environment.GetEnvironmentVariable("QUILL_THEME_PROBE") is { Length: > 0 } p ? p : null;

    private static void Probe()
    {
        if (ProbePath == null) return;
        try { System.IO.File.AppendAllText(ProbePath, Describe() + Environment.NewLine); }
        catch { }
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Relative luminance, gamma-correct. Averaging the raw bytes is
    /// wrong by enough to put Brown Paper on the wrong side of the threshold.</summary>
    public static double Luminance(Color c)
    {
        static double Lin(double v) { v /= 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    // ---- CIELAB, D65. Shifting lightness in L* keeps hue and saturation put;
    // shifting it in HSL does not, which is why blue grounds used to grey out.
    private static (double L, double a, double b) ToLab(Color c)
    {
        static double Lin(double v) { v /= 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double r = Lin(c.R), g = Lin(c.G), bl = Lin(c.B);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * bl) / 0.95047;
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * bl;
        double z = (0.0193 * r + 0.1192 * g + 0.9505 * bl) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static Color FromLab(double L, double a, double b)
    {
        L = Math.Clamp(L, 0, 100);
        double fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double G(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116.0) / 7.787;
        double x = G(fx) * 0.95047, y = G(fy), z = G(fz) * 1.08883;
        double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        double gg = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        double bb = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        static byte S(double v)
        {
            v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }
        return Color.FromArgb(255, S(r), S(gg), S(bb));
    }
}
