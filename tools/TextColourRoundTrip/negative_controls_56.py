"""CONCEPTS-REF 56 negative controls. Run by path from any directory:
    python tools/TextColourRoundTrip/negative_controls_56.py
Exit 0 means every mutant built clean AND turned the harness red, and the
restored source is green again.

Negative controls for CONCEPTS-REF 56: break the REAL TextFlushPolicy.cs,
rebuild the harness, run it, require (build clean AND harness red), restore
the exact original bytes. Writes nothing but TextFlushPolicy.cs, and puts it
back byte for byte (sha256 checked) whatever happens."""
import hashlib, os, re, subprocess, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, r"src\Quill\Services\TextFlushPolicy.cs")
PROJ = os.path.join(ROOT, r"tools\TextColourRoundTrip\TextColourRoundTrip.csproj")
EXE = os.path.join(ROOT, r"tools\TextColourRoundTrip\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\TextColourRoundTrip.exe")
NL = "\r\n"

def eol_ok(raw):
    crlf = raw.count(b"\r\n"); bare = raw.count(b"\n") - crlf
    return bare == 0 and crlf > 0

FIRST = "        if (string.IsNullOrEmpty(plain) || plain[^1] != '\\r') return (plain?.Length ?? 0, 0);"
MUTANTS = [
    ("A: trim a box that was REACHED but not edited (a reach counts as an edit)",
     "        if (!reached || documentWhenReached is null) return false;" + NL +
     "        return !string.Equals(documentWhenReached, live, System.StringComparison.Ordinal);",
     "        if (!reached) return false;" + NL +
     "        return reached;"),
    ("B: trim drops INTERIOR blank lines (takes the first run of marks, not the trailing one)",
     FIRST,
     FIRST + NL +
     "        int ma = plain.IndexOf(\"\\r\\r\", System.StringComparison.Ordinal);" + NL +
     "        int mb = ma; while (mb >= 0 && mb < plain.Length && plain[mb] == '\\r') mb++;" + NL +
     "        if (ma >= 0 && mb < plain.Length) return (ma + 1, mb - ma - 1);"),
    ("C1: trim drops the story's FINAL paragraph mark too (range shifted onto it)",
     "        int start = plain.Length - 1 - drop;",
     "        int start = plain.Length - drop;"),
    ("C2: trim drops the CONTENT's own paragraph mark (keeps no empty paragraph)",
     "        int keep = hasContent ? 2 : 1;",
     "        int keep = 1;"),
    ("D: trim while the box is still LIVE (releasing ignored)",
     "        if (!releasing) return false;" + NL,
     ""),
    ("E: an all-empty box is emptied to nothing (final mark taken)",
     "        int keep = hasContent ? 2 : 1;",
     "        int keep = hasContent ? 2 : 0;"),
]

def build():
    r = subprocess.run(["dotnet", "build", PROJ, "-c", "Debug", "-p:Platform=x64"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = r.stdout + r.stderr
    w = re.findall(r"^\s*(\d+) Warning\(s\)", out, re.M)
    e = re.findall(r"^\s*(\d+) Error\(s\)", out, re.M)
    codes = sorted(set(re.findall(r"(?:warning|error) (CS\d+)", out)))
    return r.returncode, (int(w[-1]) if w else -1), (int(e[-1]) if e else -1), codes

def run():
    r = subprocess.run([EXE], capture_output=True, text=True, encoding="utf-8", errors="replace")
    fails = [l for l in r.stdout.splitlines() if l.startswith("FAIL")]
    summary = [l for l in r.stdout.splitlines() if l.startswith("OK -") or "CHECK(S) FAILED" in l]
    return r.returncode, fails, summary

orig = open(SRC, "rb").read()
orig_sha = hashlib.sha256(orig).hexdigest()
assert eol_ok(orig)
text = orig.decode("utf-8")
all_red = True
try:
    for name, old, new in MUTANTS:
        n = text.count(old)
        if n != 1:
            print(f"== {name}\n   MUTATION DID NOT APPLY: pattern found {n} times"); all_red = False; continue
        mutated = text.replace(old, new).encode("utf-8")
        assert eol_ok(mutated), "mutation broke line endings"
        open(SRC, "wb").write(mutated)
        bc, w, e, codes = build()
        print(f"== {name}")
        print(f"   build exit={bc} warnings={w} errors={e} codes={codes}")
        if bc != 0 or e != 0:
            print("   BUILD FAILED - this mutant proves nothing"); all_red = False
        else:
            rc, fails, summary = run()
            print(f"   harness exit={rc}  {summary[0] if summary else ''}")
            for f in fails: print("   " + f[:230])
            if rc == 0 or not fails:
                print("   STAYED GREEN - control failed"); all_red = False
        open(SRC, "wb").write(orig)
finally:
    open(SRC, "wb").write(orig)
    assert hashlib.sha256(open(SRC, "rb").read()).hexdigest() == orig_sha
    print(f"restored TextFlushPolicy.cs, sha256 {orig_sha[:16]} identical")

bc, w, e, codes = build()
rc, fails, summary = run()
print(f"== RESTORED: build exit={bc} warnings={w} errors={e}; harness exit={rc} {summary[0] if summary else ''} fails={len(fails)}")
sys.exit(0 if all_red and rc == 0 and e == 0 else 1)
