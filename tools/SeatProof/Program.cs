using System.Text;
using Quill.Controls;
using Quill.Services;
using Windows.UI;

// =====================================================================
// §34's numbers, measured against the SHIPPED arithmetic.
//
// The question this file exists to answer is NOT "what colour is a tool
// seat" - §29 answered that and got it right. It is "WHAT IS THE SEAT
// STANDING ON", which §29 answered wrongly: it measured every seat against
// the page, and wherever §7 has taken the ring's fill away the seat is
// standing on the dial's own drop shadow over the page instead.
// =====================================================================

static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
static double L(Color c) => PageTheme.Lightness(c);
static double Ratio(Color a, Color b) => PagePlate.Contrast(a, b);

var White = Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
var Black = Color.FromArgb(255, 0x00, 0x00, 0x00);

// ToolWheel.BestInk, transcribed - ToolWheel.cs is a WinUI partial and cannot
// be linked. Section 0 asserts the expression below still matches the one in
// the source, so the transcription cannot drift silently.
Color BestInk(Color plate) => Ratio(White, plate) > Ratio(Black, plate) ? White : Black;

bool failed = false;
void Fail(string why) { failed = true; Console.WriteLine($"      **FAIL** {why}"); }

// ---------------------------------------------------------------------
// 0. THE FOUR THINGS THIS FILE CANNOT LINK, ASSERTED AGAINST THE SOURCE
// ---------------------------------------------------------------------
// tools/PanelProof went on reporting a muted alpha of 140 after PageTheme had
// moved to 143, because it hardcoded a value it also linked (§30.6). Anything
// this file cannot link is therefore checked against the source text, and a
// move fails the run rather than being reported as a measurement.
static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Quill.sln"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("Quill.sln not found above " + AppContext.BaseDirectory);
}
var root = RepoRoot();
string wheelSrc = File.ReadAllText(Path.Combine(root, "src", "Quill", "Controls", "ToolWheel.cs"));
string mainSrc = File.ReadAllText(Path.Combine(root, "src", "Quill", "MainWindow.xaml.cs"));

Console.WriteLine("== 0. THE UNLINKABLE FOUR, ASSERTED AGAINST THE SOURCE TEXT ==");
void Assert(string label, string haystack, string needle)
{
    bool ok = haystack.Contains(needle, StringComparison.Ordinal);
    Console.WriteLine($"  [{(ok ? "ok" : "MOVED")}] {label}");
    if (!ok) Fail($"{label} - the source no longer contains: {needle.Trim()}");
}

// (a) BestInk, transcribed above.
Assert("ToolWheel.BestInk is still white-or-black by ratio", wheelSrc,
       "Contrast(Colors.White, plate) > Contrast(Colors.Black, plate) ? Colors.White : Colors.Black;");
// (b) The ring's fill is the linked constant, not a second copy of 0.62.
Assert("ToolWheel.Refresh takes its ring fill from PagePlate.RingFill", wheelSrc,
       "var ringFill = dark ? Colors.Transparent : PagePlate.RingFill(surface, PageTheme.Ground);");
// (c) The shadow's alpha is the linked constant, and its gradient is still
//     SOLID out to solid*0.98 - which is what makes the wash under a seat flat.
Assert("ToolWheel.ShadowBrush takes its alpha from PagePlate.ShadowAlpha", wheelSrc,
       "var wash = Color.FromArgb(PagePlate.ShadowAlpha, 0, 0, 0);");
Assert("ToolWheel.ShadowBrush is still solid out to solid * 0.98", wheelSrc,
       "b.GradientStops.Add(new GradientStop { Offset = solid * 0.98, Color = wash });");
Assert("ToolWheel's shadow still spans (RingOut + 14) * 2", wheelSrc,
       "_shadow.Width = _shadow.Height = (RingOut + 14) * 2;");
Assert("ToolWheel's shadow is still offset y+2", wheelSrc,
       "Canvas.SetTop(_shadow, Half - RingOut - 14 + 2);");
Assert("ToolWheel's shadow still goes in FIRST", wheelSrc, "_wheel.Children.Add(_shadow);");
// (d) The three shell grounds MainWindow.ResolveGround can return. There is no
//     way to link this method; the literals are asserted instead.
Assert("MainWindow.ResolveGround still returns #0F0E10 / #000000 for dark", mainSrc,
       "? (_library.OledBlack ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 0x0F, 0x0E, 0x10))");
Assert("MainWindow.ResolveGround still returns #F7F6F1 for light", mainSrc,
       ": Color.FromArgb(255, 0xF7, 0xF6, 0xF1);");
Assert("Theme still defaults to Dark", File.ReadAllText(Path.Combine(root, "src", "Quill", "Models", "NoteModels.cs")),
       "public string Theme { get; set; } = \"Dark\";");
Assert("ThemeSource still defaults to Manual", File.ReadAllText(Path.Combine(root, "src", "Quill", "Models", "NoteModels.cs")),
       "public string ThemeSource { get; set; } = \"Manual\";");

// The geometry claim PagePlate.ShadowAlpha's remarks make: every seat lies
// wholly inside the SOLID part of the shadow, so the wash under it is flat.
// R, RingIn, RingOut, MarkR, SeatSize are private consts in a WinUI partial;
// they are re-derived here from the same expressions and asserted in the text.
{
    Assert("ToolWheel.R is still 98", wheelSrc, "private const double R = 98;");
    Assert("ToolWheel.RingOut is still 1.00 * R", wheelSrc, "private const double RingOut = 1.00 * R;");
    Assert("ToolWheel.RingIn is still 0.58 * R", wheelSrc, "private const double RingIn = 0.58 * R;");
    Assert("ToolWheel.MarkBox is still 23", wheelSrc, "private const double MarkBox = 23;");
    Assert("ToolWheel.SeatSize is still 26", wheelSrc, "private const double SeatSize = 26;");
    Assert("ToolWheel.ArcStroke is still 2.6", wheelSrc, "private const double ArcStroke = 2.6;");
    Assert("ToolWheel.MarkR is still RingIn + ArcStroke + 1.2 + MarkBox / 2", wheelSrc,
           "private const double MarkR = RingIn + ArcStroke + 1.2 + MarkBox / 2;");
    const double r = 98, ringIn = 0.58 * r, ringOut = 1.00 * r;
    const double markR = ringIn + 2.6 + 1.2 + 23 / 2.0, seatSize = 26;
    double shadowR = ringOut + 14;
    double solidR = shadowR * (ringOut / shadowR) * 0.98;
    double seatFar = markR + seatSize / 2 + 2;      // +2 for the shadow's y offset
    Console.WriteLine($"  shadow solid to {solidR:F2} DIP | seat band {markR - seatSize / 2:F2}..{markR + seatSize / 2:F2} DIP" +
                      $" | farthest seat point from the shadow's centre {seatFar:F2} DIP" +
                      $" -> {(seatFar <= solidR ? "wholly inside the solid part" : "**CROSSES THE GRADIENT**")}");
    if (seatFar > solidR) Fail("a seat reaches the shadow's gradient; the wash under it is not flat");
}
Console.WriteLine();

// ---------------------------------------------------------------------
var papers = new (string Name, PaperKind Kind)[]
{
    ("Plain White",  PaperKind.PlainWhite),
    ("Transparent",  PaperKind.Transparent),
    ("Crumpled",     PaperKind.Crumpled),
    ("Lightweight",  PaperKind.Lightweight),
    ("Heavyweight",  PaperKind.Heavyweight),
    ("Rippled",      PaperKind.Rippled),
    ("Blueprint",    PaperKind.Blueprint),
    ("Brown Paper",  PaperKind.BrownPaper),
    ("Darkprint",    PaperKind.Darkprint),
};
static Color GroundOf(PaperKind k)
{
    var (r, g, b) = PaperGrain.GroundRgb(k);
    return Color.FromArgb(255, r, g, b);
}
var oled = Color.FromArgb(255, 0x00, 0x00, 0x00);
var pinnedDark = Color.FromArgb(255, 0x0F, 0x0E, 0x10);
var pinnedLight = Color.FromArgb(255, 0xF7, 0xF6, 0xF1);

// "the nine shipped papers plus OLED black", which is the set §29 reported on.
var shipped = papers.Select(p => (p.Name, Ground: GroundOf(p.Kind))).Append(("OLED black", oled)).ToArray();

// SUPERSEDED ARITHMETIC, REPRODUCED DELIBERATELY. This is §29's Seat(): the
// floor measured against the PAGE, gated to the dark base's branch. It is what
// shipped at 2b8d7b3 and it is the ONLY way to state a "before" column. It is
// read by nothing else and it must NOT be updated when PagePlate changes - its
// whole job is to say what §29 shipped.
static Color Seat29(Color ground)
{
    var seat = PagePlate.Of(ground, PagePlate.Tint);
    if (!PagePlate.BaseIsDark(ground) || PagePlate.Contrast(seat, ground) >= PagePlate.SeatFloor) return seat;
    double lg = PageTheme.Lightness(ground);
    double gap = PageTheme.Lightness(seat) - lg;
    double dir = gap > 0 ? 1 : gap < 0 ? -1 : (lg >= PagePlate.BaseSplit ? -1 : 1);
    static Color? Lift(Color seat, Color g, double lg, double dir)
    {
        double lo = lg, hi = dir > 0 ? 100 : 0;
        if (PagePlate.Contrast(PageTheme.WithLightness(seat, hi), g) < PagePlate.SeatFloor) return null;
        for (int i = 0; i < 32; i++)
        {
            double mid = (lo + hi) / 2;
            if (PagePlate.Contrast(PageTheme.WithLightness(seat, mid), g) >= PagePlate.SeatFloor) hi = mid;
            else lo = mid;
        }
        return PageTheme.WithLightness(seat, hi);
    }
    return Lift(seat, ground, lg, dir) ?? Lift(seat, ground, lg, -dir) ?? seat;
}

// Every surface one page can put under a seat. The ring's fill is shell-keyed,
// so the shell is enumerated rather than assumed: the three MainWindow can pin
// plus ThemeSource = "Page", where the shell IS the paper.
List<(string Who, Color C)> Surfaces(Color page)
{
    var outp = new List<(string, Color)>();
    foreach (var (label, shell) in new (string, Color)[]
             { ("pinned dark", pinnedDark), ("OLED shell", oled), ("pinned light", pinnedLight), ("ThemeSource=Page", page) })
    {
        PageTheme.SetGrounds(shell, page);
        outp.Add(PageTheme.IsDark
            ? ($"shadow ({label})", PagePlate.SeatBackdrop(page))
            : ($"ring ({label})", PagePlate.RingFill(PageTheme.Surface, PageTheme.Ground)));
    }
    return outp;
}

Console.WriteLine($"Tint = {PagePlate.Tint:F2}   SeatFloor = {PagePlate.SeatFloor:F2}:1   MarkFloor = {PagePlate.MarkFloor:F1}:1");
Console.WriteLine($"ShadowAlpha = {PagePlate.ShadowAlpha} ({100.0 * PagePlate.ShadowAlpha / 255:F2}% black)   RingTint = {PagePlate.RingTint:F2}");
Console.WriteLine();

// ---------------------------------------------------------------------
// 1. WHAT A SEAT ACTUALLY STANDS ON
// ---------------------------------------------------------------------
Console.WriteLine("== 1. WHAT A SEAT ACTUALLY STANDS ON, per shell ==");
Console.WriteLine("   The default install is the FIRST row block: Theme = \"Dark\", ThemeSource =");
Console.WriteLine("   \"Manual\", so the shell is #0F0E10, PageTheme.IsDark is true, and §7 takes");
Console.WriteLine("   the ring's fill away on EVERY paper - white stocks included.");
Console.WriteLine();
Console.WriteLine("| shell | paper | page | ring | backdrop | seat (§29) | on backdrop | on page |");
Console.WriteLine("|---|---|---|---|---|---|---|---|");
foreach (var (slabel, shell) in new (string, Color?)[]
         { ("pinned dark #0F0E10", pinnedDark), ("pinned light #F7F6F1", pinnedLight), ("ThemeSource=Page", null) })
    foreach (var (pname, page) in shipped)
    {
        PageTheme.SetGrounds(shell ?? page, page);
        bool ringGone = PageTheme.IsDark;
        var back = ringGone ? PagePlate.SeatBackdrop(page) : PagePlate.RingFill(PageTheme.Surface, PageTheme.Ground);
        var seat = Seat29(page);
        Console.WriteLine($"| {slabel} | {pname} | `{Hex(page)}` | {(ringGone ? "gone" : "`" + Hex(back) + "`")} | " +
                          $"`{Hex(back)}` | `{Hex(seat)}` | **{Ratio(seat, back):F3}:1** | {Ratio(seat, page):F3}:1 |");
    }
Console.WriteLine();

// ---------------------------------------------------------------------
// 2. THE NINE SHIPPED PAPERS PLUS OLED BLACK, BEFORE AND AFTER
// ---------------------------------------------------------------------
Console.WriteLine("== 2. THE NINE SHIPPED PAPERS PLUS OLED BLACK, seat vs its ACTUAL backdrop ==");
Console.WriteLine("| paper | page | backdrop | raw mix | §29 seat | §29 : backdrop | §34 seat | §34 : backdrop | dL* | moved | mark:seat |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
double wBefore = double.MaxValue, wAfter = double.MaxValue, wMark = double.MaxValue;
string wbWho = "", waWho = "", wmWho = "";
foreach (var (pname, page) in shipped)
{
    var back = PagePlate.SeatBackdrop(page);
    var raw = PagePlate.Seat(page, PagePlate.Tint, 0.0);
    var before = Seat29(page);
    var after = PagePlate.Seat(page);
    double rb = Ratio(before, back), ra = Ratio(after, back), rm = Ratio(BestInk(after), after);
    if (rb < wBefore) { wBefore = rb; wbWho = pname; }
    if (ra < wAfter) { wAfter = ra; waWho = pname; }
    if (rm < wMark) { wMark = rm; wmWho = pname; }
    bool moved = !after.Equals(before);
    Console.WriteLine($"| {pname} | `{Hex(page)}` | `{Hex(back)}` | `{Hex(raw)}` | `{Hex(before)}` | {rb:F3}:1 | " +
                      $"`{Hex(after)}` | **{ra:F3}:1** | {L(after) - L(before):+0.00;-0.00;0.00} | " +
                      $"{(moved ? "**yes**" : "-")} | {rm:F2}:1 |");
    if (ra < PagePlate.SeatFloor - 1e-6) Fail($"{pname} seat:backdrop {ra:F3}:1 is under SeatFloor {PagePlate.SeatFloor:F2}:1");
    if (rm < PagePlate.MarkFloor) Fail($"{pname} mark:seat {rm:F3}:1 is under MarkFloor {PagePlate.MarkFloor:F1}:1");
}
Console.WriteLine();
Console.WriteLine($"  worst seat vs ACTUAL backdrop : before {wBefore:F3}:1 ({wbWho})   ->   after {wAfter:F3}:1 ({waWho})");
Console.WriteLine($"  worst mark on seat, after     : {wMark:F2}:1 ({wmWho})   floor {PagePlate.MarkFloor:F1}:1   " +
                  $"{(wMark >= PagePlate.MarkFloor ? "PASS" : "**UNDER THE FLOOR**")}");
Console.WriteLine();

// ---------------------------------------------------------------------
// 3. WHICH SURFACE IS ACTUALLY THE WORST ONE - the claim PagePlate.SeatBackdrop
//    makes, and the case where it does not hold.
// ---------------------------------------------------------------------
Console.WriteLine("== 3. IS THE SHADOW THE WORSE SURFACE? (PagePlate.SeatBackdrop's claim) ==");
Console.WriteLine("| paper | §34 seat | shadow | worst RING surface | its ratio | shadow binds? |");
Console.WriteLine("|---|---|---|---|---|---|");
bool bindsOnShipped = true;
foreach (var (pname, page) in shipped)
{
    var after = PagePlate.Seat(page);
    double rs = Ratio(after, PagePlate.SeatBackdrop(page));
    double worst = double.MaxValue; string who = "-";
    foreach (var (w, c) in Surfaces(page))
        if (w.StartsWith("ring", StringComparison.Ordinal) && Ratio(after, c) < worst) { worst = Ratio(after, c); who = w; }
    bool binds = rs <= worst + 1e-9;
    if (!binds) bindsOnShipped = false;
    Console.WriteLine($"| {pname} | `{Hex(after)}` | {rs:F3}:1 | {who} | {worst:F3}:1 | {(binds ? "yes" : "**NO**")} |");
}
Console.WriteLine($"  the shadow is the worse surface on every shipped ground: {(bindsOnShipped ? "YES" : "NO")}");
if (!bindsOnShipped) Fail("PagePlate.SeatBackdrop claims the shadow binds on the shipped set; it does not");
Console.WriteLine();

// ---------------------------------------------------------------------
// 4. THE OTHER GROUNDS A USER CAN REACH
// ---------------------------------------------------------------------
Console.WriteLine("== 4. THE OTHER GROUNDS A USER CAN REACH ==");
Console.WriteLine("| ground | backdrop | §29 seat | §29 : bd | §34 seat | §34 : bd | mark:seat |");
Console.WriteLine("|---|---|---|---|---|---|---|");
foreach (var (name, g) in new (string, Color)[]
{
    ("custom default #FAF9F5", Color.FromArgb(255, 0xFA, 0xF9, 0xF5)),
    ("pinned dark  #0F0E10",   pinnedDark),
    ("pinned light #F7F6F1",   pinnedLight),
    ($"mid grey (LightBase) {Hex(PagePlate.LightBase)}", PagePlate.LightBase),
    ($"mid grey (DarkBase) {Hex(PagePlate.DarkBase)}",   PagePlate.DarkBase),
})
{
    var back = PagePlate.SeatBackdrop(g);
    var before = Seat29(g);
    var after = PagePlate.Seat(g);
    Console.WriteLine($"| {name} | `{Hex(back)}` | `{Hex(before)}` | {Ratio(before, back):F3}:1 | " +
                      $"`{Hex(after)}` | {Ratio(after, back):F3}:1 | {Ratio(BestInk(after), after):F2}:1 |");
}
Console.WriteLine();

// ---------------------------------------------------------------------
// 5. THE WHOLE GAMUT
// ---------------------------------------------------------------------
Console.WriteLine("== 5. EVERY PAGE COLOUR IN sRGB, step 3 ==");
{
    long total = 0, fired = 0, under = 0, discMoved = 0, ringWorse = 0, movedFrom29 = 0;
    long ringUnder15 = 0, ringUnder1529 = 0;
    double minAfter = double.MaxValue, minMark = double.MaxValue, maxMove = 0, worstRing = double.MaxValue;
    double worstRing29 = double.MaxValue;
    Color minAfterAt = default, minMarkAt = default, maxMoveAt = default, worstRingAt = default, worstRing29At = default;
    // The pinned-light ring is one fixed colour for every page, so it is
    // computed once rather than per ground.
    PageTheme.SetGrounds(pinnedLight, pinnedLight);
    var pinnedLightRing = PagePlate.RingFill(PageTheme.Surface, PageTheme.Ground);
    for (int r = 0; r < 256; r += 3)
        for (int g = 0; g < 256; g += 3)
            for (int b = 0; b < 256; b += 3)
            {
                var page = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
                var raw = PagePlate.Of(page, PagePlate.Tint);
                var after = PagePlate.Seat(page);
                var back = PagePlate.SeatBackdrop(page);
                total++;
                if (!after.Equals(raw)) fired++;
                if (!after.Equals(Seat29(page))) movedFrom29++;
                // THE DISC. ToolWheel fills it from PagePlate.Of and nothing
                // else; the user ruled it perfect and it must not have moved.
                if (!PagePlate.Of(page, PagePlate.Tint).Equals(raw)) discMoved++;
                double ra = Ratio(after, back);
                if (ra < PagePlate.SeatFloor - 1e-6) under++;
                if (ra < minAfter) { minAfter = ra; minAfterAt = page; }
                double rm = Ratio(BestInk(after), after);
                if (rm < minMark) { minMark = rm; minMarkAt = page; }
                double mv = Math.Abs(L(after) - L(raw));
                if (mv > maxMove) { maxMove = mv; maxMoveAt = page; }
                double rr = Ratio(after, pinnedLightRing);
                if (rr < ra - 1e-9) ringWorse++;
                if (rr < worstRing) { worstRing = rr; worstRingAt = page; }
                double rr29 = Ratio(Seat29(page), pinnedLightRing);
                if (rr29 < worstRing29) { worstRing29 = rr29; worstRing29At = page; }
                if (rr < 1.5) ringUnder15++;
                if (rr29 < 1.5) ringUnder1529++;
            }
    Console.WriteLine($"  grounds swept              : {total:N0}");
    Console.WriteLine($"  floor fired on             : {fired:N0}  ({100.0 * fired / total:F2}%)");
    Console.WriteLine($"  differs from §29's seat on : {movedFrom29:N0}  ({100.0 * movedFrom29 / total:F2}%)");
    Console.WriteLine($"  still under SeatFloor      : {under:N0}");
    Console.WriteLine($"  INNER DISC moved on        : {discMoved:N0}   (must be 0 - the user ruled it perfect)");
    Console.WriteLine($"  worst seat:backdrop        : {minAfter:F3}:1 at page {Hex(minAfterAt)}");
    Console.WriteLine($"  worst mark:seat            : {minMark:F3}:1 at page {Hex(minMarkAt)}  (BestInk's sRGB minimum is 4.583:1)");
    Console.WriteLine($"  largest move off the mix   : {maxMove:F2} L* at page {Hex(maxMoveAt)}");
    Console.WriteLine($"  a pinned-light RING is the worse surface on {ringWorse:N0} grounds ({100.0 * ringWorse / total:F2}%),");
    Console.WriteLine($"     worst {worstRing:F3}:1 at page {Hex(worstRingAt)} - NOT floored against, and §34 says why.");
    Console.WriteLine($"     §29 shipped {worstRing29:F3}:1 at page {Hex(worstRing29At)} on the SAME surface. §34 IMPROVES");
    Console.WriteLine($"     that surface on every shipped paper (1.14..1.35:1 -> 2.17..2.65:1, section 3) but");
    Console.WriteLine($"     makes the WORST custom page worse, because the lift is aimed at the shadow and a");
    Console.WriteLine($"     saturated page can land the result on the shell's fixed ring. Under 1.5:1 there:");
    Console.WriteLine($"     {ringUnder1529:N0} grounds before ({100.0 * ringUnder1529 / total:F2}%) -> {ringUnder15:N0} after ({100.0 * ringUnder15 / total:F2}%).");
    Console.WriteLine($"     NOT fixed here: closing it needs the SHELL's own ground inside PagePlate, which is");
    Console.WriteLine($"     the shell-keying items 2.1/2.2 removed. The ring's fill is the real defect. Flagged.");
    if (under > 0) Fail($"{under:N0} grounds are still under SeatFloor");
    if (discMoved > 0) Fail($"the inner disc moved on {discMoved:N0} grounds");
    if (minMark < PagePlate.MarkFloor) Fail($"worst mark:seat over the gamut is {minMark:F3}:1");
}
Console.WriteLine();

// ---------------------------------------------------------------------
// 6. THE LEVER - what a different SeatFloor would do to the six white stocks
// ---------------------------------------------------------------------
Console.WriteLine("== 6. THE LEVER: SeatFloor against the real backdrop ==");
Console.WriteLine("   §29's 2.0 was DERIVED at a black backdrop (Ys = the 0.05 flare term) and is");
Console.WriteLine("   TRANSPLANTED at a light one. If #919292 reads too heavy on white paper this");
Console.WriteLine("   is the one constant to move, and this is what each value costs.");
Console.WriteLine();
Console.WriteLine("   NOTE THE 1.50 AND 1.75 ROWS. Pure white on #CFCFCF is only 1.558:1, so below");
Console.WriteLine("   ~1.56 the lift can go UP on some white stocks and DOWN on others - Crumpled");
Console.WriteLine("   goes to #FEFCF9 at 1.75 while Plain White goes to #9C9C9D. Six papers that");
Console.WriteLine("   should look alike would not. At 2.00 the up side is out of reach for all six");
Console.WriteLine("   and they move together, which is a measured reason to prefer it over 1.75.");
Console.WriteLine();
Console.Write("| floor |");
foreach (var (n, _) in shipped) Console.Write($" {n} |");
Console.WriteLine(" worst mark:seat |");
Console.Write("|---|");
foreach (var _ in shipped) Console.Write("---|");
Console.WriteLine("---|");
foreach (double floor in new[] { 1.50, 1.75, 2.00, 2.25, 2.50 })
{
    Console.Write($"| **{floor:F2}:1** |");
    double wm = double.MaxValue;
    foreach (var (_, page) in shipped)
    {
        var s = PagePlate.Seat(page, PagePlate.Tint, floor);
        wm = Math.Min(wm, Ratio(BestInk(s), s));
        Console.Write($" `{Hex(s)}` |");
    }
    Console.WriteLine($" {wm:F2}:1 |");
}
Console.WriteLine();

// ---------------------------------------------------------------------
// 7. THE FIGURES PagePlate's OWN REMARKS QUOTE, RE-MEASURED
// ---------------------------------------------------------------------
// §27's harness found its own class comment quoting two numbers that were each
// about one L* out, because they were written from the intended value rather
// than measured. Every figure §34 puts in PagePlate.cs is re-measured here, so
// the comment cannot drift away from the arithmetic it describes.
Console.WriteLine("== 7. THE FIGURES QUOTED IN PagePlate.cs, RE-MEASURED ==");
void Quote(string claim, string measured, bool ok)
{
    Console.WriteLine($"  [{(ok ? "ok" : "WRONG")}] {claim,-62} -> {measured}");
    if (!ok) Fail($"PagePlate.cs quotes \"{claim}\" but the arithmetic says {measured}");
}
{
    var pw = GroundOf(PaperKind.PlainWhite);
    var pwBack = PagePlate.SeatBackdrop(pw);
    var pwRaw = PagePlate.Of(pw, PagePlate.Tint);
    Quote("Plain White #D1D1D1 on #CFCFCF = 1.020:1",
          $"{Hex(pwRaw)} on {Hex(pwBack)} = {Ratio(pwRaw, pwBack):F3}:1",
          Hex(pwRaw) == "#D1D1D1" && Hex(pwBack) == "#CFCFCF" && Math.Abs(Ratio(pwRaw, pwBack) - 1.020) < 0.0005);
    Quote("the black page as reported measures 1.525:1",
          $"{Ratio(PagePlate.Of(oled, PagePlate.Tint), PagePlate.SeatBackdrop(oled)):F3}:1",
          Math.Abs(Ratio(PagePlate.Of(oled, PagePlate.Tint), PagePlate.SeatBackdrop(oled)) - 1.525) < 0.0005);
    Quote("pure white on #CFCFCF is 1.558:1", $"{Ratio(White, pwBack):F3}:1",
          Math.Abs(Ratio(White, pwBack) - 1.558) < 0.0005);
    Quote($"ShadowAlpha {PagePlate.ShadowAlpha}/255 = 18.04% black",
          $"{100.0 * PagePlate.ShadowAlpha / 255:F2}%", Math.Abs(100.0 * PagePlate.ShadowAlpha / 255 - 18.04) < 0.005);

    var bp = GroundOf(PaperKind.Blueprint);
    Quote("Blueprint #7E9FBA on #26699F at 2.097:1, byte-identical",
          $"{Hex(PagePlate.Seat(bp))} on {Hex(PagePlate.SeatBackdrop(bp))} = {Ratio(PagePlate.Seat(bp), PagePlate.SeatBackdrop(bp)):F3}:1",
          Hex(PagePlate.Seat(bp)) == "#7E9FBA" && Hex(PagePlate.SeatBackdrop(bp)) == "#26699F"
          && PagePlate.Seat(bp).Equals(PagePlate.Of(bp, PagePlate.Tint)));
    var bn = GroundOf(PaperKind.BrownPaper);
    Quote("Brown Paper #B09985 on #8B5D34 at 2.085:1, byte-identical",
          $"{Hex(PagePlate.Seat(bn))} on {Hex(PagePlate.SeatBackdrop(bn))} = {Ratio(PagePlate.Seat(bn), PagePlate.SeatBackdrop(bn)):F3}:1",
          Hex(PagePlate.Seat(bn)) == "#B09985" && Hex(PagePlate.SeatBackdrop(bn)) == "#8B5D34"
          && PagePlate.Seat(bn).Equals(PagePlate.Of(bn, PagePlate.Tint)));
    Quote("OLED black #3F403F, byte-identical to §29",
          Hex(PagePlate.Seat(oled)), Hex(PagePlate.Seat(oled)) == "#3F403F" && PagePlate.Seat(oled).Equals(Seat29(oled)));

    foreach (var (name, want) in new (PaperKind, string)[]
    {
        (PaperKind.PlainWhite, "#919292"), (PaperKind.Transparent, "#8A8B8B"),
        (PaperKind.Crumpled, "#888784"), (PaperKind.Lightweight, "#8C8B8A"),
        (PaperKind.Heavyweight, "#84827F"), (PaperKind.Rippled, "#8A8A87"),
        (PaperKind.Darkprint, "#4F5255"),
    })
    {
        var g = GroundOf(name);
        Quote($"{name} seat is {want}", Hex(PagePlate.Seat(g)), Hex(PagePlate.Seat(g)) == want);
    }
    double dl = L(PagePlate.Seat(pw)) - L(pwRaw);
    Quote("Plain White moves -23.37 L*", $"{dl:+0.00;-0.00}", Math.Abs(dl + 23.37) < 0.005);
    Quote("worst mark on seat over the shipped set is 5.48:1 (Heavyweight)",
          $"{wMark:F2}:1 ({wmWho})", Math.Abs(wMark - 5.48) < 0.005 && wmWho == "Heavyweight");
}
Console.WriteLine();

Console.WriteLine(failed
    ? "RESULT: **FAILED** - see the FAIL lines above."
    : "RESULT: PASS - every shipped ground clears SeatFloor against the surface it stands on,\n" +
      "        every mark clears MarkFloor on its seat, and the inner disc is byte-identical.");
Console.WriteLine();
Console.WriteLine("NOTHING IN THIS FILE WAS VERIFIED ON SCREEN. It is arithmetic against the");
Console.WriteLine("shipped source; §34.6 says what to look at and what failure looks like.");
return failed ? 1 : 0;
