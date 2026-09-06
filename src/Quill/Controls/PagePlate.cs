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

    /// <summary>§29/§34: THE SEAT FLOOR - the minimum contrast a TOOL SEAT may
    /// have against THE SURFACE IT STANDS ON, and it is stated as a RATIO
    /// because L* cannot see this defect.
    ///
    /// <para><b>The report.</b> <i>"the 10 tools displayed have a transparent bg
    /// when in dark ui mode"</i>, clarified as <i>"their background are
    /// transparent when page colour and indirectly the theme is black"</i> -
    /// standing against <i>"the opacity, stability, size, redo and undo
    /// backgrounds are perfect"</i>, which is the inner disc.
    /// <c>ToolWheel.Refresh</c> filled BOTH from one <c>Of(page, Tint)</c>
    /// value. One colour, praised at 113 DIP and reported absent at 26.</para>
    ///
    /// <para><b>WHY §27'S L* FLOOR CANNOT BE REUSED HERE, AND THIS WAS
    /// MEASURED RATHER THAN ASSUMED.</b> The obvious reading is that the mix has
    /// collapsed onto the page, as <see cref="PanelSeparation"/> guards against.
    /// It has not. On a black page the seat is <c>#2D2D2D</c>, which is
    /// <b>18.47 L*</b> off the page - the LARGEST separation of any ground the
    /// dial ships against - and <b>1.525:1</b>, which is the HIGHEST
    /// seat-versus-page ratio of any of them. Darkprint is worse on both counts
    /// (8.84 L*, 1.330:1) and was NOT reported. An L* floor only bites BELOW its
    /// value, so no value of one can reach the black page without first moving
    /// all nine papers. The quantity that fails near black is the RATIO: L* is
    /// spacious exactly where luminance is compressed.</para>
    ///
    /// <para><b>2.0, and it is derived rather than tasted.</b> The ratio is
    /// <c>(Ys + 0.05) / (Yp + 0.05)</c> and that 0.05 is WCAG's flare term - the
    /// light the room bounces off the glass. On a black page <c>Yp</c> is 0, so
    /// a ratio of 2.0 is exactly <c>Ys = 0.05</c>: the seat is as bright as the
    /// modelled flare. UNDER 2.0 there, the seat is dimmer than the reflection
    /// on the screen, which is as literal a reading of "the background is
    /// transparent" as this file can offer. It is a FLOOR, so it is set at the
    /// minimum that is defensible and not at a comfortable value; §29 of
    /// docs/CONCEPTS-REF-2026-08-07.md carries the measured table for 2.25, 2.5
    /// and 3.0 should a screen run say 2.0 is not enough.</para>
    ///
    /// <para><b>§34 CHANGES WHAT THIS IS MEASURED AGAINST, AND NOT THE NUMBER.</b>
    /// §29 measured the seat against the PAGE. The seat is not on the page.
    /// Where §7 takes the ring's fill away it stands on
    /// <see cref="SeatBackdrop"/> - the page seen through the dial's own drop
    /// shadow - and on Plain White that is <c>#D1D1D1</c> on <c>#CFCFCF</c> =
    /// <b>1.020:1</b>, worse than the black page that was actually reported. So
    /// the floor is unchanged at 2.0 and §29's <c>BaseIsDark</c> GATE IS GONE:
    /// the gate existed to protect the light branch, and the light branch turns
    /// out to be the branch that fails. What the gate was protecting survives on
    /// its own measurements instead - see <see cref="Seat(Color)"/>.</para>
    ///
    /// <para><b>2.0 was DERIVED at a black backdrop and is TRANSPLANTED at a
    /// light one</b>, and that is stated rather than hidden. The flare argument
    /// above needs <c>Yp = 0</c>. Against <c>#CFCFCF</c> it says nothing, and
    /// the number is carried over because §29 set it and because changing the
    /// surface and the constant in one run would leave neither measured. §34
    /// carries the table for 1.5, 1.75, 2.25 and 2.5 on the light stocks.</para></summary>
    public const double SeatFloor = 2.0;

    /// <summary>§34: THE DIAL'S DROP SHADOW, AS AN ALPHA - and a tool seat
    /// stands on IT, not on the paper.
    ///
    /// <para><b>The mechanism, read off <c>ToolWheel</c> rather than
    /// supposed.</b> <c>BuildWheel</c> adds one <c>_shadow</c> ellipse FIRST,
    /// under every other part, filled by <c>ShadowBrush</c> with a radial
    /// gradient that is SOLID black at this alpha from the centre out to
    /// <c>0.98 x RingOut/(RingOut + 14)</c> of its radius. That is 96.04 DIP,
    /// against a seat band of 59.14..85.14 DIP and a shadow centre offset 2 DIP
    /// down - so every seat lies wholly inside the SOLID part and sees a flat
    /// 46/255 = 18.04% black wash, not a gradient.</para>
    ///
    /// <para><b>Why the ellipse is solid inside at all, when a drop shadow is
    /// only ever seen OUTSIDE the thing that casts it.</b> It is a stand-in for
    /// a composition blur, which §1.1 rules out because the dial sits over a
    /// Win2D swap chain. Under an OPAQUE object the interior of that stand-in is
    /// covered and painting it solid costs nothing. §7 then took the ring's fill
    /// away on a dark shell, and the interior became visible. Photographed
    /// rather than argued: <c>scratchpad/vp10/r02-dial.png</c> shows the darker
    /// disc over the page where the ring has gone, and
    /// <c>scratchpad/vp12/a02-dial-crop.png</c> shows the same wash through the
    /// UNAVAILABLE cells on a light shell, where the sector's own opacity is
    /// 0.</para>
    ///
    /// <para>Read by <c>ToolWheel.ShadowBrush</c>, so the app and
    /// <c>tools/SeatProof</c> paint and measure one number.</para></summary>
    public const byte ShadowAlpha = 46;

    /// <summary>§34: the RING's own fill - <c>ToolWheel</c>'s OTHER backdrop for
    /// a seat, and the reason this file has to know that there are two.
    ///
    /// <para>§7 leaves the ring an opaque fill on a light shell, and a seat
    /// drawn over an opaque sector stands on THAT rather than on the shadow.
    /// It lives here so <c>tools/SeatProof</c> can measure both surfaces off the
    /// shipping constant instead of a copy of it - the failure §30.6 caught in
    /// <c>tools/PanelProof</c>, which hardcoded a value it also linked.</para>
    ///
    /// <para><b>Both of this mix's operands are SHELL-derived, and that is not
    /// this file's doing</b> - the expression is <c>ToolWheel</c>'s, moved and
    /// not changed. It is the same shell-keying items 2.1 and 2.2 removed
    /// elsewhere, it is why a light shell puts a near-white ring over a
    /// Darkprint page, and §34 flags it rather than fixing it: the ring is a
    /// large visible surface and re-keying it is a ruling, not a
    /// tuning.</para></summary>
    public const double RingTint = 0.62;

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

    /// <summary>§34: WHAT A TOOL SEAT ACTUALLY STANDS ON where §7 has taken the
    /// ring's fill away - the page seen through the dial's own drop shadow.
    ///
    /// <para>Not a paper, and that is the honest cost of §34: this file's whole
    /// thesis is that a chrome ground is derived from the PAGE, and a shadow is
    /// not a page. It is here because the alternative was worse. §29 judged the
    /// seat against the paper and published ten figures for a surface the seat
    /// does not touch; the only way to stop doing that is for the formula to
    /// know what is under the seat.</para>
    ///
    /// <para><b>It is page-derived and shell-INDEPENDENT, deliberately.</b> The
    /// ring's fill (<see cref="RingTint"/>) is the seat's other possible
    /// backdrop and it IS shell-keyed, so flooring against whichever one is live
    /// would give one paper two seat colours - a light one under a dark shell
    /// and a dark one under a light shell, flipping on the theme toggle. It is
    /// not needed: measured, this surface is the WORSE of the two on all nine
    /// shipped papers and on OLED black, before and after the floor, so
    /// clearing it clears the ring case as well. That is not universal - over a
    /// 3-step sweep of the whole gamut a pinned-light ring is the worse surface
    /// on some saturated custom pages - and §34 states the number rather than
    /// implying there is none.</para></summary>
    public static Color SeatBackdrop(Color ground) =>
        PageTheme.Over(Color.FromArgb(ShadowAlpha, 0, 0, 0), ground);

    /// <summary>§34: THE RING'S FILL, as <c>ToolWheel.Refresh</c> computes it -
    /// the seat's backdrop wherever §7 has NOT taken the ring away.
    ///
    /// <para>Moved here unchanged so the harness can link it. Both operands are
    /// the SHELL's tokens; see <see cref="RingTint"/>.</para></summary>
    public static Color RingFill(Color surface, Color shellGround) =>
        Mix(surface, shellGround, RingTint);

    /// <summary>§29/§34: A TOOL SEAT'S GROUND - the formula at
    /// <see cref="Tint"/>, held at least <see cref="SeatFloor"/> in contrast
    /// from <see cref="SeatBackdrop"/>.
    ///
    /// <para><b>§29 held it away from the PAGE and gated the floor to the dark
    /// base's branch. Both are corrected here, and the second follows from the
    /// first.</b> The gate existed to protect the light branch, on two stated
    /// reasons: that it carries the user's own <c>#7EA0B9</c>, and that a light
    /// page's seat "is not standing on the page at all". The second reason was
    /// right and was never followed through - if the seat is not on the page
    /// then neither is the FLOOR's measurement, and §29 published nine
    /// seat-versus-page figures anyway. Measured against what the seat is
    /// really on, the light branch is the branch that fails: <b>1.020:1</b> on
    /// Plain White against <c>#CFCFCF</c>, where the black page that was
    /// actually reported measures 1.525:1.</para>
    ///
    /// <para><b>The first reason survives WITHOUT the gate, and that is a
    /// measurement rather than a hope.</b> Blueprint's seat <c>#7E9FBA</c>
    /// stands on <c>#26699F</c> at <b>2.097:1</b> and Brown Paper's
    /// <c>#B09985</c> on <c>#8B5D34</c> at <b>2.085:1</b>. Both clear
    /// <see cref="SeatFloor"/> on their own, so both come back BYTE-IDENTICAL
    /// with the gate gone and the user's <c>#7EA0B9</c> is untouched. So does
    /// OLED black's <c>#3F403F</c>.</para>
    ///
    /// <para><b>What moves, and it is the six white stocks.</b> Plain White
    /// <c>#D1D1D1</c> to <c>#919292</c> (-23.37 L*), Transparent to
    /// <c>#8A8B8B</c>, Crumpled to <c>#888784</c>, Lightweight to
    /// <c>#8C8B8A</c>, Heavyweight to <c>#84827F</c>, Rippled to
    /// <c>#8A8A87</c>. They measured 1.020..1.134:1 against their real backdrop
    /// and they needed to. Darkprint moves LESS than §29 moved it -
    /// <c>#56585C</c> to <c>#4F5255</c> - because the shadow darkens a dark page
    /// too and the floor therefore has less work to do. THE COST IS THAT §24's
    /// "one colour for the disc and every cell alike" is now gone on EVERY page
    /// rather than only the near-black ones: the disc stays <c>#D1D1D1</c> on
    /// Plain White while its seats become <c>#919292</c>.</para>
    ///
    /// <para><b>The INNER DISC is byte-identical on every ground without
    /// exception</b>, which is the one guarantee the user's ruling demands.
    /// <c>ToolWheel</c> fills it straight from <see cref="Of"/> and this method
    /// is not on its path; measured over a 3-step lattice of all 636 056
    /// grounds, <b>0 differences</b>.</para>
    ///
    /// <para><b>§0's rule, for the fifth time in this file's history.</b> The
    /// seat's ground moved again, so the mark on it is re-judged against the new
    /// seat - <c>ToolWheel.BestInk</c> is fed this method's result. Over the
    /// same 636 056 grounds the re-judged mark bottoms out at <b>4.583:1</b>,
    /// <c>BestInk</c>'s provable minimum over the whole sRGB gamut; over the
    /// nine shipped papers plus OLED black the worst is <b>5.48:1</b> (Heavyweight),
    /// down from 12.44:1 and still clear of <see cref="MarkFloor"/>.</para></summary>
    public static Color Seat(Color ground) => Seat(ground, Tint, SeatFloor);

    /// <inheritdoc cref="Seat(Color)"/>
    /// <remarks>The parameterised form exists for the same reason
    /// <see cref="Panel(Color, double, double)"/>'s does: an acceptance harness
    /// has to sweep the SHIPPED arithmetic rather than a transcription of it,
    /// which is §24.7's method and the reason the figures above can be quoted as
    /// measurements. <paramref name="floor"/> = 0 returns the unfloored mix,
    /// which is how <c>tools/SeatProof</c> gets its "before" column.</remarks>
    public static Color Seat(Color ground, double t, double floor)
    {
        var seat = Of(ground, t);
        // §34: the floor is measured against what the seat STANDS ON, not
        // against the paper. There is no branch gate: see this method's remarks
        // for why the light branch's exclusion did not survive being measured,
        // and for what still comes back byte-identical without it.
        var back = SeatBackdrop(ground);
        if (Contrast(seat, back) >= floor) return seat;

        double lb = PageTheme.Lightness(back);
        double gap = PageTheme.Lightness(seat) - lb;
        // The lean is the base grey's own choice; §27's Panel reverses it for the
        // same reason, and a dead tie leans away from the backdrop's end of the
        // axis. On a white page the lean is UP by 0.70 L* and the up side cannot
        // reach the floor at all - pure white on #CFCFCF is 1.558:1 - so the
        // second Lift is what actually carries that case. It is not a fallback
        // there; it is the answer.
        double dir = gap > 0 ? 1 : gap < 0 ? -1 : (lb >= BaseSplit ? -1 : 1);
        return Lift(seat, back, lb, dir, floor)
            ?? Lift(seat, back, lb, -dir, floor)
            ?? seat;
    }

    /// <summary>Push a seat along L* until it clears <paramref name="floor"/>
    /// against <paramref name="against"/> - the surface it stands on - keeping
    /// a/b, so a seat is pushed OFF its backdrop rather than repainted grey,
    /// exactly as §27's clamp does.
    ///
    /// <para>A search rather than a closed form, and deliberately: contrast is a
    /// function of LUMINANCE while the thing being held is L*, and
    /// <see cref="PageTheme.WithLightness"/> clips a/b at the gamut edge, so the
    /// closed form would be solving the wrong variable and then be wrong again
    /// at the edge. The invariant is what carries the guarantee: <c>hi</c> is
    /// only ever moved to a value that has been MEASURED to clear the floor, so
    /// the returned colour always does. Returns null when the far endpoint
    /// cannot reach it, which is the caller's cue to try the other side.</para></summary>
    private static Color? Lift(Color seat, Color against, double lStart, double dir, double floor)
    {
        double lo = lStart, hi = dir > 0 ? 100 : 0;
        if (Contrast(PageTheme.WithLightness(seat, hi), against) < floor) return null;
        // 32 halvings take a 0..100 axis far below the byte quantisation
        // WithLightness rounds to anyway; this runs once per Refresh, not per cell.
        for (int i = 0; i < 32; i++)
        {
            double mid = (lo + hi) / 2;
            if (Contrast(PageTheme.WithLightness(seat, mid), against) >= floor) hi = mid;
            else lo = mid;
        }
        return PageTheme.WithLightness(seat, hi);
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
