// CONCEPTS-REF 56.8 - THE TRIM, AGAINST THE ENGINE QUILL SHIPS.
//
// tools/TextColourRoundTrip part 6 proves the DECISIONS (TextFlushPolicy) and
// models the live document. What it cannot say is what the RichEdit engine
// does when the trim deletes through it: which formatting the surviving
// paragraph keeps, what a table looks like in the story's plain text, whether
// a link shifts the positions. This tool asks the engine.
//
//   LINKED  - src/Quill/Services/TextTrim.cs (the trim InkSurface.FlushTexts
//             calls) and src/Quill/Services/TextFlushPolicy.cs, compiled as
//             the app compiles them.
//   ENGINE  - WinUIEdit.dll, the RichEdit build every Microsoft.UI.Xaml
//             RichEditBox runs on, reached through Microsoft.UI.Text - the
//             same RichEditTextDocument type box.Document returns.
//   NOT SEEN - everything XAML adds on top: the RichEditBox template, the
//             default formatting Quill gives a box, focus, caret, clipboard.
//
// No screen: the engine is hosted by a MESSAGE-ONLY window (parent
// HWND_MESSAGE), never shown, never focused, given no input.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Quill.Services;

var log = new List<string>();
int failures = 0;
void Check(string label, bool ok, string detail = "")
{
    log.Add($"{(ok ? "PASS" : "FAIL")} {label}{(detail.Length > 0 ? "   -- " + detail : "")}");
    if (!ok) failures++;
}
var info = new List<string>();
var ordinaryOutputs = new List<(string Name, string Rtf)>();

// ---- 8a. THE APP CALLS THIS FILE, AND HAS NO OTHER COPY --------------------
string root = FindRoot();
string inkSurface = File.ReadAllText(Path.Combine(root, @"src\Quill\Controls\InkSurface.cs"));
int calls = Regex.Matches(inkSurface, @"TextTrim\.TrimTrailingEmptyParagraphs\(ui\.Box\.Document\)").Count;
bool privateCopy = inkSurface.Contains("string? TrimTrailingEmptyParagraphs(", StringComparison.Ordinal);
Check("56.8 [8a] InkSurface.FlushTexts calls the linked TextTrim.TrimTrailingEmptyParagraphs(ui.Box.Document), "
      + "once, and keeps no private copy of the trim - so what runs below is what the app runs",
      calls == 1 && !privateCopy,
      $"{calls} call(s); private copy present: {privateCopy}");

// ---- 8b. THE ENGINE IS THE APP'S ENGINE ------------------------------------
string ownDll = Path.Combine(AppContext.BaseDirectory, "WinUIEdit.dll");
string appDll = Path.Combine(root, @"src\Quill\bin\x64\Debug\net8.0-windows10.0.19041.0\WinUIEdit.dll");
string ownSha = Sha(ownDll);
string appSha = File.Exists(appDll) ? Sha(appDll) : "(Quill not built)";
Check("56.8 [8b] the WinUIEdit.dll driven here is byte-identical to the one in Quill's own build output",
      File.Exists(appDll) && ownSha == appSha,
      $"here {ownSha[..16]}, Quill {(appSha.Length >= 16 ? appSha[..16] : appSha)}");
Engine.Init(ownDll);

// ---- 8c. SECTION 50'S PREMISE, ON THE ENGINE -------------------------------
string grown = StoredDocWith("lecture notes", 46, Plainfinal);
var g = Load(grown);
var (gPlain, gStart, gEnd) = Story(g);
string grownPlainExpected = "lecture notes" + new string('\r', 49);
var ladder = new List<int>();
string s = StoredDocWith("closing words", 0, Plainfinal);
ladder.Add(Pars(s));
for (int i = 0; i < 5; i++) { s = Rtf(Load(s)); ladder.Add(Pars(s)); }
bool plusOne = ladder.Zip(ladder.Skip(1), (a, b) => b - a).All(d => d == 1);
Check("56.8 [8c] THE MODEL'S PREMISE HOLDS ON THE ENGINE: a stored note with 46 empties reads as its words "
      + "then one '\\r' per \\par the writer emitted plus the engine's own final mark (49), the story span is "
      + "exactly the text's length, and every open-and-save adds exactly ONE paragraph (section 50's ladder)",
      gPlain == grownPlainExpected && gStart == 0 && gEnd == gPlain.Length && plusOne,
      $"plain = words + {gPlain.Length - 13} x \\r, span [{gStart},{gEnd}); \\par per session: {string.Join(" -> ", ladder)}");

// ---- 8d. THE GROWN NOTE, EDITED, TRIMMED -----------------------------------
Type(g, "lecture notes", "!");
string gBefore = Rtf(g);
string? gTrim = TextTrim.TrimTrailingEmptyParagraphs(g);
var (gAfter, _, _) = Story(g);
Check("56.8 [8d] A GROWN NOTE, EDITED: the shipping trim, on the engine, leaves the words and exactly ONE "
      + "empty paragraph, stores the engine's own serialisation of that, and everything up to the "
      + "content's own \\par is the untrimmed document's exact prefix",
      gTrim != null && gAfter == "lecture notes!\r\r" && Pars(gTrim) == 2 &&
      gTrim.StartsWith(gBefore[..(gBefore.IndexOf(@"lecture notes!\par", StringComparison.Ordinal) + 18)], StringComparison.Ordinal),
      $"{gBefore.Length} -> {gTrim?.Length} chars, \\par {Pars(gBefore)} -> {(gTrim is null ? -1 : Pars(gTrim))}, plain {Esc(gAfter)}");
if (gTrim != null) ordinaryOutputs.Add(("8d", gTrim));

// ---- 8e. FINDING 2, THE REALISTIC SHAPE ------------------------------------
// The user right-aligned and indented the blank line under their words. The
// ENGINE then grows the note over five sessions - it appends its final mark
// after that line each time - and the user edits it. The line they formatted
// is the FIRST trailing empty; the mark that must survive is the LAST.
string own = StoredDocWith("closing words", 0, @"\pard\qr\li720\sl300\slmult1");
string ownGrown = own;
for (int i = 0; i < 5; i++) ownGrown = Rtf(Load(ownGrown));
var o = Load(ownGrown);
var (oPlain, _, _) = Story(o);
// Read NOW, as values: a range's ParagraphFormat is a live view, and would
// report the survivor's formatting if it were read after the trim.
int oFirst = oPlain.IndexOf('\r') + 1;
var oFirstAlign = o.GetRange(oFirst, oFirst + 1).ParagraphFormat.Alignment;
float oIndent = o.GetRange(oFirst, oFirst + 1).ParagraphFormat.LeftIndent;
var oLastAlign = o.GetRange(oPlain.Length - 1, oPlain.Length).ParagraphFormat.Alignment;
float oLastIndent = o.GetRange(oPlain.Length - 1, oPlain.Length).ParagraphFormat.LeftIndent;
Type(o, "closing words", "!");
string? oTrim = TextTrim.TrimTrailingEmptyParagraphs(o);
var (oAfter, _, _) = Story(o);
var oSurvAlign = o.GetRange(oAfter.Length - 1, oAfter.Length).ParagraphFormat.Alignment;
float oSurvIndent = o.GetRange(oAfter.Length - 1, oAfter.Length).ParagraphFormat.LeftIndent;
Check("56.8 [8e] THE USER'S OWN BLANK LINE KEEPS ITS FORMATTING: a note whose right-aligned, indented "
      + "blank line was grown by the engine for 5 sessions is trimmed to one empty paragraph, and that "
      + "paragraph is right-aligned with the same indent - which the engine's final mark was NOT before "
      + "the trim, so this is the copy working and not the default",
      oTrim != null && oAfter == "closing words!\r\r" &&
      oLastAlign != ParagraphAlignment.Right && oLastIndent != oIndent &&
      oFirstAlign == ParagraphAlignment.Right && oIndent > 0 &&
      oSurvAlign == ParagraphAlignment.Right && oSurvIndent == oIndent,
      $"plain {Esc(oPlain)} -> {Esc(oAfter)}; before: first empty {oFirstAlign}/{oIndent}pt, "
      + $"engine's final mark {oLastAlign}/{oLastIndent}pt; after: survivor {oSurvAlign}/{oSurvIndent}pt");
if (oTrim != null) ordinaryOutputs.Add(("8e", oTrim));

// ---- 8f. FINDING 2, THE BRIEF'S SHAPE: A CENTRED, BOLD, 24pt BLANK LINE ----
string fmtBlank = StoredDocWith(@"text\par" + Nl + @"\pard\qc\sl300\slmult1\b\fs48\par" + Nl + @"\pard\sl300\slmult1\b0\fs24", 4, Plainfinal);
var f = Load(fmtBlank);
var (fPlain, _, _) = Story(f);
var fLast = f.GetRange(fPlain.Length - 1, fPlain.Length);
var (fAlign0, fBold0, fSize0) = (fLast.ParagraphFormat.Alignment, fLast.CharacterFormat.Bold, fLast.CharacterFormat.Size);
Type(f, "text", "s");
string? fTrim = TextTrim.TrimTrailingEmptyParagraphs(f);
var (fAfter, _, _) = Story(f);
var fSurv = f.GetRange(fAfter.Length - 1, fAfter.Length);
var (fAlign1, fBold1, fSize1) = (fSurv.ParagraphFormat.Alignment, fSurv.CharacterFormat.Bold, fSurv.CharacterFormat.Size);
Check("56.8 [8f] ...and the brief's shape: the first trailing blank line is centred, bold and 24pt, the "
      + "rest are plain; after the trim the one surviving empty paragraph is centred, bold and 24pt, "
      + "which the engine's final mark was not",
      fTrim != null && fAfter == "texts\r\r" &&
      fAlign0 != ParagraphAlignment.Center && fBold0 != FormatEffect.On && fSize0 != 24f &&
      fAlign1 == ParagraphAlignment.Center && fBold1 == FormatEffect.On && fSize1 == 24f,
      $"before: final mark {fAlign0}/bold {fBold0}/{fSize0}pt; after: survivor {fAlign1}/bold {fBold1}/{fSize1}pt; plain {Esc(fAfter)}");
if (fTrim != null) ordinaryOutputs.Add(("8f", fTrim));

// ---- 8g. FORMATTED RUNS AND A CENTRED LAST CONTENT LINE --------------------
const string RunsBody = @"\b bold\b0  \i italic\i0  \cf2 red words\cf1  \fs36 large\fs24  plain\par" + "\r\n"
                      + @"\pard\qc\sl300\slmult1\i centred closing line\i0";
var r = Load(StoredDocWith(RunsBody, 46, Plainfinal));
Type(r, "plain", "ly");
string rBefore = Rtf(r);
int rContentEnd = rBefore.IndexOf(@"\par", rBefore.IndexOf("centred closing line", StringComparison.Ordinal), StringComparison.Ordinal) + 4;
string? rTrim = TextTrim.TrimTrailingEmptyParagraphs(r);
var (rAfter, _, _) = Story(r);
Check("56.8 [8g] BOLD, ITALIC, COLOUR AND SIZE RUNS AND A CENTRED LAST CONTENT LINE: the engine's own "
      + "serialisation of everything up to the content's last \\par is unchanged by the trim, byte for byte",
      rTrim != null && rTrim.StartsWith(rBefore[..rContentEnd], StringComparison.Ordinal) &&
      rAfter.EndsWith("centred closing line\r\r", StringComparison.Ordinal) && Pars(rTrim) == 3,
      $"{rContentEnd}-char prefix kept: {rTrim?.StartsWith(rBefore[..rContentEnd], StringComparison.Ordinal)}; \\par {Pars(rBefore)} -> {(rTrim is null ? -1 : Pars(rTrim))}");
if (rTrim != null) ordinaryOutputs.Add(("8g", rTrim));

// ---- 8h. INTERIOR BLANK LINES, AND WHITESPACE-ONLY PARAGRAPHS IN THE RUN ---
string wsBody = @"first thought\par" + Nl + @"\par" + Nl + @"\par" + Nl + @"\par" + Nl + @"after the gap\par" + Nl
              + @" \par" + Nl + @"\par" + Nl + @"\~\par" + Nl + @"\par" + Nl + @"\tab";
var w = Load(StoredDocWith(wsBody, 5, Plainfinal));
var (wPlain, _, _) = Story(w);
Type(w, "after the gap", "!");
string? wTrim = TextTrim.TrimTrailingEmptyParagraphs(w);
var (wAfter, _, _) = Story(w);
const string WsExpected = "first thought\r\r\r\rafter the gap!\r \r\r\u00a0\r\r\t\r\r";
Check("56.8 [8h] THREE INTERIOR BLANK LINES, THEN PARAGRAPHS OF ONE SPACE, ONE NO-BREAK SPACE AND ONE TAB, "
      + "EMPTY ONES BETWEEN THEM, INSIDE WHAT LOOKS LIKE THE TRAILING RUN: all of them survive; only the "
      + "marks after the tab's paragraph go, down to one",
      wTrim != null && wAfter == WsExpected,
      $"{Esc(wPlain)} -> {Esc(wAfter)}");
if (wTrim != null) ordinaryOutputs.Add(("8h", wTrim));

// ---- 8i. A BOX OF NOTHING BUT EMPTY PARAGRAPHS -----------------------------
var e = Load(StoredDocWith("", 3, Plainfinal));
var (ePlain, _, _) = Story(e);
string? eTrim = TextTrim.TrimTrailingEmptyParagraphs(e);
var (eAfter, _, _) = Story(e);
Check("56.8 [8i] A BOX OF NOTHING BUT EMPTY PARAGRAPHS keeps exactly one mark, on the engine",
      eTrim != null && eAfter == "\r",
      $"{Esc(ePlain)} -> {Esc(eAfter)}");
if (eTrim != null) ordinaryOutputs.Add(("8i", eTrim));

// ---- 8j. NOTHING TO DROP ---------------------------------------------------
var n = Load(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Segoe UI;}}\pard done\par}");
string nBefore = Rtf(n);
var (nPlain, _, _) = Story(n);
string? nTrim = TextTrim.TrimTrailingEmptyParagraphs(n);
Check("56.8 [8j] A NOTE ALREADY AT ONE TRAILING EMPTY PARAGRAPH is refused and left exactly as it was",
      nTrim == null && Rtf(n) == nBefore && nPlain == "done\r\r",
      $"plain {Esc(nPlain)}, trim {(nTrim is null ? "null" : "wrote")}, document unchanged: {Rtf(n) == nBefore}");
ordinaryOutputs.Add(("8j", nBefore));

// ---- 8k. A LINK ------------------------------------------------------------
var k = Load(StoredDocWith(@"see {\field{\*\fldinst{HYPERLINK ""https://example.com""}}{\fldrslt{example}}} here", 4, Plainfinal));
var (kPlain, kStart, kEnd) = Story(k);
Type(k, " here", "!");
string? kTrim = TextTrim.TrimTrailingEmptyParagraphs(k);
var (kAfter, _, _) = Story(k);
Check("56.8 [8k] A BOX WITH A HYPERLINK FIELD IS TRIMMED, and the link survives: the field's instruction "
      + "text is counted in the story's plain text AND its span alike, so positions still map one to one "
      + "(section 56.7 had inferred the opposite)",
      kEnd - kStart == kPlain.Length && kTrim != null && kAfter.EndsWith(" here!\r\r", StringComparison.Ordinal) &&
      kTrim.Contains(@"HYPERLINK ""https://example.com""", StringComparison.Ordinal) &&
      kTrim.Contains(@"\fldrslt", StringComparison.Ordinal),
      $"span {kEnd - kStart} = plain {kPlain.Length}; after {Esc(kAfter)}");
if (kTrim != null) ordinaryOutputs.Add(("8k", kTrim));

// ---- 8l. TABLES: REFUSED, AND LEFT EXACTLY AS THEY WERE --------------------
const string Row1 = @"\trowd\trgaph108\trleft-108\cellx3000\cellx6000\pard\intbl cell_one\cell\cell\row";
const string Row2Empty = @"\trowd\trgaph108\cellx3000\cellx6000\pard\intbl\cell\cell\row";
var tables = new (string Name, string Rtf)[]
{
    ("a table, its second cell empty, then the final paragraph",
     StoredDocWith("", 0, Plainfinal).Replace(@"\cf1\f0\fs24 \par", @"\cf1\f0\fs24 " + Row1 + @"\pard\sl300\slmult1")),
    ("words, a table, then 4 empty paragraphs",
     StoredDocWith(@"before\par" + Nl + Row1 + @"\pard\sl300\slmult1", 4, Plainfinal)),
    ("two rows, the last all empty cells",
     StoredDocWith(Row1 + Nl + Row2Empty + @"\pard\sl300\slmult1", 0, Plainfinal)),
};
bool tablesOk = true, rangesClean = true;
var tDetail = new List<string>();
foreach (var (name, rtf) in tables)
{
    var t = Load(rtf);
    string tBefore = Rtf(t);
    var (tPlain, _, _) = Story(t);
    bool gate = TextFlushPolicy.ContainsTableStructure(tBefore);
    bool belt = TextFlushPolicy.PlainTextShowsTable(tPlain);
    string? tTrim = TextTrim.TrimTrailingEmptyParagraphs(t);
    var (tAfterPlain, _, _) = Story(t);
    bool ok = gate && belt && tTrim == null && Rtf(t) == tBefore && tAfterPlain == tPlain;
    tablesOk &= ok;
    // What the range function would take WITHOUT the gate.
    var (rs, rl) = TextFlushPolicy.EmptyParagraphMarksToDrop(tPlain);
    int rowEnd = tPlain.LastIndexOf('\uFFFB');
    bool clean = rl == 0 || (tPlain.Substring(rs, rl).All(c => c == '\r') && rs > rowEnd + 1);
    rangesClean &= clean;
    tDetail.Add($"{name}: plain {Esc(tPlain)} gate={gate} belt={belt} trim={(tTrim is null ? "refused" : "WROTE")} unchanged={Rtf(t) == tBefore}; ungated range ({rs},{rl})");
}
Check("56.8 [8l] A TABLE IS NEVER TRIMMED: for each of three table shapes the engine reads and writes back "
      + "as a table, BOTH refusals fire - the RTF gate on the engine's own serialisation and the plain-text "
      + "belt on its story - the trim refuses, and the document and its plain text are exactly as they were",
      tablesOk, string.Join(" | ", tDetail));
Check("56.8 [8m] WHAT THE GATE IS INSURANCE AGAINST, MEASURED: on this engine a table row is "
      + "U+FFF9 CR ... U+0007 ... U+FFFB CR - cell marks are U+0007, not CR - and for all three shapes the "
      + "range the policy would choose without the gate holds only paragraph marks after the row end's "
      + "own CR. The gate stays: any shape not measured here is refused rather than reasoned about",
      rangesClean, rangesClean ? "no ungated range touches a table character or the row end's mark" : "an ungated range reaches into the table");

// ---- 8n. THE GATE NEVER FIRES ON AN ORDINARY NOTE --------------------------
var falsePositives = ordinaryOutputs.Where(x => TextFlushPolicy.ContainsTableStructure(x.Rtf)).Select(x => x.Name).ToList();
// The belt over the story each of those documents reads as, on the engine.
var beltPositives = ordinaryOutputs.Where(x => TextFlushPolicy.PlainTextShowsTable(Story(Load(x.Rtf)).Plain))
                                   .Select(x => x.Name).ToList();
Check("56.8 [8n] BOTH TABLE REFUSALS ARE SILENT ON EVERY ORDINARY DOCUMENT THE ENGINE WROTE ABOVE - runs, "
      + "colours, sizes, alignment, indents, a link, tabs, a no-break space - the RTF gate on the engine's "
      + "serialisation and the belt on its story, so neither can quietly switch the trim off everywhere",
      ordinaryOutputs.Count >= 8 && falsePositives.Count == 0 && beltPositives.Count == 0,
      $"{ordinaryOutputs.Count} engine outputs, gate fired on: {(falsePositives.Count == 0 ? "none" : string.Join(",", falsePositives))}; "
      + $"belt fired on: {(beltPositives.Count == 0 ? "none" : string.Join(",", beltPositives))}");

// ---- NOT CHECKS: shapes reported, not asserted ------------------------------
foreach (var (name, rtf) in new[]
{
    ("nested table", StoredDocWith(@"\pard\intbl\itap1 outer\par" + Nl
        + @"\pard\intbl\itap2 inner\nestcell{\*\nesttableprops\trowd\cellx2000\nestrow}{\nonesttables\par}" + Nl
        + @"\pard\intbl\itap1\cell" + Nl + @"\trowd\cellx4000\row" + Nl + @"\pard\sl300\slmult1", 3, Plainfinal)),
    ("a lone \\intbl paragraph, no row", StoredDocWith(@"\pard\intbl lonely\par" + Nl + @"\pard\sl300\slmult1", 3, Plainfinal)),
})
{
    var t = Load(rtf);
    string tb = Rtf(t);
    var (tp, _, _) = Story(t);
    string? tt = TextTrim.TrimTrailingEmptyParagraphs(t);
    info.Add($"INFO {name}: plain {Esc(tp)}; engine writes table words: {TextFlushPolicy.ContainsTableStructure(tb)}; trim {(tt is null ? "refused" : "wrote")}");
}

// ===========================================================================
foreach (var line in log) Console.WriteLine(line);
Console.WriteLine();
foreach (var line in info) Console.WriteLine(line);
Console.WriteLine();
Console.WriteLine(failures == 0 ? $"OK - {log.Count} checks held against WinUIEdit.dll {ownSha[..16]}." : $"{failures} CHECK(S) FAILED");
Console.WriteLine("NOT MEASURED HERE: the RichEditBox template, Quill's default box formatting, focus, the caret, "
                  + "the clipboard and anything pasted through it. The engine was reached through a message-only window.");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------------------
static RichEditTextDocument Load(string rtf)
{
    var d = Engine.NewDoc();
    d.SetText(TextSetOptions.FormatRtf, rtf);
    return d;
}

static string Rtf(RichEditTextDocument d) { d.GetText(TextGetOptions.FormatRtf, out string r); return r; }

static (string Plain, int Start, int End) Story(RichEditTextDocument d)
{
    var st = d.GetRange(0, 0);
    st.Expand(TextRangeUnit.Story);
    st.GetText(TextGetOptions.None, out string p);
    return (p, st.StartPosition, st.EndPosition);
}

// A keystroke: text inserted through the document right after the first
// occurrence of `after` in the story - an edit, as far as the engine knows.
static void Type(RichEditTextDocument d, string after, string text)
{
    var (p, _, _) = Story(d);
    int at = p.IndexOf(after, StringComparison.Ordinal);
    if (at < 0) throw new InvalidOperationException($"no \"{after}\" in the story");
    d.GetRange(at + after.Length, at + after.Length).Text = text;
}

static int Pars(string rtf) => Regex.Matches(rtf, @"\\par(?![a-z])").Count;

static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, @"src\Quill\Quill.csproj"))) return dir.FullName;
    throw new DirectoryNotFoundException("no src\\Quill\\Quill.csproj above " + AppContext.BaseDirectory);
}

static string Esc(string s) => "\"" + string.Concat(s.Select(c => c switch
{
    '\r' => "\\r", '\n' => "\\n", '\v' => "\\v", '\t' => "\\t",
    _ when c < 32 || c > 126 => $"\\u{(int)c:X4}",
    _ => c.ToString(),
})) + "\"";

// The stored shape tools/TextColourRoundTrip part 6 uses (header, two-colour
// table, generator, body, empties, a closing paragraph, CRLF, NUL).
static string StoredDocWith(string body, int empties, string finalParagraph)
{
    var sb = new StringBuilder();
    sb.Append(@"{\rtf1\fbidis\ansi\ansicpg1252\deff0\nouicompat\deflang2057{\fonttbl{\f0\fnil Segoe UI;}}").Append(Nl);
    sb.Append(@"{\colortbl ;\red250\green249\blue245;\red200\green30\blue30;}").Append(Nl);
    sb.Append(@"{\*\generator Riched20 3.1.0008}\viewkind4\uc1 ").Append(Nl);
    sb.Append(@"\pard\sl300\slmult1\cf1\f0\fs24 ").Append(body).Append(@"\par").Append(Nl);
    for (int i = 0; i < empties; i++) sb.Append(@"\par").Append(Nl);
    sb.Append(Nl).Append(finalParagraph).Append(@"\par").Append(Nl);
    sb.Append('}').Append(Nl).Append('\0');
    return sb.ToString();
}

partial class Program
{
    const string Nl = "\r\n";
    const string Plainfinal = @"\pard\sl300\slmult1";
}

// The engine, hosted by a message-only window. RichEditWndProc is the window
// procedure WinUIEdit.dll exports for exactly this; the class is registered
// once, each document gets its own window.
static class Engine
{
    static IntPtr _inst;
    public static void Init(string dll)
    {
        IntPtr mod = LoadLibraryW(dll);
        if (mod == IntPtr.Zero) throw new InvalidOperationException($"LoadLibrary {dll}: {Marshal.GetLastWin32Error()}");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = GetProcAddress(mod, "RichEditWndProc"),
            cbWndExtra = IntPtr.Size,
            hInstance = _inst = GetModuleHandleW(null),
            lpszClassName = "QuillTrimEngineProof",
        };
        if (wc.lpfnWndProc == IntPtr.Zero || RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RichEditWndProc not registered");
    }

    public static RichEditTextDocument NewDoc()
    {
        const uint ES_MULTILINE = 0x4, ES_WANTRETURN = 0x1000, EM_GETOLEINTERFACE = 0x0400 + 60;
        IntPtr hwnd = CreateWindowExW(0, "QuillTrimEngineProof", "", ES_MULTILINE | ES_WANTRETURN,
                                      0, 0, 400, 400, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, _inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed");
        SendMessageW(hwnd, EM_GETOLEINTERFACE, IntPtr.Zero, out IntPtr ole);
        if (ole == IntPtr.Zero) throw new InvalidOperationException("EM_GETOLEINTERFACE returned nothing");
        Guid iid = typeof(RichEditTextDocument).Assembly.GetType("Microsoft.UI.Text.ITextDocument")!.GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(ole, ref iid, out IntPtr pDoc));
        return RichEditTextDocument.FromAbi(pDoc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string p);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string n);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h,
                                         IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, out IntPtr l);
}
