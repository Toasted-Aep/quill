using Quill.Controls;
using Quill.Services;
using Windows.UI;

// =====================================================================
// §39.8's numbers, measured against the SHIPPED arithmetic.
//
// The question is NOT "what colour is a corner circle" - 16.2 answered
// that: a faint raised disc with a rule around it, drawn straight onto the
// canvas over the subject. It is "WHICH GROUND ARE ITS TWO HALVES KEYED
// TO", which SelectionChrome answered wrongly for both of them: it painted
// the fill PageTheme.Surface and the rule PageTheme.OnSurface, and those
// two are keyed to the SHELL's ground while the handle stands on the PAGE.
//
// §39.6 fixed the same defect one element group over - the guides - and
// explicitly flagged these four ellipses as the unaudited remainder. This
// is that audit.
// =====================================================================

static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

bool failed = false;
void Fail(string why) { failed = true; Console.WriteLine($"      **FAIL** {why}"); }

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Quill.sln"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("Quill.sln not found above " + AppContext.BaseDirectory);
}
var root = RepoRoot();
string chromeSrc = File.ReadAllText(Path.Combine(root, "src", "Quill", "Controls", "SelectionChrome.cs"));
string mainSrc = File.ReadAllText(Path.Combine(root, "src", "Quill", "MainWindow.xaml.cs"));

Console.WriteLine("== 0. THE UNLINKABLE TWO, ASSERTED AGAINST THE SOURCE TEXT ==");
void Assert(string label, string haystack, string needle)
{
    bool ok = haystack.Contains(needle, StringComparison.Ordinal);
    Console.WriteLine($"  [{(ok ? "ok" : "MOVED")}] {label}");
    if (!ok) Fail($"{label} - the source no longer contains: {needle.Trim()}");
}

// (a) The two brushes this whole file is about. SelectionChrome.cs is a WinUI
//     file and cannot be linked into a console app, so the pair and its alpha
//     are asserted here instead. If someone re-keys either half back to a
//     shell token, or moves the alpha, this run fails and the tables below
//     stop being a description of what ships.
Assert("SelectionChrome fills the corner circles with PageTheme.PageSurface", chromeSrc,
       "e.Fill = new SolidColorBrush(PageTheme.PageSurface);");
Assert("SelectionChrome rules them with PageTheme.OnPage at alpha 150", chromeSrc,
       "e.Stroke = new SolidColorBrush(PageTheme.WithAlpha(PageTheme.OnPage, 150));");
Assert("the handles are still hollow ellipses with a centred stroke", chromeSrc,
       "StrokeThickness = Metrics.HandleStroke,");
// (b) The three shell grounds MainWindow.ResolveGround can pin. That method is
//     the ENTIRE universe of a mismatch: under ThemeSource = "Manual" the shell
//     is one of these three no matter what paper is loaded.
Assert("MainWindow.ResolveGround still returns #0F0E10 / #000000 for dark", mainSrc,
       "? (_library.OledBlack ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 0x0F, 0x0E, 0x10))");
Assert("MainWindow.ResolveGround still returns #F7F6F1 for light", mainSrc,
       ": Color.FromArgb(255, 0xF7, 0xF6, 0xF1);");
Assert("ThemeSource still defaults to Manual", File.ReadAllText(Path.Combine(root, "src", "Quill", "Models", "NoteModels.cs")),
       "ThemeSource { get; set; } = \"Manual\"");
Console.WriteLine();

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

var shells = new (string Name, Color G)[]
{
    ("Manual light #F7F6F1", Color.FromArgb(255, 0xF7, 0xF6, 0xF1)),
    ("Manual dark  #0F0E10", Color.FromArgb(255, 0x0F, 0x0E, 0x10)),
    ("Manual OLED  #000000", Color.FromArgb(255, 0x00, 0x00, 0x00)),
};

const byte HandleAlpha = 150;

// THE THREE SURFACES A HOLLOW DISC IS JUDGED ON, and the reason there are
// three rather than one. The stroke is centred on the ellipse path, so the
// rule straddles the boundary: its inner half composites over the fill, its
// outer half over whatever the handle landed on. A token that satisfies only
// one of the two is not a fix, which is exactly what the stroke-only
// alternative in section 4 fails on.
static (double DiscVsPage, double RuleVsPage, double RuleVsDisc) Trio(Color fill, Color ink, Color page) =>
(
    PageTheme.Contrast(fill, page),
    PageTheme.Contrast(PageTheme.Over(PageTheme.WithAlpha(ink, HandleAlpha), page), page),
    PageTheme.Contrast(PageTheme.Over(PageTheme.WithAlpha(ink, HandleAlpha), fill), fill)
);

Console.WriteLine("== 1. WHAT SHIPPED: Surface / OnSurface, both keyed to the SHELL ==");
Console.WriteLine("   Those two fields still exist and are still shell-keyed - nothing about");
Console.WriteLine("   them changed, they simply stopped being what a corner circle reads. So");
Console.WriteLine("   this table is the defect measured on the LIVE tokens, not on a memory");
Console.WriteLine("   of them: set the two grounds apart and read Surface / OnSurface.");
Console.WriteLine();
Console.WriteLine($"  {"page",-13} {"pinned shell",-22} {"disc/page",9} {"rule/page",9} {"rule/disc",9}");
double wDisc = 99, wRule = 99;
string wDiscAt = "", wRuleAt = "";
foreach (var (pn, pk) in papers)
{
    var page = GroundOf(pk);
    foreach (var (sn, sg) in shells)
    {
        PageTheme.SetGrounds(sg, page);
        var (a, b, c) = Trio(PageTheme.Surface, PageTheme.OnSurface, page);
        if (a < wDisc) { wDisc = a; wDiscAt = $"{pn} under {sn}"; }
        if (b < wRule) { wRule = b; wRuleAt = $"{pn} under {sn}"; }
        string flag = a < 1.10 ? "  <- disc gone" : b < 1.10 ? "  <- rule gone" : "";
        Console.WriteLine($"  {pn,-13} {sn,-22} {a,9:F3} {b,9:F3} {c,9:F3}{flag}");
    }
}
Console.WriteLine();
Console.WriteLine($"  WORST disc/page {wDisc:F3}:1  ({wDiscAt})");
Console.WriteLine($"  WORST rule/page {wRule:F3}:1  ({wRuleAt})");
if (wRule > 1.10) Fail("the old pair no longer collapses - section 1 has stopped describing the defect");
Console.WriteLine();

Console.WriteLine("== 2. WHAT SHIPS NOW: PageSurface / OnPage, both keyed to the PAGE ==");
Console.WriteLine($"  {"page",-13} {"disc/page",9} {"rule/page",9} {"rule/disc",9}   fill / ink");
double nDisc = 99, nRule = 99, nRing = 99;
foreach (var (pn, pk) in papers)
{
    var page = GroundOf(pk);
    // Every pinned shell must give the SAME answer, which is the property the
    // fix is: the handle's colours no longer depend on the shell at all.
    (double, double, double)? seen = null;
    foreach (var (_, sg) in shells)
    {
        PageTheme.SetGrounds(sg, page);
        var t = Trio(PageTheme.PageSurface, PageTheme.OnPage, page);
        if (seen is { } s && s != t) Fail($"{pn}: the handle still moves with the shell");
        seen = t;
    }
    var (a, b, c) = seen!.Value;
    nDisc = Math.Min(nDisc, a); nRule = Math.Min(nRule, b); nRing = Math.Min(nRing, c);
    Console.WriteLine($"  {pn,-13} {a,9:F3} {b,9:F3} {c,9:F3}   {Hex(PageTheme.PageSurface)} / {Hex(PageTheme.OnPage)}");
}
Console.WriteLine();
Console.WriteLine($"  ranges: disc/page {nDisc:F3}+   rule/page {nRule:F3}+   rule/disc {nRing:F3}+");
if (nDisc < 1.20 || nRule < 1.50 || nRing < 1.50)
    Fail("a page-keyed handle has fallen below the range §39.8 records");
Console.WriteLine();

Console.WriteLine("== 3. THE RE-KEY IS A NO-OP UNDER ThemeSource = \"Page\" ==");
Console.WriteLine("   PageSurface is Surface's raise on PageGround and OnPage is OnSurface's");
Console.WriteLine("   rule on PageGround, so when the two grounds are equal the new pair must");
Console.WriteLine("   be BYTE-IDENTICAL to the old one. Nothing in Page mode changes colour.");
foreach (var (pn, pk) in papers)
{
    var page = GroundOf(pk);
    PageTheme.SetGrounds(page, page);
    bool fillSame = PageTheme.PageSurface == PageTheme.Surface;
    bool inkSame = PageTheme.OnPage == PageTheme.OnSurface;
    Console.WriteLine($"  [{(fillSame && inkSame ? "ok" : "DIFFERS")}] {pn,-13} " +
                      $"fill {Hex(PageTheme.Surface)} -> {Hex(PageTheme.PageSurface)}   " +
                      $"ink {Hex(PageTheme.OnSurface)} -> {Hex(PageTheme.OnPage)}");
    if (!fillSame || !inkSame) Fail($"{pn}: the re-key is not a no-op in Page mode");
}
Console.WriteLine();

Console.WriteLine("== 4. WHY BOTH HALVES MOVED: the stroke-only alternative, measured ==");
Console.WriteLine("   Re-keying only the ink - the smallest change that fixes rule-vs-page -");
Console.WriteLine("   does not close the defect, it relocates it. A shell-keyed fill under a");
Console.WriteLine("   page-keyed ink is the same split pair with the grounds swapped.");
Console.WriteLine();
Console.WriteLine($"  {"page",-13} {"pinned shell",-22} {"rule/disc",9}");
double sDisc = 99;
string sDiscAt = "";
foreach (var (pn, pk) in papers)
{
    var page = GroundOf(pk);
    foreach (var (sn, sg) in shells)
    {
        PageTheme.SetGrounds(sg, page);
        var (_, _, c) = Trio(PageTheme.Surface, PageTheme.OnPage, page);
        if (c < sDisc) { sDisc = c; sDiscAt = $"{pn} under {sn}"; }
        if (c < 1.35) Console.WriteLine($"  {pn,-13} {sn,-22} {c,9:F3}  <- rule invisible on its own disc");
    }
}
Console.WriteLine();
Console.WriteLine($"  WORST rule/disc under the stroke-only alternative: {sDisc:F3}:1  ({sDiscAt})");
Console.WriteLine($"  ...against {nRing:F3}:1 for the co-keyed pair that shipped.");
if (sDisc > 1.35) Fail("the stroke-only alternative no longer fails - section 4's argument has gone stale");
Console.WriteLine();

Console.WriteLine(failed ? "RESULT: FAIL" : "RESULT: PASS");
return failed ? 1 : 0;
