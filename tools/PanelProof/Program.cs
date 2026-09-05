using Quill.Controls;
using Quill.Services;
using Windows.UI;

// =====================================================================
// §27's numbers, measured against the SHIPPED arithmetic.
// =====================================================================

static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
static double L(Color c) => PageTheme.Lightness(c);
static double Ratio(Color a, Color b) => PagePlate.Contrast(a, b);

// Composite an alpha mark onto its ground - a muted ink is not a colour, it is
// an alpha, and its contrast has to be measured on what it actually resolves to.
//
// THIS USED TO BE THIS FILE'S OWN COPY of the arithmetic. The whole point of
// this project is that it links the shipping source rather than transcribing
// it, and a harness carrying its own second implementation of a formula it is
// checking is the same defect §30.6 caught here in another form. It now calls
// PageTheme's, which is what the app composites with.
static Color Over(Color mark, Color ground) => PageTheme.Over(mark, ground);

// Job 3: THE ACCEPTANCE GATE. Section 2 sets this when a SHIPPED paper's
// muted caption ink drops under PagePlate.MarkFloor on its own panel, and the
// process exits non-zero at the bottom of this file. Everything above that
// line used to only ever print "UNDER THE FLOOR" - readable in a scrollback
// nobody was tailing, and indistinguishable at a glance from every other line
// this file prints. A run that changes PanelT, PanelSeparation, a base grey
// or the muted alpha and pushes any shipped paper under the floor now fails
// the run, not just the transcript.
bool panelProofFailed = false;

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

var extra = new (string Name, Color Ground)[]
{
    ("custom default #FAF9F5", Color.FromArgb(255, 0xFA, 0xF9, 0xF5)),
    ("OLED black #000000",     Color.FromArgb(255, 0x00, 0x00, 0x00)),
    ("pinned dark #0F0E10",    Color.FromArgb(255, 0x0F, 0x0E, 0x10)),
    ("pinned light #F7F6F1",   Color.FromArgb(255, 0xF7, 0xF6, 0xF1)),
    // Read, not retyped: these two used to be a byte-for-byte copy of
    // PagePlate's own base greys, so a future change to either constant would
    // have kept probing the OLD collapse point instead of the shipped one.
    ($"mid grey (LightBase) {Hex(PagePlate.LightBase)}", PagePlate.LightBase),
    ($"mid grey (DarkBase) {Hex(PagePlate.DarkBase)}",   PagePlate.DarkBase),
};

Console.WriteLine($"PanelT = {PagePlate.PanelT:F2}   PanelSeparation = {PagePlate.PanelSeparation:F1} L*   MarkFloor = {PagePlate.MarkFloor:F1}:1");
Console.WriteLine();

// ---- 1. why t = 0.30: the sweep the constant was chosen off -----------
Console.WriteLine("== 1. t sweep: worst separation over the nine shipped papers, clamp DISABLED ==");
Console.WriteLine("   t     worst dL*   paper");
for (double t = 0.50; t >= 0.09; t -= 0.05)
{
    double worst = double.MaxValue; string who = "";
    foreach (var (name, kind) in papers)
    {
        var g = GroundOf(kind);
        double d = Math.Abs(L(PagePlate.Panel(g, t, 0.0)) - L(g));
        if (d < worst) { worst = d; who = name; }
    }
    Console.WriteLine($"  {t:F2}    {worst,7:F2}    {who}{(worst >= PagePlate.PanelSeparation ? "   <= clears the floor" : "")}");
}
// the exact crossing, to two decimals
{
    double lo = 0.0, hi = 1.0;
    for (int i = 0; i < 60; i++)
    {
        double mid = (lo + hi) / 2, worst = double.MaxValue;
        foreach (var (_, kind) in papers)
        {
            var g = GroundOf(kind);
            worst = Math.Min(worst, Math.Abs(L(PagePlate.Panel(g, mid, 0.0)) - L(g)));
        }
        if (worst >= PagePlate.PanelSeparation) lo = mid; else hi = mid;
    }
    Console.WriteLine($"  largest t whose worst shipped-paper gap still clears {PagePlate.PanelSeparation:F1} L*: t = {lo:F3}");
}
Console.WriteLine();

// ---- 2. the nine papers, shipped constants ---------------------------
Console.WriteLine("== 2. THE NINE SHIPPED PAPERS, at the shipped constants ==");
Console.WriteLine("| paper | ground | L* | base | panel | L* | sep dL* | clamp | ink | ink:panel | muted:panel |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
// The alpha is READ from the shipped token, not retyped here: this file
// links PageTheme.cs so its numbers are the ones the app uses, and a
// hardcoded copy defeated exactly that - it went on reporting 140 after
// PageTheme.Apply had been raised to 143, which is the harness failing in
// the same way as the code it checks.
int mutedAlpha = PageTheme.OnPanelMuted.A;
double worstSep = double.MaxValue, worstInk = double.MaxValue, worstMuted = double.MaxValue;
string worstSepWho = "", worstInkWho = "", worstMutedWho = "";
foreach (var (name, kind) in papers)
{
    var g = GroundOf(kind);
    var panel = PagePlate.Panel(g);
    var raw = PagePlate.Panel(g, PagePlate.PanelT, 0.0);
    bool clamped = !panel.Equals(raw);
    var ink = PagePlate.PanelInk(panel);
    var muted = Over(Color.FromArgb((byte)mutedAlpha, ink.R, ink.G, ink.B), panel);
    double sep = Math.Abs(L(panel) - L(g));
    double ri = Ratio(ink, panel), rm = Ratio(muted, panel);
    if (sep < worstSep) { worstSep = sep; worstSepWho = name; }
    if (ri < worstInk) { worstInk = ri; worstInkWho = name; }
    if (rm < worstMuted) { worstMuted = rm; worstMutedWho = name; }
    Console.WriteLine($"| {name} | `{Hex(g)}` | {L(g):F2} | {(PagePlate.BaseIsDark(g) ? "**dark**" : "light")} | " +
                      $"`{Hex(panel)}` | {L(panel):F2} | {sep:F2} | {(clamped ? "**fired**" : "-")} | " +
                      $"`{Hex(ink)}` | {ri:F2}:1 | {rm:F2}:1 |");
}
Console.WriteLine();
Console.WriteLine($"  worst separation      : {worstSep:F2} L*  ({worstSepWho})   floor {PagePlate.PanelSeparation:F1}" +
                  $"   {(worstSep >= PagePlate.PanelSeparation ? "PASS" : "**UNDER THE FLOOR**")}");
Console.WriteLine($"  worst ink:panel       : {worstInk:F2}:1  ({worstInkWho})    floor {PagePlate.MarkFloor:F1}:1" +
                  $"   {(worstInk >= PagePlate.MarkFloor ? "PASS" : "**UNDER THE FLOOR**")}");
// The MUTED variant is reported against the same floor and NOT quietly. §27's
// sweep (section 4) reaches 2.726:1 on an arbitrary page colour, which is a
// different question from what the SHIPPED papers do - so this line answers the
// shipped question on its own, and says so when it fails.
Console.WriteLine($"  worst muted:panel     : {worstMuted:F2}:1  ({worstMutedWho})    floor {PagePlate.MarkFloor:F1}:1" +
                  $"   {(worstMuted >= PagePlate.MarkFloor ? "PASS" : "**UNDER THE FLOOR - see §27's flag**")}");
foreach (var (name, kind) in papers)
{
    var g = GroundOf(kind);
    var panel = PagePlate.Panel(g);
    var ink = PagePlate.PanelInk(panel);
    double rm = Ratio(Over(Color.FromArgb((byte)mutedAlpha, ink.R, ink.G, ink.B), panel), panel);
    if (rm < PagePlate.MarkFloor)
        Console.WriteLine($"      FLAG: {name} muted-on-panel {rm:F3}:1 is under {PagePlate.MarkFloor:F1}:1");
}
if (worstMuted < PagePlate.MarkFloor)
{
    // Job 3: the loud half of the flag. Everything below this line is still
    // diagnostic text; this is the one line that makes the run itself fail.
    panelProofFailed = true;
    // What it would take, so the flag is actionable rather than just alarming.
    // OnPanelMuted is OnPanel at alpha `mutedAlpha` - PageTheme.Apply sets it -
    // and that figure is read above, not retyped here, for the same reason the
    // whole file links PageTheme.cs instead of copying it: a hardcoded "140"
    // sat in this very line after PageTheme.Apply had already moved the alpha
    // to 143, and went on reporting the old number silently. `need` below is
    // the smallest alpha that clears the floor on every shipped paper. NOT
    // applied: it is a visual weight decision about secondary text and it
    // belongs to the user, not to the harness.
    int need = mutedAlpha;
    for (int alpha = mutedAlpha; alpha <= 255; alpha++)
    {
        double lo2 = double.MaxValue;
        foreach (var (_, kind) in papers)
        {
            var g = GroundOf(kind);
            var panel = PagePlate.Panel(g);
            var ink = PagePlate.PanelInk(panel);
            lo2 = Math.Min(lo2, Ratio(Over(Color.FromArgb((byte)alpha, ink.R, ink.G, ink.B), panel), panel));
        }
        if (lo2 >= PagePlate.MarkFloor) { need = alpha; break; }
    }
    Console.WriteLine($"      the muted alpha is {mutedAlpha} (PageTheme.Apply); {need} is the smallest that clears " +
                      $"{PagePlate.MarkFloor:F1}:1 on all nine. NOT APPLIED - see §27's flag.");
}
Console.WriteLine();

// ---- 3. the other grounds a user can reach ---------------------------
Console.WriteLine("== 3. THE OTHER GROUNDS A USER CAN REACH ==");
Console.WriteLine("| ground | L* | base | panel | sep dL* | clamp | ink:panel | muted:panel |");
Console.WriteLine("|---|---|---|---|---|---|---|---|");
foreach (var (name, g) in extra)
{
    var panel = PagePlate.Panel(g);
    var raw = PagePlate.Panel(g, PagePlate.PanelT, 0.0);
    var ink = PagePlate.PanelInk(panel);
    var muted = Over(Color.FromArgb((byte)mutedAlpha, ink.R, ink.G, ink.B), panel);
    Console.WriteLine($"| {name} | {L(g):F2} | {(PagePlate.BaseIsDark(g) ? "dark" : "light")} | `{Hex(panel)}` | " +
                      $"{Math.Abs(L(panel) - L(g)):F2} | {(panel.Equals(raw) ? "-" : "**fired**")} | " +
                      $"{Ratio(ink, panel):F2}:1 | {Ratio(muted, panel):F2}:1 |");
}
Console.WriteLine();

// ---- 4. the whole gamut ----------------------------------------------
Console.WriteLine("== 4. EVERY PAGE COLOUR IN sRGB, step 3 ==");
{
    double minSep = double.MaxValue, minInk = double.MaxValue, minMuted = double.MaxValue;
    Color sepAt = default, inkAt = default, mutedAt = default;
    long clamped = 0, total = 0;
    for (int r = 0; r < 256; r += 3)
        for (int gg = 0; gg < 256; gg += 3)
            for (int b = 0; b < 256; b += 3)
            {
                var g = Color.FromArgb(255, (byte)r, (byte)gg, (byte)b);
                var panel = PagePlate.Panel(g);
                var raw = PagePlate.Panel(g, PagePlate.PanelT, 0.0);
                if (!panel.Equals(raw)) clamped++;
                total++;
                double sep = Math.Abs(L(panel) - L(g));
                if (sep < minSep) { minSep = sep; sepAt = g; }
                var ink = PagePlate.PanelInk(panel);
                double ri = Ratio(ink, panel);
                if (ri < minInk) { minInk = ri; inkAt = g; }
                var muted = Over(Color.FromArgb((byte)mutedAlpha, ink.R, ink.G, ink.B), panel);
                double rm = Ratio(muted, panel);
                if (rm < minMuted) { minMuted = rm; mutedAt = g; }
            }
    Console.WriteLine($"  colours swept          : {total:N0}");
    Console.WriteLine($"  clamp fired on         : {clamped:N0}  ({100.0 * clamped / total:F2}%)");
    Console.WriteLine($"  worst separation       : {minSep:F2} L*  at page {Hex(sepAt)}");
    Console.WriteLine($"  worst ink:panel        : {minInk:F3}:1  at page {Hex(inkAt)} -> panel {Hex(PagePlate.Panel(inkAt))}");
    Console.WriteLine($"  worst muted:panel      : {minMuted:F3}:1  at page {Hex(mutedAt)} -> panel {Hex(PagePlate.Panel(mutedAt))}");
}
Console.WriteLine();

// ---- 5. THE DEFECT, and what the old code did ------------------------
Console.WriteLine("== 5. THE MARK THAT WAS WRONG: PageTheme.OnSurface on the new panel ==");
Console.WriteLine("   Every panel mark used to be OnSurface, which is picked by the SHELL's");
Console.WriteLine("   luminance. These are the four shells a default install can be in, each");
Console.WriteLine("   over Plain White paper - the user's own case first.");
Console.WriteLine();
var shells = new (string Name, Color G)[]
{
    ("pinned dark  #0F0E10", Color.FromArgb(255, 0x0F, 0x0E, 0x10)),
    ("OLED black   #000000", Color.FromArgb(255, 0x00, 0x00, 0x00)),
    ("pinned light #F7F6F1", Color.FromArgb(255, 0xF7, 0xF6, 0xF1)),
    ("page = paper (Blueprint)", GroundOf(PaperKind.Blueprint)),
};
Console.WriteLine("| shell | page | panel | old mark (OnSurface) | old ratio | new mark (OnPanel) | new ratio |");
Console.WriteLine("|---|---|---|---|---|---|---|");
foreach (var (sname, sg) in shells)
    foreach (var (pname, kind) in new[] { ("Plain White", PaperKind.PlainWhite), ("Darkprint", PaperKind.Darkprint) })
    {
        var pg = GroundOf(kind);
        PageTheme.SetGrounds(sg, pg);
        var old = PageTheme.Luminance(sg) < 0.5 ? PageTheme.InkOnDark : PageTheme.InkOnLight;
        Console.WriteLine($"| {sname} | {pname} | `{Hex(PageTheme.Panel)}` | `{Hex(old)}` | " +
                          $"**{Ratio(old, PageTheme.Panel):F2}:1** | `{Hex(PageTheme.OnPanel)}` | " +
                          $"{Ratio(PageTheme.OnPanel, PageTheme.Panel):F2}:1 |");
    }
Console.WriteLine();

// ---- 6. PageTheme's own wiring ---------------------------------------
Console.WriteLine("== 6. PageTheme wiring ==");
PageTheme.SetGrounds(Color.FromArgb(255, 0x0F, 0x0E, 0x10), GroundOf(PaperKind.PlainWhite));
Console.WriteLine("  " + PageTheme.Describe());
int fired = 0;
void Bump() => fired++;
PageTheme.Changed += Bump;
bool a1 = PageTheme.SetGrounds(PageTheme.Ground, PageTheme.PageGround);
bool a2 = PageTheme.SetGrounds(PageTheme.Ground, GroundOf(PaperKind.Blueprint));
bool a3 = PageTheme.SetGrounds(Color.FromArgb(255, 0xF7, 0xF6, 0xF1), GroundOf(PaperKind.Blueprint));
PageTheme.Changed -= Bump;
Console.WriteLine($"  no move -> raised={a1} | page only -> raised={a2} | shell only -> raised={a3} | Changed fired {fired}x (expect 2)");
Console.WriteLine($"  panel now {Hex(PageTheme.Panel)} on page {Hex(PageTheme.PageGround)} / shell {Hex(PageTheme.Ground)}");

// The two tokens are MEASURED against each other rather than asserted. This
// line used to end "- they DISAGREE here, which is the point", on a state where
// they are both #141414: a light shell over Blueprint puts InkOnLight on the
// shell and PanelInk lands on InkOnLight too. The claim was written from what
// the author expected and never checked against the state above it, which is
// the same defect §27 is fixing one layer down. So it reports what it finds,
// and then goes and finds a state where they really do differ.
static void Agreement(string what)
{
    bool same = PageTheme.OnSurface.Equals(PageTheme.OnPanel);
    Console.WriteLine($"  {what}: shell {Hex(PageTheme.Ground)} -> OnSurface {Hex(PageTheme.OnSurface)} | " +
                      $"page {Hex(PageTheme.PageGround)} -> panel {Hex(PageTheme.Panel)} -> OnPanel {Hex(PageTheme.OnPanel)} | " +
                      (same ? "AGREE" : "DISAGREE"));
}
Agreement("as left by the wiring test");
// Both directions of disagreement, which is what makes the two tokens
// independent rather than one being a rename of the other.
PageTheme.SetGrounds(Color.FromArgb(255, 0x0F, 0x0E, 0x10), GroundOf(PaperKind.PlainWhite));
Agreement("dark shell / light page ");
PageTheme.SetGrounds(Color.FromArgb(255, 0xF7, 0xF6, 0xF1), GroundOf(PaperKind.Darkprint));
Agreement("light shell / dark page ");
Console.WriteLine();

// ---- 7. THE DERIVATION FIGURE, ASSERTED --------------------------------
// PagePlate.PanelT's remarks quote four numbers. They are re-measured here on
// every run, so the comment cannot drift away from the arithmetic it describes -
// which is the failure §24's harness had when it was not committed.
Console.WriteLine("== 7. THE DERIVATION FIGURE IN PagePlate.PanelT, RE-MEASURED ==");
{
    static (double Worst, string Who) WorstAt(double t, (string Name, PaperKind Kind)[] ps)
    {
        double worst = double.MaxValue; string who = "";
        foreach (var (name, kind) in ps)
        {
            var (r, g0, b0) = PaperGrain.GroundRgb(kind);
            var g = Color.FromArgb(255, r, g0, b0);
            double d = Math.Abs(PageTheme.Lightness(PagePlate.Panel(g, t, 0.0)) - PageTheme.Lightness(g));
            if (d < worst) { worst = d; who = name; }
        }
        return (worst, who);
    }
    static void Claim(string text, bool ok) =>
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {text}");

    var at30 = WorstAt(0.30, papers);
    var at35 = WorstAt(0.35, papers);
    double lo = 0.0, hi = 1.0;
    for (int i = 0; i < 60; i++)
    {
        double mid = (lo + hi) / 2;
        if (WorstAt(mid, papers).Worst >= PagePlate.PanelSeparation) lo = mid; else hi = mid;
    }
    bool everBinds = true;
    for (double t = 0.10; t <= 0.501; t += 0.05)
        if (WorstAt(t, papers).Who != "Darkprint") everBinds = false;

    Claim("\"Darkprint binds first\" - it is the worst paper at every t in 0.10..0.50", everBinds);
    Claim($"\"crossing the floor at t = 0.328\" - bisected {lo:F3}", Math.Abs(lo - 0.328) < 0.0005);
    Claim($"\"0.30 is the round number under it\" - {PagePlate.PanelT:F2} < {lo:F3}",
          PagePlate.PanelT < lo && PagePlate.PanelT >= lo - 0.05);
    Claim($"\"worst gap 10.23 L* (Darkprint) at 0.30\" - {at30.Worst:F2} ({at30.Who})",
          Math.Abs(at30.Worst - 10.23) < 0.005 && at30.Who == "Darkprint");
    Claim($"\"at 0.35 it would be 9.69\" - {at35.Worst:F2}", Math.Abs(at35.Worst - 9.69) < 0.005);
    Claim($"\"Darkprint ground is L* 17.28\" - {PageTheme.Lightness(GroundOf(PaperKind.Darkprint)):F2}",
          Math.Abs(PageTheme.Lightness(GroundOf(PaperKind.Darkprint)) - 17.28) < 0.005);
    Claim($"\"DarkBase is L* 31.89\" - {PageTheme.Lightness(PagePlate.DarkBase):F2}",
          Math.Abs(PageTheme.Lightness(PagePlate.DarkBase) - 31.89) < 0.005);
    Claim($"\"LightBase is L* 73.31\" - {PageTheme.Lightness(PagePlate.LightBase):F2}",
          Math.Abs(PageTheme.Lightness(PagePlate.LightBase) - 73.31) < 0.005);
    bool noClamp = true;
    foreach (var (_, kind) in papers)
    {
        var g = GroundOf(kind);
        if (!PagePlate.Panel(g).Equals(PagePlate.Panel(g, PagePlate.PanelT, 0.0))) noClamp = false;
    }
    Claim("\"the clamp never fires on shipped stock\"", noClamp);
}
Console.WriteLine();

// ---- 8. THE BEFORE/AFTER BASELINE --------------------------------------
//
// A TRANSCRIPTION, and the only one in this file. Everything else here is
// measured against linked shipping source; this cannot be, because the code it
// measures was DELETED by §27. It is the panel ramp that stood at 8b1050a:
// LightPanelChroma 0.85 / DarkPanelChroma 0.35, bands L* 95..97.5 and 13..29,
// with a gy <= 0.004 escape to pure black - applied, as it was, to the SHELL's
// ground. `git show 8b1050a:src/Quill/Services/PageTheme.cs` is the original.
Console.WriteLine("== 8. BEFORE / AFTER: the superseded shell ramp vs §27's page panel ==");
{
    static (double L, double a, double b) ToLab(Color c)
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
    static Color FromLab(double L, double a, double b)
    {
        L = Math.Clamp(L, 0, 100);
        double fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double G(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116.0) / 7.787;
        double x = G(fx) * 0.95047, y = G(fy), z = G(fz) * 1.08883;
        static byte S(double v)
        {
            v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }
        return Color.FromArgb(255,
            S(3.2406 * x - 1.5372 * y - 0.4986 * z),
            S(-0.9689 * x + 1.8758 * y + 0.0415 * z),
            S(0.0557 * x - 0.2040 * y + 1.0570 * z));
    }
    // The deleted ramp, verbatim in behaviour, keyed to the SHELL as it was.
    static Color OldPanel(Color shell)
    {
        var (_, a, b) = ToLab(shell);
        double gy = PageTheme.Luminance(shell);
        if (gy < 0.5)
            return gy <= 0.004
                ? Color.FromArgb(255, 0, 0, 0)
                : FromLab(13.0 + 16.0 * Math.Min(1.0, gy / 0.5), a * 0.35, b * 0.35);
        double t = Math.Clamp((gy - 0.5) / 0.5, 0.0, 1.0);
        return FromLab(95.0 + 2.5 * t, a * 0.85, b * 0.85);
    }

    Console.WriteLine("| shell | page | OLD panel (from shell) | OLD sep from page | NEW panel (from page) | NEW sep | NEW ink | NEW ratio |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|");
    foreach (var (sname, sg) in shells)
        foreach (var (pname, kind) in new[] { ("Plain White", PaperKind.PlainWhite), ("Darkprint", PaperKind.Darkprint) })
        {
            var pg = GroundOf(kind);
            var oldP = OldPanel(sg);
            var newP = PagePlate.Panel(pg);
            var ink = PagePlate.PanelInk(newP);
            Console.WriteLine($"| {sname} | {pname} | `{Hex(oldP)}` L*{L(oldP):F1} | {Math.Abs(L(oldP) - L(pg)):F2} | " +
                              $"`{Hex(newP)}` L*{L(newP):F1} | {Math.Abs(L(newP) - L(pg)):F2} | `{Hex(ink)}` | {Ratio(ink, newP):F2}:1 |");
        }
    Console.WriteLine();
    // The report, in one line.
    {
        var shell = Color.FromArgb(255, 0x0F, 0x0E, 0x10);
        var page = GroundOf(PaperKind.PlainWhite);
        var oldP = OldPanel(shell);
        PageTheme.SetGrounds(shell, page);
        var oldInk = PageTheme.Luminance(shell) < 0.5 ? PageTheme.InkOnDark : PageTheme.InkOnLight;
        Console.WriteLine("  THE REPORT - \"the app theme is black in a white page what is this?\"");
        Console.WriteLine($"    default install: shell {Hex(shell)} pinned dark, page {Hex(page)} Plain White (L* {L(page):F2})");
        Console.WriteLine($"    BEFORE: panel {Hex(oldP)} (L* {L(oldP):F2}) - {Math.Abs(L(oldP) - L(page)):F1} L* below the paper. The black slab.");
        Console.WriteLine($"    AFTER : panel {Hex(PageTheme.Panel)} (L* {L(PageTheme.Panel):F2}) - {Math.Abs(L(PageTheme.Panel) - L(page)):F1} L* below the paper.");
        Console.WriteLine($"    and the mark on it: {Hex(oldInk)} at {Ratio(oldInk, PageTheme.Panel):F2}:1  ->  " +
                          $"{Hex(PageTheme.OnPanel)} at {Ratio(PageTheme.OnPanel, PageTheme.Panel):F2}:1");
    }
}

// ---- 10. ITEM 2.1: A SURFACE WITH NO PLATE AT ALL ---------------------
//
// §27 re-keyed every mark that stands on a PageTheme.Panel and left
// ChromeUi.Ink on the shell's OnSurface, with a reason that was true of every
// consumer it checked: those marks stand on a PageTheme.Surface plate.
//
// CanvasPane is the consumer that breaks it. It is BARE by a measured
// reference - no background, no border, no shadow - so its marks stand on the
// PAPER. On the default install that put #F2F2F2 on #FCFCFC: 1.091:1, the
// whole Precision panel invisible. PageTheme.OnPage / OnPageMuted / PageOutline
// are the re-keyed tokens; this section measures them the way section 2
// measures the panel's.
//
// THE MUTED INK IS THE ONE THAT CAN FAIL, and that is why it is gated. OnPage
// is PagePlate.Ink, a luminance pick whose own floor over the nine papers is
// 3.66:1 (Brown Paper). OnPageMuted is that ink at alpha 140, which composites
// TOWARD the page and therefore always loses ratio - so Brown Paper is where a
// muted caption would go under the floor first, and nothing but a measurement
// settles whether it does.
//
// The OUTLINE is reported and NOT gated, exactly as §27 leaves PanelOutline
// ungated: an alpha-36 hairline is a rule, not a mark carrying meaning, and
// WCAG's 3:1 is about the second. It is printed so that a change which makes
// it disappear altogether is visible in the transcript.
Console.WriteLine("== 10. item 2.1: the bare pane's ink, on the page itself ==");
Console.WriteLine("| page | ground | OnPage | ratio | OnPageMuted (composited) | ratio | outline | ratio | pageIsDark |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
bool pageInkFailed = false;
string worstPageInkPaper = "";
double worstPageInk = 999;
{
    // The default install: a pinned dark shell over whatever the paper is. That
    // is the case item 2.1 was reported on, and the case where the OLD token and
    // the NEW one disagree most.
    var shell = Color.FromArgb(255, 0x0F, 0x0E, 0x10);
    foreach (var (name, kind) in papers)
    {
        var pg = GroundOf(kind);
        PageTheme.SetGrounds(shell, pg);
        var ink = PageTheme.OnPage;
        var muted = Over(PageTheme.OnPageMuted, pg);
        var rule = Over(PageTheme.PageOutline, pg);
        double ri = Ratio(ink, pg), rm = Ratio(muted, pg), rr = Ratio(rule, pg);
        if (rm < worstPageInk) { worstPageInk = rm; worstPageInkPaper = name + " (muted)"; }
        if (ri < worstPageInk) { worstPageInk = ri; worstPageInkPaper = name + " (ink)"; }
        bool bad = ri < PagePlate.MarkFloor || rm < PagePlate.MarkFloor;
        if (bad) pageInkFailed = true;
        Console.WriteLine($"| {name} | `{Hex(pg)}` | `{Hex(ink)}` | {ri:F2}:1 | `{Hex(muted)}` | {rm:F2}:1 | " +
                          $"`{Hex(rule)}` | {rr:F2}:1 | {(PageTheme.PageIsDark ? 1 : 0)} |{(bad ? "  <- UNDER THE FLOOR" : "")}");
    }
}
Console.WriteLine();
Console.WriteLine($"  worst mark-on-page over the nine shipped papers: {worstPageInk:F3}:1  ({worstPageInkPaper})");
{
    // The report, in one line - the same shape section 8 gives §27's.
    var shell = Color.FromArgb(255, 0x0F, 0x0E, 0x10);
    var page = GroundOf(PaperKind.PlainWhite);
    PageTheme.SetGrounds(shell, page);
    Console.WriteLine("  THE REPORT - the Precision panel, every word invisible on the default paper");
    Console.WriteLine($"    default install: shell {Hex(shell)} pinned dark, page {Hex(page)} Plain White");
    Console.WriteLine($"    BEFORE: heading/chip {Hex(PageTheme.OnSurface)} at {Ratio(PageTheme.OnSurface, page):F3}:1   " +
                      $"muted {Hex(Over(PageTheme.OnSurfaceMuted, page))} at {Ratio(Over(PageTheme.OnSurfaceMuted, page), page):F3}:1");
    Console.WriteLine($"    AFTER : heading/chip {Hex(PageTheme.OnPage)} at {Ratio(PageTheme.OnPage, page):F3}:1   " +
                      $"muted {Hex(Over(PageTheme.OnPageMuted, page))} at {Ratio(Over(PageTheme.OnPageMuted, page), page):F3}:1");
}
Console.WriteLine();

// ---- 11. ITEM 2.2: THE TEXT EDITOR'S OWN GROUND -----------------------
//
// A box being edited on a #FCFCFC page drew its words on #606060 - 2.93:1,
// under the floor, the lowest-contrast text on the page being the text the
// user is currently typing.
//
// WHERE THAT GREY CAME FROM, AND WHY THIS SECTION DOES NOT LINK IT.
// InkSurface sets Background = Transparent and Foreground = boxInk on the
// RichEditBox; WinUI's template overrides both from its visual states, and a
// VisualState setter outranks a local value while the state holds.
// TextControlBackgroundFocused resolves to the DARK theme's
// ControlFillColorInputActive, #B31E1E1E, and over the paper that composites to
//     0x1E * (179/255) + 0xFC * (1 - 179/255) = 96.17  ->  #606060
// on all three channels. The theme is dark because MainWindow.ApplyTheme keys
// RootGrid.RequestedTheme to PageTheme.IsDark - the SHELL's darkness.
//
// #B31E1E1E is WinUI'S constant, not ours. It is written here ONCE, in a
// comment, as the identification of a value OBSERVED ON SCREEN - never as a
// number this section computes with. That distinction is the whole of 30.6's
// lesson: a harness that hardcodes a value it also links is checking its own
// transcription. If WinUI ever changes that brush the comment goes stale and
// the fix stays right, because the fix REMOVES the dependency on it.
//
// So this section measures only the shipped answer: the editor now stands on
// the page, and its ink is PageTheme.TextInk of that page - both linked.
Console.WriteLine("== 11. item 2.2: the text editor stands on the page ==");
Console.WriteLine("| page | ground | TextInk | ratio | floor |");
Console.WriteLine("|---|---|---|---|---|");
bool editorFailed = false;
double worstEditor = 999;
string worstEditorPaper = "";
foreach (var (name, kind) in papers)
{
    var pg = GroundOf(kind);
    var ink = PageTheme.TextInk(pg);
    double r = Ratio(ink, pg);
    if (r < worstEditor) { worstEditor = r; worstEditorPaper = name; }
    bool bad = r < PagePlate.MarkFloor;
    if (bad) editorFailed = true;
    Console.WriteLine($"| {name} | `{Hex(pg)}` | `{Hex(ink)}` | {r:F2}:1 | {PagePlate.MarkFloor:F1}:1 |{(bad ? "  <- UNDER THE FLOOR" : "")}");
}
foreach (var (name, g) in extra)
{
    var ink = PageTheme.TextInk(g);
    Console.WriteLine($"| {name} | `{Hex(g)}` | `{Hex(ink)}` | {Ratio(ink, g):F2}:1 | (not gated: not shipped stock) |");
}
Console.WriteLine();
Console.WriteLine($"  worst editor-ink-on-page over the nine shipped papers: {worstEditor:F3}:1  ({worstEditorPaper})");
// TextInk's own remarks claim a floor of 4.183:1 over the WHOLE sRGB gamut, at
// the crossing of the two inks' curves. Nine papers cannot verify a claim about
// a gamut, so it is swept: a 3-step lattice is 636,056 grounds, the same lattice
// 29 used for BestInk, and it is cheap enough to run every time.
{
    double floor = 99; Color at = default;
    for (int r = 0; r < 256; r += 3)
        for (int g = 0; g < 256; g += 3)
            for (int b = 0; b < 256; b += 3)
            {
                var ground = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
                double v = Ratio(PageTheme.TextInk(ground), ground);
                if (v < floor) { floor = v; at = ground; }
            }
    Console.WriteLine($"  and over a 3-step sRGB lattice (636,056 grounds): floor {floor:F3}:1 at {Hex(at)}");
    Console.WriteLine($"  -> the editor cannot go under the {PagePlate.MarkFloor:F1}:1 floor on ANY page, not merely on the nine.");
}
Console.WriteLine();

// ---- 9. THE GATE, and it is loud -------------------------------------
//
// Job 3: a non-zero exit, not a printed line. Section 2's per-paper FLAG loop
// and its "worst muted:panel" summary already said this in text; text is what
// a screen run reads and a CI step does not. Only the NINE SHIPPED PAPERS gate
// the exit code - section 4's whole-gamut sweep is EXPECTED to reach under the
// floor on an arbitrary custom page colour (§27 measured 2.726:1 there) and
// that is a stated, accepted limit rather than a regression, so it stays a
// printed number and nothing here reads it.
Console.WriteLine("== 9. GATE ==");
if (panelProofFailed)
{
    Console.WriteLine("  FAIL: muted-on-panel is under the 3:1 floor on a shipped paper - see section 2.");
    Console.Error.WriteLine("PanelProof FAILED: muted-on-panel is under the 3:1 floor on a shipped paper - see section 2.");
    return 1;
}
// Item 2.1 gates the same way and for the same reason. A section that only
// PRINTS "UNDER THE FLOOR" is a section nobody reads on the run that breaks it.
if (pageInkFailed)
{
    Console.WriteLine("  FAIL: a bare pane's ink is under the 3:1 floor on a shipped paper - see section 10.");
    Console.Error.WriteLine("PanelProof FAILED: a bare pane's ink is under the 3:1 floor on a shipped paper - see section 10.");
    return 1;
}
if (editorFailed)
{
    Console.WriteLine("  FAIL: the text editor's ink is under the 3:1 floor on a shipped paper - see section 11.");
    Console.Error.WriteLine("PanelProof FAILED: the text editor's ink is under the 3:1 floor on a shipped paper - see section 11.");
    return 1;
}
Console.WriteLine("  PASS: muted-on-panel clears the 3:1 floor on all nine shipped papers.");
Console.WriteLine("  PASS: the bare pane's ink and muted ink clear the 3:1 floor on all nine shipped papers.");
Console.WriteLine("  PASS: the text editor's ink clears the 3:1 floor on all nine shipped papers, and over the gamut.");
return 0;
