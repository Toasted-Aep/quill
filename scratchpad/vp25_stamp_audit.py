"""Run 25: census the places new content objects are constructed, and say which
of them stamp a LayerKey.

16a557a claims "18.9 seam 5's stamping at the twelve places new content is
made". The interesting number is not twelve - it is how many construction sites
DO NOT stamp, because an unstamped element silently lands on the base layer and
the user finds out when they hide a layer and their ink stays.

A construction site is `new PenStroke`/`new ShapeElement`/`new TextElement`
followed by an object initialiser. The initialiser is found by matching braces
from the first `{` after the type name, so a nested `new(...)` inside it does
not end the block early.
"""
import io, os, re, sys

ROOT = r"C:\Users\irony\Downloads\Quill Gem - Fable\Quill\src\Quill"
TYPES = ("PenStroke", "ShapeElement", "TextElement")
PAT = re.compile(r"\bnew\s+(" + "|".join(TYPES) + r")\b")


def block_after(text, i):
    """Return (initialiser_text, end_index) for the {...} starting at/after i,
    or (None, i) when this `new X(...)` has no initialiser at all."""
    j = i
    n = len(text)
    # skip a constructor argument list if present
    while j < n and text[j] in " \t\r\n":
        j += 1
    if j < n and text[j] == "(":
        depth = 0
        while j < n:
            if text[j] == "(":
                depth += 1
            elif text[j] == ")":
                depth -= 1
                if depth == 0:
                    j += 1
                    break
            j += 1
    while j < n and text[j] in " \t\r\n":
        j += 1
    if j >= n or text[j] != "{":
        return None, j
    depth = 0
    start = j
    while j < n:
        if text[j] == "{":
            depth += 1
        elif text[j] == "}":
            depth -= 1
            if depth == 0:
                return text[start:j + 1], j + 1
        j += 1
    return None, j


rows = []
for dirpath, _dirs, files in os.walk(ROOT):
    for fn in files:
        if not fn.endswith(".cs"):
            continue
        p = os.path.join(dirpath, fn)
        text = io.open(p, encoding="utf-8-sig", errors="replace").read()
        rel = os.path.relpath(p, ROOT)
        for m in PAT.finditer(text):
            init, _end = block_after(text, m.end())
            line = text.count("\n", 0, m.start()) + 1
            if init is None:
                rows.append((rel, line, m.group(1), "no-initialiser", ""))
                continue
            if "LayerKey" in init:
                which = "ActiveLayerKey" if "ActiveLayerKey" in init else "other-key"
                rows.append((rel, line, m.group(1), "STAMPED", which))
            else:
                rows.append((rel, line, m.group(1), "not-stamped", ""))

stamped = [r for r in rows if r[3] == "STAMPED"]
active = [r for r in stamped if r[4] == "ActiveLayerKey"]
other = [r for r in stamped if r[4] == "other-key"]
bare = [r for r in rows if r[3] == "not-stamped"]
noinit = [r for r in rows if r[3] == "no-initialiser"]

print("construction sites for PenStroke / ShapeElement / TextElement under src/Quill")
print("  total with an object initialiser :", len(stamped) + len(bare))
print("  STAMPED from ActiveLayerKey      :", len(active))
print("  STAMPED from another key         :", len(other), "(table cells take the TABLE's key, 18.10)")
print("  NOT stamped                      :", len(bare))
print("  no initialiser (ctor only)       :", len(noinit))
print()
print("--- stamped from ActiveLayerKey ---")
for r in active:
    print("   {}:{}  {}".format(r[0], r[1], r[2]))
print()
print("--- stamped from another element's key ---")
for r in other:
    print("   {}:{}  {}".format(r[0], r[1], r[2]))
print()
print("--- NOT stamped (each lands on the base layer by default) ---")
for r in bare:
    print("   {}:{}  {}".format(r[0], r[1], r[2]))
print()
print("--- no initialiser ---")
for r in noinit:
    print("   {}:{}  {}".format(r[0], r[1], r[2]))
