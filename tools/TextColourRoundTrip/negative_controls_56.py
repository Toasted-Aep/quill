"""CONCEPTS-REF 56 negative controls. Run by path from any directory:
    python tools/TextColourRoundTrip/negative_controls_56.py
Exit 0 means every mutant compiled src/Quill with 0 errors, built both
harnesses clean, and turned the checks it names red; and the restored
sources are byte-identical (sha256) and green again.

Break the REAL shipping code - src/Quill/Services/TextFlushPolicy.cs (the
decisions) or src/Quill/Services/TextTrim.cs (the trim that runs against the
live document) - then, for each mutant:
  1. build src/Quill x64            - must be 0 errors (a mutant that does not
                                      compile in the app is not a control);
  2. build TextColourRoundTrip      - 0 errors, only the 120 pre-existing CS0436;
  3. build TrimEngineProof          - 0 errors (it links TextTrim.cs and
                                      TextFlushPolicy.cs and drives them against
                                      WinUIEdit.dll through a message-only
                                      window: no screen, Quill.exe not launched);
  4. run both harnesses; every check the mutant NAMES must be FAIL in the
     harness named, and a mutant naming no check must turn TextColourRoundTrip
     red somewhere;
  5. put the original bytes back (sha256 checked) - whatever happens.
Writes nothing but those two source files."""
import hashlib, os, re, subprocess, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
POLICY = os.path.join(ROOT, r"src\Quill\Services\TextFlushPolicy.cs")
TRIM = os.path.join(ROOT, r"src\Quill\Services\TextTrim.cs")
QUILL = os.path.join(ROOT, r"src\Quill\Quill.csproj")
TCRT_PROJ = os.path.join(ROOT, r"tools\TextColourRoundTrip\TextColourRoundTrip.csproj")
TCRT_EXE = os.path.join(ROOT, r"tools\TextColourRoundTrip\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\TextColourRoundTrip.exe")
TEP_PROJ = os.path.join(ROOT, r"tools\TrimEngineProof\TrimEngineProof.csproj")
TEP_EXE = os.path.join(ROOT, r"tools\TrimEngineProof\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\TrimEngineProof.exe")
NL = "\r\n"
BS = chr(92)          # a backslash, built rather than typed
R = BS + "r"          # the two characters \r as they appear in C# source

def eol_ok(raw):
    crlf = raw.count(b"\r\n"); bare = raw.count(b"\n") - crlf
    return bare == 0 and crlf > 0

FIRST = "        if (string.IsNullOrEmpty(plain) || plain[^1] != '" + R + "') return (plain?.Length ?? 0, 0);"
BELT = ("            if (c == '" + BS + "uFFF9' || c == '" + BS + "uFFFA' || c == '" + BS + "uFFFB' || c == '"
        + BS + "u0007') return true;")

# (name, file, [(old, new), ...], [(harness, substring of the check label), ...])
# harness is "TCRT" (TextColourRoundTrip) or "TEP" (TrimEngineProof).
MUTANTS = [
    ("A: trim a box that was REACHED but not edited (a reach counts as an edit)", POLICY,
     [("        if (!reached || documentWhenReached is null) return false;" + NL +
       "        return !string.Equals(documentWhenReached, live, System.StringComparison.Ordinal);",
       "        if (!reached) return false;" + NL +
       "        return reached;")], []),
    ("B: trim drops INTERIOR blank lines (takes the first run of marks, not the trailing one)", POLICY,
     [(FIRST,
       FIRST + NL +
       "        int ma = plain.IndexOf(\"" + R + R + "\", System.StringComparison.Ordinal);" + NL +
       "        int mb = ma; while (mb >= 0 && mb < plain.Length && plain[mb] == '" + R + "') mb++;" + NL +
       "        if (ma >= 0 && mb < plain.Length) return (ma + 1, mb - ma - 1);")], []),
    ("C1: trim drops the story's FINAL paragraph mark too (range shifted onto it)", POLICY,
     [("        int start = plain.Length - 1 - drop;", "        int start = plain.Length - drop;")], []),
    ("C2: trim drops the CONTENT's own paragraph mark (keeps no empty paragraph)", POLICY,
     [("        int keep = hasContent ? 2 : 1;", "        int keep = 1;")], []),
    ("D: trim while the box is still LIVE (releasing ignored)", POLICY,
     [("        if (!releasing) return false;" + NL, "")], []),
    ("E: an all-empty box is emptied to nothing (final mark taken)", POLICY,
     [("        int keep = hasContent ? 2 : 1;", "        int keep = hasContent ? 2 : 0;")], []),
    # ---- 56.8 ---------------------------------------------------------------
    ("W: the run scanner treats WHITESPACE as a paragraph mark (a space/tab/no-break-space paragraph counts as empty)", POLICY,
     [("        while (run < plain.Length && plain[plain.Length - 1 - run] == '" + R + "') run++;",
       "        while (run < plain.Length && char.IsWhiteSpace(plain[plain.Length - 1 - run])) run++;")],
     [("TCRT", "[6n] A TRAILING RUN THAT MIXES WHITESPACE"), ("TEP", "[8h]")]),
    ("G1: the RTF table gate answers 'no table' for every document", POLICY,
     [("            if (rtf.Contains(word, System.StringComparison.Ordinal)) return true;",
       "            if (rtf.Contains(word, System.StringComparison.Ordinal)) return false;")],
     [("TCRT", "[6m] A TABLE-SHAPED STORY"), ("TCRT", "[6m] THE RTF GATE ALONE"),
      ("TCRT", "[6m] ...AND THROUGH THE MIRRORED FLUSH"), ("TEP", "[8l]")]),
    ("G2: the plain-text table belt answers 'no table' for every story", POLICY,
     [(BELT, BELT.replace("return true;", "return false;"))],
     [("TCRT", "[6m] THE PLAIN-TEXT BELT ALONE"), ("TEP", "[8l]")]),
    ("G3: TextTrim IGNORES THE TABLE GATE - neither refusal is asked", TRIM,
     [("        if (TextFlushPolicy.ContainsTableStructure(before)) return null;",
       "        // mutant G3: the RTF table gate is not asked"),
      ("            if (TextFlushPolicy.PlainTextShowsTable(plain)) return null;",
       "            // mutant G3: the plain-text table belt is not asked")],
     [("TEP", "[8l]")]),
    ("F1: the survivor is NOT given the first empty paragraph's formatting (the copy is skipped)", TRIM,
     [("            if (!finalAfter.ParagraphFormat.IsEqual(paraFirst) ||",
       "            if (paraFirst is null &&")],
     [("TEP", "[8e]"), ("TEP", "[8f]")]),
    ("F2: the copy writes the WRONG formatting - its IsEqual check must refuse and restore", TRIM,
     [("                finalAfter.ParagraphFormat = paraFirst;" + NL +
       "                finalAfter.CharacterFormat = charFirst;",
       "                finalAfter.ParagraphFormat = paraBefore;" + NL +
       "                finalAfter.CharacterFormat = charBefore;")],
     [("TEP", "[8e]"), ("TEP", "[8f]")]),
]

def build(proj):
    r = subprocess.run(["dotnet", "build", proj, "-c", "Debug", "-p:Platform=x64"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = r.stdout + r.stderr
    w = re.findall(r"^\s*(\d+) Warning\(s\)", out, re.M)
    e = re.findall(r"^\s*(\d+) Error\(s\)", out, re.M)
    codes = sorted(set(re.findall(r"(?:warning|error) (CS\d+)", out)))
    return r.returncode, (int(w[-1]) if w else -1), (int(e[-1]) if e else -1), codes

def run(exe):
    r = subprocess.run([exe], capture_output=True, text=True, encoding="utf-8", errors="replace")
    fails = [l for l in r.stdout.splitlines() if l.startswith("FAIL")]
    summary = [l for l in r.stdout.splitlines() if l.startswith("OK -") or "CHECK(S) FAILED" in l]
    return r.returncode, fails, (summary[0] if summary else "(no summary)")

def build_all():
    """Returns (ok, lines). ok means all three compiled with 0 errors and the
    harness warnings are the pre-existing ones."""
    lines, ok = [], True
    for label, proj in (("src/Quill", QUILL), ("TextColourRoundTrip", TCRT_PROJ), ("TrimEngineProof", TEP_PROJ)):
        bc, w, e, codes = build(proj)
        lines.append(f"   build {label}: exit={bc} warnings={w} errors={e} codes={codes}")
        if bc != 0 or e != 0: ok = False
        if label == "TextColourRoundTrip" and (w != 120 or codes not in ([], ["CS0436"])): ok = False
        if label != "TextColourRoundTrip" and w != 0: ok = False
    return ok, lines

originals = {p: open(p, "rb").read() for p in (POLICY, TRIM)}
shas = {p: hashlib.sha256(b).hexdigest() for p, b in originals.items()}
for p, b in originals.items(): assert eol_ok(b), f"{p}: line endings are not all CRLF"

def restore():
    for p, b in originals.items():
        open(p, "wb").write(b)
        assert hashlib.sha256(open(p, "rb").read()).hexdigest() == shas[p]

all_red = True
try:
    for name, path, edits, expect in MUTANTS:
        print(f"== {name}")
        text = originals[path].decode("utf-8")
        bad = [old for old, _ in edits if text.count(old) != 1]
        if bad:
            print(f"   MUTATION DID NOT APPLY: {len(bad)} pattern(s) not found exactly once"); all_red = False; continue
        for old, new in edits: text = text.replace(old, new)
        mutated = text.encode("utf-8")
        assert eol_ok(mutated), "mutation broke line endings"
        open(path, "wb").write(mutated)
        ok, lines = build_all()
        for l in lines: print(l)
        if not ok:
            print("   BUILD NOT CLEAN - this mutant proves nothing"); all_red = False
        else:
            results = {"TCRT": run(TCRT_EXE), "TEP": run(TEP_EXE)}
            for h, (rc, fails, summary) in results.items():
                print(f"   {h}: exit={rc}  {summary}")
                for f in fails: print("      " + f[:200])
            if not expect:
                if results["TCRT"][0] == 0 or not results["TCRT"][1]:
                    print("   STAYED GREEN - control failed"); all_red = False
            for h, label in expect:
                hit = any(label in f for f in results[h][1])
                print(f"   named {h} {label!r}: {'RED' if hit else 'STAYED GREEN - control failed'}")
                if not hit or results[h][0] == 0: all_red = False
        restore()
finally:
    restore()
    for p in originals: print(f"restored {os.path.basename(p)}, sha256 {shas[p][:16]} identical")

ok, lines = build_all()
for l in lines: print(l)
green = True
for h, exe in (("TCRT", TCRT_EXE), ("TEP", TEP_EXE)):
    rc, fails, summary = run(exe)
    print(f"== RESTORED {h}: exit={rc} {summary} fails={len(fails)}")
    if rc != 0 or fails: green = False
print("ALL CONTROLS RED, RESTORED GREEN" if all_red and ok and green else "NEGATIVE CONTROLS DID NOT ALL HOLD")
sys.exit(0 if all_red and ok and green else 1)
