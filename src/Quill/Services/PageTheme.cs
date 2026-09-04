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

    /// <summary>§27: THE PAGE THE USER IS ACTUALLY DRAWING ON, which is NOT
    /// <see cref="Ground"/>.
    ///
    /// <para>The two are the same colour only when <c>ThemeSource == "Page"</c>,
    /// and that field defaults to <c>"Manual"</c> - so on a default install
    /// <see cref="Ground"/> is a pinned shell colour that knows nothing about
    /// the paper. §24 fixed that for the chrome by reading the page directly at
    /// each call site. <see cref="Panel"/> could not do the same, because a
    /// panel's ground is read from static factories with no page in reach
    /// (<c>BottomMenu.Plate</c> is the plainest case), so the page is published
    /// here instead - once, by <c>MainWindow.PushGround</c>, through
    /// <see cref="SetGrounds"/>.</para>
    ///
    /// <para>Falls back to the shell's ground with no page: the gallery and
    /// startup, where there is no paper to read.</para></summary>
    public static Color PageGround { get; private set; } = Color.FromArgb(255, 0xFA, 0xFA, 0xFA);

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

    /// <summary>Floating window fill: Settings, Export, Brushes, Objects, the
    /// bottom mode bar, the grid editor's bar and the selection pill.
    ///
    /// <para><b>§27: DERIVED FROM <see cref="PageGround"/>, not from
    /// <see cref="Ground"/>.</b> It used to be a ramp off the shell's ground,
    /// which is how a default install came to show a BLACK Settings panel on
    /// white graph paper - the user's report, verbatim: <i>"the app theme is
    /// black in a white page what is this?"</i>. The ruling was <i>"Follow the
    /// page, but stay heavier"</i>, and that is
    /// <see cref="Quill.Controls.PagePlate.Panel(Color)"/>: §24's one formula at
    /// a third endpoint, with a separation floor so a mid-tone paper cannot
    /// swallow it.</para>
    ///
    /// <para>What was lost with the old ramp, said plainly: it carried 0.85 of
    /// the page's a/b and sat in an L* 95..97.5 band, so a warm paper's panel
    /// was cream. The mix runs toward a neutral grey, so the new panel carries
    /// 0.30 of the cast and is a tinted grey instead. That is the price of
    /// "heavier" and it is one constant - see <c>PagePlate.PanelT</c>.</para></summary>
    public static Color Panel { get; private set; }

    /// <summary>§0/§24.6: the mark for <see cref="Panel"/>, judged against
    /// <see cref="Panel"/>.
    ///
    /// <para>NOT <see cref="OnSurface"/>. That one is selected by
    /// <see cref="IsDark"/>, i.e. keyed to the SHELL's ground - the ground the
    /// panel has just stopped using. A panel-standing mark that keeps it is
    /// §17.4's defect wearing a new name, and on the default install it is the
    /// worst case there is: white ink on a light panel.</para></summary>
    public static Color OnPanel { get; private set; }

    /// <summary>Secondary ink on a panel - the same relation
    /// <see cref="OnSurfaceMuted"/> has to <see cref="OnSurface"/>.</summary>
    public static Color OnPanelMuted { get; private set; }

    /// <summary>Hairlines and dividers on a panel.</summary>
    public static Color PanelOutline { get; private set; }

    /// <summary>Which side of the line <see cref="Panel"/> is on, for the stock
    /// WinUI controls inside a panel (TextBox, Slider, ComboBox) which resolve
    /// their own brushes from <c>ElementTheme</c> and not from this class.
    ///
    /// <para>Defined as "<see cref="OnPanel"/> is the light ink" rather than as
    /// a second luminance test, so the element theme and the ink this class
    /// hands out can never disagree about one panel near the crossover.</para></summary>
    public static bool PanelIsDark { get; private set; }

    /// <summary>Links and primary buttons. The user's accent, untouched by the
    /// page - it is their choice, not the paper's.</summary>
    public static Color Accent { get; set; } = Color.FromArgb(255, 0xD9, 0x77, 0x57);

    /// <summary>The two marks <see cref="OnSurface"/> chooses between, named so
    /// that a surface which has to make the SAME choice against a DIFFERENT
    /// ground can make it out of the same two colours.
    ///
    /// <para>§24 needs exactly that: the two floating bars stand on the PAGE,
    /// not on the shell's ground, and those two part company the moment
    /// ThemeSource is "Manual" - which is the default. A mark keyed to the wrong
    /// one of the two is §17.4's defect, and re-deriving the pair at the call
    /// site would be a second copy of them that could drift.</para></summary>
    public static readonly Color InkOnDark = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);

    /// <inheritdoc cref="InkOnDark"/>
    public static readonly Color InkOnLight = Color.FromArgb(255, 0x14, 0x14, 0x14);

    /// <summary>The ink TYPED WORDS take on a light page. Not the same pair as
    /// <see cref="InkOnLight"/>/<see cref="InkOnDark"/>, and deliberately so:
    /// those two are the CHROME's marks, these two are the page's own writing
    /// ink and have been #141413 / #FAF9F5 since the first text box. Named here
    /// so that the four surfaces which draw a text box - the XAML editor, the
    /// 16.7 veil, the Win2D raster and the vector exporter - stop carrying four
    /// copies of one ternary between them (CONCEPTS-REF 25.4).</summary>
    public static readonly Color TextInkOnLight = Color.FromArgb(255, 0x14, 0x14, 0x13);

    /// <inheritdoc cref="TextInkOnLight"/>
    public static readonly Color TextInkOnDark = Color.FromArgb(255, 0xFA, 0xF9, 0xF5);

    /// <summary>WCAG 2.x contrast ratio, 1..21, off <see cref="Luminance"/>.
    /// Here rather than at a call site because this file already owns the
    /// gamma-correct luminance the whole shell decides light from dark on, and a
    /// ratio computed from a second one is how two surfaces come to disagree
    /// about one page.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>CONCEPTS-REF 25.4: WHICH INK A TEXT BOX WITH NO COLOUR OF ITS
    /// OWN IS DRAWN IN - whichever of the two writing inks contrasts BETTER with
    /// the page behind it. Ties go to the dark ink, which is the ink a page with
    /// no opinion has always had.
    ///
    /// <para><b>A best-of, not a threshold, and the threshold proposed instead
    /// was tested and rejected.</b> The change that brought this here was asked
    /// whether <c>ColorUtil.IsDark</c> should become <c>Luminance &lt; 0.5</c>
    /// on the ground that IsDark averages raw bytes and puts Brown Paper on the
    /// wrong side. That is true of the judgement <c>PagePlate.BaseIsDark</c>
    /// makes and it is NOT true of this one, because this one is about contrast
    /// against two specific inks rather than about whether a ground is blackish.
    /// Swept over sRGB at a step of 5 - 140,608 colours - and scored against the
    /// better of the two inks by the ratio above:</para>
    ///
    /// <list type="bullet">
    /// <item><c>ColorUtil.IsDark</c> (weighted byte average, threshold 100)
    /// picks the WORSE ink on 8.25% of the gamut, worst case losing 2.97 ratio
    /// points - at #00AA00, 5.93:1 available and 2.95:1 chosen.</item>
    /// <item><c>Luminance &lt; 0.5</c> picks the worse ink on <b>40.06%</b>,
    /// worst case losing 7.84 - at #BEC30A, 9.66:1 available and 1.81:1 chosen.
    /// It would flip a Blueprint page (4.37 -&gt; 4.00) and a Brown Paper page
    /// (4.50 -&gt; 3.89) to the LOWER-contrast ink, which is the opposite of the
    /// improvement it was proposed as. The suggestion was right about
    /// <c>PagePlate</c> and wrong about here.</item>
    /// <item>Best-of is right by construction, and it agrees with the shipped
    /// IsDark on all nine paper grounds and on Quill's own page backgrounds - so
    /// NO EXISTING NOTE CHANGES COLOUR. Its floor over the whole gamut is where
    /// the two curves cross, at Y = 0.18827, where both inks give
    /// <b>4.183:1</b>. That clears WCAG's 3:1 everywhere and falls short of
    /// 4.5:1 only for backgrounds inside a narrow band around that crossing -
    /// stated rather than hidden, and better than either threshold manages.</item>
    /// </list>
    ///
    /// <para>Deliberately NOT <see cref="Quill.Controls.PagePlate.Ink"/>, which
    /// is a luminance threshold ON PURPOSE: that one keeps 7's ruling that
    /// Blueprint, Brown Paper and Darkprint carry WHITE CHROME. Chrome is a
    /// different question from the legibility of the user's own prose, and 25.4
    /// is the second question.</para></summary>
    public static Color TextInk(Color background) =>
        Contrast(TextInkOnLight, background) >= Contrast(TextInkOnDark, background)
            ? TextInkOnLight
            : TextInkOnDark;

    // §27 REMOVED THE PANEL RAMP that used to live here: LightPanelChroma 0.85 /
    // DarkPanelChroma 0.35, and the two L* bands 95..97.5 and 13..29. Those
    // numbers were the user's and they are not withdrawn on their merits - they
    // are withdrawn because they were applied to `Ground`, the SHELL's colour,
    // and a panel that follows the shell is the defect §27 exists to fix. The
    // ruling that replaces them - "follow the page, but stay heavier" - is one
    // formula with two constants, and both live in PagePlate beside §24's.

    /// <summary>Raised whenever either ground changes and every surface must repaint.</summary>
    public static event Action? Changed;

    static PageTheme() => Apply(Ground);

    /// <summary>Point every surface at a new shell ground, leaving
    /// <see cref="PageGround"/> where it is. Cheap and idempotent;
    /// <see cref="Changed"/> only fires when the ground actually moved.</summary>
    public static void SetGround(Color ground) => SetGrounds(ground, PageGround);

    /// <summary>§27: BOTH GROUNDS AT ONCE, and <see cref="Changed"/> fires at
    /// most once for the pair.
    ///
    /// <para>They have to move together. A page turn under
    /// <c>ThemeSource = "Page"</c> moves both; under the default <c>"Manual"</c>
    /// it moves only the page. Setting them one at a time would either repaint
    /// twice on the first case - which is the cost §24.9's guard was written to
    /// avoid - or repaint the panels from the previous page on the second.</para>
    ///
    /// <para>Returns whether it raised <see cref="Changed"/>, so a caller that
    /// keeps its own stale-page guard can tell whether the subscription has
    /// already done the work.</para></summary>
    public static bool SetGrounds(Color shell, Color page)
    {
        bool shellMoved = shell.R != Ground.R || shell.G != Ground.G || shell.B != Ground.B;
        bool pageMoved = page.R != PageGround.R || page.G != PageGround.G || page.B != PageGround.B;
        if (!shellMoved && !pageMoved) return false;
        PageGround = page;
        Apply(shell);
        Changed?.Invoke();
        return true;
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

        OnSurface = IsDark ? InkOnDark : InkOnLight;
        OnSurfaceMuted = WithAlpha(OnSurface, 140);
        Outline = WithAlpha(OnSurface, 36);
        // §27: THE PANEL IS DERIVED FROM THE PAGE, NOT FROM THE SHELL.
        //
        // It used to be a ramp off `g` - the shell's ground - carrying 0.85 of
        // its a/b into an L* 95..97.5 band (light) or 13..29 (dark). Every
        // number in that ramp was the user's, and every one of them was applied
        // to the wrong colour: `g` is the paper only when ThemeSource is "Page",
        // and that field defaults to "Manual". A pinned dark shell over white
        // graph paper therefore produced a BLACK Settings panel, which is the
        // report this section exists for.
        //
        // No adjustment to the ramp could have fixed that, for the same reason
        // §24.1 gives about the corner plates: it was the wrong SOURCE, and no
        // value read off the wrong source is right on more than one paper at a
        // time. So the ramp is gone and the panel is PagePlate's one formula at
        // its third endpoint, with a separation floor - "follow the page, but
        // stay heavier", which is the whole of the ruling.
        //
        // The marks follow in the same breath, because §0 says they must: a
        // ground that moved and a mark that did not is §17.4, and it has already
        // fired twice in this file's history.
        Panel = Quill.Controls.PagePlate.Panel(PageGround);
        OnPanel = Quill.Controls.PagePlate.PanelInk(Panel);
        PanelIsDark = OnPanel.R == InkOnDark.R && OnPanel.G == InkOnDark.G && OnPanel.B == InkOnDark.B;
        OnPanelMuted = WithAlpha(OnPanel, 143);
        PanelOutline = WithAlpha(OnPanel, 36);
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
        $"onSurfaceMuted={Hex(OnSurfaceMuted)} outline={Hex(Outline)} panel={Hex(Panel)} accent={Hex(Accent)} " +
        // §27's fields are APPENDED, never inserted: the scratchpad probes that
        // read this line key off position for the older fields.
        $"pageGround={Hex(PageGround)} panelIsDark={(PanelIsDark ? 1 : 0)} onPanel={Hex(OnPanel)} " +
        $"panelSep={Math.Abs(Lightness(Panel) - Lightness(PageGround)):F2}";

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

    /// <summary>CIE L*, perceptual lightness on 0..100.
    ///
    /// <para>NOT interchangeable with <see cref="Luminance"/>, and §24 turns on
    /// the difference. Luminance answers "does the chrome have to invert?" and
    /// puts Blueprint (0.199) and Brown Paper (0.206) firmly on the dark side,
    /// which is what §7 requires. L* answers "is this page blackish or
    /// lightish?" and puts the same two at 51.7 and 52.5 - just above the
    /// perceptual midpoint - while Darkprint sits at 17.3. Both are right about
    /// their own question. Exposed here rather than recomputed elsewhere because
    /// there is already exactly one CIELAB implementation in this file and a
    /// second copy is how two surfaces come to disagree about one page.</para></summary>
    public static double Lightness(Color c) => ToLab(c).L;

    /// <summary>The same colour at a different CIE lightness - a/b, i.e. hue and
    /// chroma, are carried through untouched.
    ///
    /// <para>§27's separation floor needs exactly this: a panel whose mix has
    /// collapsed onto its page has to be pushed OFF the page without being
    /// repainted grey. Shifting L* in CIELAB keeps hue and saturation put;
    /// shifting it in HSL does not, which is the reason this file has a CIELAB
    /// implementation at all - and the reason this helper lives here rather than
    /// beside the caller, where it would be the second copy of it.</para>
    ///
    /// <para>The round trip is not exact at the gamut edge: a colour whose a/b
    /// cannot be realised at the requested L* is clipped by <c>FromLab</c>'s
    /// per-channel clamp, so the result can come back a shade off the L* asked
    /// for. Callers that need the guarantee must re-measure, and §27's harness
    /// does.</para></summary>
    public static Color WithLightness(Color c, double lStar)
    {
        var (_, a, b) = ToLab(c);
        return FromLab(Math.Clamp(lStar, 0, 100), a, b);
    }

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
