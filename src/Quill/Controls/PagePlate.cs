using System;
using Quill.Models;
using Quill.Services;
using Windows.UI;

namespace Quill.Controls;

/// <summary>§24: THE ONE FORMULA EVERY CHROME GROUND IS DERIVED FROM, AND IT
/// READS THE PAGE THE USER IS DRAWING ON.
///
/// <para><b>Why this exists at all.</b> <see cref="PageTheme.Ground"/> is NOT
/// the paper. <c>MainWindow.ResolveGround</c> returns the page's paper only when
/// <c>ThemeSource == "Page"</c>, and that field defaults to <c>"Manual"</c> - so
/// on a default install <c>PageTheme.Ground</c> is a fixed shell colour that
/// knows nothing about the paper. Every surface that wanted "the colour of the
/// page" and reached for <c>PageTheme.Ground</c> got the shell instead. §17.2's
/// corner plates were measured byte-identical <c>#0F0E10</c> on six different
/// papers across three screen runs for exactly that reason, reaching 18.77:1
/// against Plain White - the maximum possible contrast, on the paper a
/// note-taking app is most likely used on, from a section that asked for "a
/// background that mimics the page colour".</para>
///
/// <para><b>The formula.</b> One expression, two call sites, one endpoint each:
/// <code>
///     plate = grey * (1 - t) + page * t
/// </code>
/// <see cref="Full"/> (t = 1) collapses the grey out entirely and returns the
/// page's own colour - that is §17.2's corner plates, which mimic the paper.
/// <see cref="Tint"/> (t = 0.40) is the dial's plates: a grey that has taken
/// four tenths of the page's colour, "inhibiting" it as the user put it.
/// They are the same line of arithmetic evaluated at its two ends, which is the
/// point of the two rulings landing together - they cannot drift apart because
/// there is nothing to drift.</para>
///
/// <para><b>Where t = 0.40 and the base grey come from - the user's own worked
/// example, not a guess.</b> "make them inhibit the colour of the background
/// slightly, like slightly blueish grey (#7EA0B9) if blueprint is selected."
/// Blueprint's ground is <c>#2E80C2</c>. Solving the mix per channel against the
/// stated target gives t = 0.403 / 0.385 / 0.357 for a base of 180, and at a
/// round t = 0.40 the formula returns <c>#7E9FBA</c> - within one unit of
/// <c>#7EA0B9</c> on all three channels. So <see cref="LightBase"/> is
/// <c>#B4B4B4</c> and <see cref="Tint"/> is 0.40, and both are the user's
/// numbers rather than this file's.</para>
///
/// <para><b><see cref="DarkBase"/> = #4B4B4B, and it was chosen here.</b> The
/// user specified only "dark grey (for black background)". 75 is the byte-axis
/// reflection of 180 about mid-grey, so the two bases are one number written
/// twice; and measured, it makes the plate as findable on a pure black page
/// (1.52:1 against the page) as the light base makes it on Plain White
/// (1.49:1). Solving for equal findability exactly lands on 72.1 and the L*
/// reflection lands on 63.1; 75 is inside 4% of the first and is the roundest of
/// the three. See §24 of docs/CONCEPTS-REF-2026-08-07.md for the working.</para>
///
/// <para><b>THE SPLIT THAT PICKS THE BASE IS L*, NOT LUMINANCE - and this is
/// the one place §24 had to depart from the obvious reading.</b> The user's own
/// example makes Blueprint a LIGHT case: <c>#7EA0B9</c> can only come out of the
/// light base. But §7 names Blueprint one of the three grounds that are DARK,
/// and <see cref="PageTheme.IsDark"/> agrees - Blueprint's relative luminance is
/// 0.199, far under 0.5. Both are right, because they are answers to different
/// questions. "Does the chrome have to invert?" is photometric and is settled by
/// luminance. "Is this page blackish or lightish?" is perceptual and is settled
/// by L*, where Blueprint sits at 51.7 and Brown Paper at 52.5 - just above the
/// perceptual midpoint - while Darkprint is at 17.3 and a pinned dark shell at
/// 4.1. So the base is chosen on <see cref="PageTheme.Lightness"/> against
/// <see cref="BaseSplit"/>, and the chrome still inverts on luminance exactly as
/// §7 requires. The margin at the top is thin and is stated rather than hidden:
/// Blueprint clears the split by 1.74 L*.
///
/// What is NOT departed from: the ground is judged with a gamma-correct
/// measure. <c>ColorUtil.IsDark</c> averages raw bytes and puts Brown Paper on
/// the wrong side of any threshold at all, which is the trap
/// <c>ToolWheel.PlateFor</c> was written to avoid and this inherits.</para>
///
/// <para><b>§0'S RULE, AND IT IS NOT OPTIONAL HERE.</b> Moving a ground moves
/// every mark standing on it. <see cref="Ink"/> is the re-keyed mark for a
/// plate at <see cref="Full"/>; a plate at <see cref="Tint"/> takes
/// <c>ToolWheel.BestInk</c>, which resolves white or black against the plate
/// itself. Measured, keeping the old pairing would have shipped §17.4's exact
/// fault a third time: <c>PageTheme.OnSurface</c> on the new Blueprint plate is
/// 2.48:1 and on Brown Paper 2.42:1, both under the 3:1 floor for a non-text
/// mark, while the re-keyed mark is 7.56:1 and 7.74:1.</para></summary>
public static class PagePlate
{
    /// <summary>§17.2's corner plates: the page's colour, nothing added. At t = 1
    /// the base grey drops out of the mix entirely, so this endpoint is the paper
    /// exactly - "the plate is meant to be the same colour as the paper, and the
    /// only thing distinguishing it is that the grain and the grid stop at its
    /// edge".</summary>
    public const double Full = 1.00;

    /// <summary>The dial's plates: four tenths of the page's colour carried into
    /// the base grey. The user's number, solved out of their own example - see
    /// this class's remarks.</summary>
    public const double Tint = 0.40;

    /// <summary>§27: PANELS. Three tenths of the page's colour - the third
    /// endpoint of the same formula, and the FURTHEST of the three from the
    /// page.
    ///
    /// <para><b>The ruling.</b> Asked what a panel should do on a white page
    /// after §24 left panels out of scope and the user found a black Settings
    /// slab on white graph paper, the answer was <i>"Follow the page, but stay
    /// heavier."</i> Follow = it is derived from the page like everything else
    /// §24 touched. Heavier = it sits FURTHER from the page than the chrome
    /// does, so it still reads as a surface floating over the paper rather than
    /// blending into it. <see cref="Full"/> is the page exactly and
    /// <see cref="Tint"/> is four tenths of the way back to it; this is three
    /// tenths, and lower <i>t</i> is literally "further from the page".</para>
    ///
    /// <para><b>Where 0.30 comes from - it is the largest value the floor
    /// allows, not a taste.</b> <see cref="PanelSeparation"/> fixes the minimum
    /// lightness gap a panel may have from its page. <i>t</i> is then chosen as
    /// the LARGEST value at which no shipped paper has to be rescued by that
    /// floor - largest, because every hundredth of <i>t</i> given up is a
    /// hundredth of the page's colour the panel stops carrying. Darkprint binds
    /// first: its ground is L* 17.28 and its base grey <see cref="DarkBase"/> is
    /// L* 31.89, so the gap the mix leaves shrinks as t rises and crosses the
    /// floor at <b>t = 0.328</b>, bisected by <c>tools/PanelProof</c> against
    /// this very method. 0.30 is the round number under that. At 0.30 the worst
    /// gap over all nine shipped papers is <b>10.23 L* (Darkprint)</b> and the
    /// clamp never fires on shipped stock; at 0.35 it would be 9.69 and
    /// Darkprint's panel would be a clamped colour rather than a mixed one. §27
    /// of docs/CONCEPTS-REF-2026-08-07.md has the table.</para>
    ///
    /// <para><b>What it costs, stated rather than hidden.</b> The mix runs
    /// toward a NEUTRAL grey, so t is a chroma dial as well as a lightness one:
    /// a panel now carries three tenths of the page's cast where the superseded
    /// <c>PageTheme</c> ramp carried 0.85 of its a/b. A warm paper's panel is
    /// warm-tinted grey rather than cream. That is the direct price of
    /// "heavier", it is one constant, and raising it is safe because the floor
    /// below catches whatever the raise would have collapsed.</para></summary>
    public const double PanelT = 0.30;

    /// <summary>§27: THE SEPARATION FLOOR - the minimum L* a panel may sit from
    /// its page, in either direction.
    ///
    /// <para><b>Why a floor exists at all.</b> The mix collapses wherever the
    /// page happens to land on the base grey's own lightness: a page at L* 73
    /// takes <see cref="LightBase"/> (#B4B4B4, L* 73.31) and <c>Of</c> returns
    /// very nearly the page itself at every <i>t</i>. That is a mid-tone paper,
    /// and it is exactly the case the chrome does not have - §17.2's corner
    /// plates are MEANT to disappear into the page and are stated that way
    /// (1.33:1 to 1.52:1 findability, §24.7), while a Settings panel that
    /// disappears is a defect. Without a floor, "follow the page" would ship a
    /// panel with no edge on any paper near L* 73.31 or L* 31.89
    /// (<see cref="DarkBase"/>). A custom page colour reaches both, and
    /// <c>tools/PanelProof</c> section 3 shows the clamp firing on exactly those
    /// two greys and on nothing else in its list.</para>
    ///
    /// <para>This paragraph read <i>"a page at L* 72 takes LightBase (L*
    /// 72.4)"</i> and both figures were about one L* out. §27's harness asserts
    /// them on every run now (section 7), which is how that was found - the
    /// comment was written from the intended value rather than measured.</para>
    ///
    /// <para><b>10.0, and it is a perceptual number.</b> A just-noticeable L*
    /// step is about 2.3; 10 is a little over four of them, which is a step
    /// nobody has to hunt for across a large flat area and is still far short of
    /// the ~30 L* a 3:1 mark-versus-ground contrast would demand. It is a
    /// SEPARATION floor, not a contrast floor: the panel has to be findable
    /// against the page, not legible against it.</para></summary>
    public const double PanelSeparation = 10.0;

    /// <summary>The grey a LIGHT page's plates are built on. #B4B4B4, solved from
    /// the user's Blueprint example.</summary>
    public static readonly Color LightBase = Color.FromArgb(255, 0xB4, 0xB4, 0xB4);

    /// <summary>The grey a DARK page's plates are built on. #4B4B4B, chosen here
    /// and justified in this class's remarks - the user specified only "dark
    /// grey".</summary>
    public static readonly Color DarkBase = Color.FromArgb(255, 0x4B, 0x4B, 0x4B);

    /// <summary>The L* at which the base switches. The PERCEPTUAL midpoint, not
    /// luminance's 0.5 - see this class's remarks for why those two disagree
    /// about Blueprint and Brown Paper and why both are right.</summary>
    public const double BaseSplit = 50.0;

    /// <summary>WCAG's floor for a non-text mark. Stated here because §24 tests
    /// against it and because §17.19 states the same number.</summary>
    public const double MarkFloor = 3.0;

    /// <summary>The live page's ground - the paper's own colour for Blueprint /
    /// Brown Paper / Darkprint, the page's background otherwise. The same helper
    /// <c>MainWindow.ResolveGround</c> uses for its own Page branch, so the two
    /// can never answer differently about one page.
    ///
    /// <para>Falls back to <see cref="PageTheme.Ground"/> with no page - the
    /// gallery and startup, where there is no paper to read and the shell's own
    /// ground is the only truthful answer.</para></summary>
    public static Color Ground(NotePage? page)
    {
        if (page == null) return PageTheme.Ground;
        try { return PaperTextures.Ground(page); }
        catch { return PageTheme.Ground; }
    }

    /// <summary>Which base a ground takes. See this class's remarks: L*, not
    /// luminance, and the two deliberately disagree about Blueprint.</summary>
    public static bool BaseIsDark(Color ground) => PageTheme.Lightness(ground) < BaseSplit;

    /// <summary>THE FORMULA. <paramref name="t"/> is <see cref="Full"/> for
    /// §17.2's corner plates, <see cref="Tint"/> for the dial's and
    /// <see cref="PanelT"/> for a panel's.</summary>
    public static Color Of(Color ground, double t) =>
        Mix(BaseIsDark(ground) ? DarkBase : LightBase, ground, t);

    /// <summary>§27: A PANEL'S GROUND - the formula at <see cref="PanelT"/>,
    /// held at least <see cref="PanelSeparation"/> L* away from the page.
    ///
    /// <para>The clamp keeps the panel's a/b and moves only its L*, so a page
    /// that would have collapsed the mix still gets a panel in its own hue - it
    /// is pushed off the page, not repainted grey. The direction is whichever
    /// way the mix was already leaning, because that lean is the one the base
    /// grey chose and reversing it would put a light panel on a lighter page.
    /// A dead tie (page exactly on the base) leans AWAY from the page's own end
    /// of the axis, and if that would run off 0..100 it takes the other side.</para>
    ///
    /// <para>Fully opaque, like every other endpoint here: a panel establishes
    /// its own ground or the marks on it are being judged against the paper.</para></summary>
    public static Color Panel(Color ground) => Panel(ground, PanelT, PanelSeparation);

    /// <inheritdoc cref="Panel(Color)"/>
    /// <remarks>The parameterised form exists so the acceptance harness can
    /// sweep <paramref name="t"/> and <paramref name="separation"/> against the
    /// SHIPPED arithmetic rather than against a transcription of it - §24.7's
    /// method, and the reason the two constants above can be quoted as
    /// measurements.</remarks>
    public static Color Panel(Color ground, double t, double separation)
    {
        var panel = Of(ground, t);
        double lg = PageTheme.Lightness(ground);
        double gap = PageTheme.Lightness(panel) - lg;
        if (Math.Abs(gap) >= separation) return panel;

        double dir = gap > 0 ? 1 : gap < 0 ? -1 : (lg >= BaseSplit ? -1 : 1);
        double want = lg + dir * separation;
        if (want < 0 || want > 100) want = lg - dir * separation;
        return PageTheme.WithLightness(panel, Math.Clamp(want, 0, 100));
    }

    /// <summary>§0/§24.6: THE MARK FOR A PANEL, JUDGED AGAINST THE PANEL.
    ///
    /// <para>Moving a ground moves every mark standing on it, and panels used to
    /// take <c>PageTheme.OnSurface</c> - which is selected by the SHELL's
    /// luminance, the very ground a panel has just stopped using. Left alone
    /// that is §17.4's defect a fourth time, and the default install is its
    /// worst case: a pinned dark shell puts <c>InkOnDark</c> (#F2F2F2) on a
    /// panel that is now light-grey-derived-from-white paper, which is around
    /// 1.5:1 - invisible.</para>
    ///
    /// <para>A best-of rather than <see cref="Ink"/>'s luminance threshold, and
    /// the difference is deliberate. <see cref="Ink"/> keeps §7's ruling that
    /// Blueprint, Brown Paper and Darkprint carry WHITE chrome; that ruling is
    /// about chrome standing on the paper. A panel is not the paper - it is a
    /// surface the formula has already moved off the paper - so the only
    /// question left is which of the two marks reads on it, and §17.19's sweep
    /// is what makes a best-of safe: it can never do worse than 4.583:1 for
    /// pure black/white, and 4.31:1 for the two inks actually used.</para></summary>
    public static Color PanelInk(Color panel) =>
        Contrast(PageTheme.InkOnLight, panel) >= Contrast(PageTheme.InkOnDark, panel)
            ? PageTheme.InkOnLight
            : PageTheme.InkOnDark;

    /// <summary>The mark for a plate at <see cref="Full"/> - i.e. for a mark
    /// standing on the page's own colour.
    ///
    /// <para>This is <see cref="PageTheme.OnSurface"/>'s own rule, re-keyed to
    /// the PAGE instead of to the shell's ground. Deliberately not a best-of
    /// contrast pick: §7 rules that Blueprint, Brown Paper and Darkprint carry
    /// WHITE chrome, a best-of would flip the first two to black, and this is
    /// the token those three papers' whole appearance is stated in. Measured
    /// over the nine shipped papers it runs 3.66:1 (Brown Paper) to 17.96:1
    /// (Plain White), all clear of the 3:1 floor.</para></summary>
    public static Color Ink(Color ground) =>
        PageTheme.Luminance(ground) < 0.5 ? PageTheme.InkOnDark : PageTheme.InkOnLight;

    /// <summary>WCAG 2.x contrast ratio, 1..21, off the same gamma-correct
    /// luminance the rest of the shell decides light from dark on.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = PageTheme.Luminance(a), lb = PageTheme.Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Linear per-channel mix, opaque out. Deliberately NOT an alpha
    /// blend and deliberately NOT interpolating alpha: a plate has to establish
    /// its own ground, and the dial already has one Mix that interpolates alpha
    /// and one §17.19-era defect caused by it.</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        static byte C(byte x, byte y, double f) =>
            (byte)Math.Clamp(Math.Round(x + (y - x) * f), 0, 255);
        return Color.FromArgb(255, C(a.R, b.R, t), C(a.G, b.G, t), C(a.B, b.B, t));
    }
}
