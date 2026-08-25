#!/usr/bin/env python3
"""THE CONTROL: is Concepts' palette the same calibration as Quill's?

Before a single colour is added from Concepts, this asks the only question
that matters: on the codes Quill *already has*, does Concepts agree with it?

If it agrees, the two are the same calibration and new codes will sit
seamlessly. If it does not, Concepts is a different calibration and adding
from it manufactures a visible seam beside every existing neighbour.

The yardstick is not zero. Adjacent markers in a family differ by a median of
~54-59 RGB units, so a disagreement should be read as a fraction of one marker
step: a median error of 27 means the typical colour is half a marker off.

Also runs the three-way comparison against meodai's public dataset (already in
scratchpad/), because the previous attempt refused that source on exactly this
evidence. If Concepts scores like meodai rather than like Quill, then Quill's
copicColors.js is the outlier and no external source will ever match it.
"""
import csv, json, os, re, statistics as st

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "src", "Quill", "Models", "CopicPalette.cs")
CON = os.path.join(HERE, "copic_out", "concepts_palette.json")
MEO = os.path.join(HERE, "meodai_copic.csv")

TOK = re.compile(r'\b([A-Za-z]*\d[0-9A-Za-z]*|White|Black)\s*:\s*([0-9a-fA-F]{6})\b')


def load_quill():
    txt = open(SRC, encoding='utf-8').read()
    return {m.group(1): m.group(2).lower() for m in TOK.finditer(txt)}


def load_meodai():
    x, h = {}, {}
    with open(MEO, encoding='utf-8') as f:
        rd = csv.DictReader(f)
        rd.fieldnames = [c.strip() for c in rd.fieldnames]
        for r in rd:
            c = r['number'].strip()
            x[c] = r['extractedColor'].strip().lstrip('#').lower()
            h[c] = r['hex'].strip().lstrip('#').lower()
    return ({k: v for k, v in x.items() if len(v) == 6},
            {k: v for k, v in h.items() if len(v) == 6})


rgb = lambda s: (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16))
dist = lambda a, b: sum((p - q) ** 2 for p, q in zip(rgb(a), rgb(b))) ** 0.5
fam = lambda c: (re.match(r'^[A-Z]*', c).group(0) or 'Core')


def main():
    quill, con = load_quill(), json.load(open(CON))
    meo_x, meo_h = load_meodai()

    shared = sorted(set(quill) & set(con))
    rows = [(c, quill[c], con[c], dist(quill[c], con[c])) for c in shared]
    ds = sorted(r[3] for r in rows)

    # the yardstick: how far apart are ADJACENT markers in Concepts itself
    byf = {}
    for c in con:
        byf.setdefault(fam(c), []).append(c)
    steps = []
    for f, cs in byf.items():
        cs.sort(key=lambda c: (len(c), c))
        steps += [dist(con[cs[i]], con[cs[i + 1]]) for i in range(len(cs) - 1)]
    step = st.median(steps)

    print("=" * 66)
    print(f"CONTROL  Quill({len(quill)}) vs Concepts({len(con)})  -> {len(rows)} shared codes")
    print("=" * 66)
    print(f"  exact matches : {sum(1 for d in ds if d == 0)} / {len(ds)}")
    print(f"  median dRGB   : {st.median(ds):.1f}   = {st.median(ds)/step:.2f} x one marker step")
    print(f"  mean          : {st.mean(ds):.1f}")
    for q in (0, 10, 25, 50, 75, 90, 100):
        print(f"  p{q:<4d}        : {ds[min(len(ds)-1, q*len(ds)//100)]:6.1f}")
    print(f"  within 8      : {sum(1 for d in ds if d <= 8)}")
    print(f"  beyond 1 step : {sum(1 for d in ds if d > step)}  "
          f"({100*sum(1 for d in ds if d > step)/len(ds):.0f}% further than two adjacent markers)")
    print(f"\n  yardstick: median step between adjacent Concepts markers = {step:.1f}")

    print("\n  per family (sorted worst first):")
    fams = {}
    for c, _, _, d in rows:
        fams.setdefault(fam(c), []).append(d)
    print(f"    {'fam':<5} {'n':>3}  {'median':>7} {'max':>7}  {'x step':>7}")
    for f in sorted(fams, key=lambda k: -st.median(fams[k])):
        m = st.median(fams[f])
        print(f"    {f:<5} {len(fams[f]):>3}  {m:>7.1f} {max(fams[f]):>7.1f}  {m/step:>6.2f}x")

    print("\n  worst 12:")
    for c, q, k, d in sorted(rows, key=lambda r: -r[3])[:12]:
        print(f"    {c:<7} quill=#{q}  concepts=#{k}   d={d:6.1f}")

    print("\n" + "=" * 66)
    print("THREE-WAY: is Concepts closer to Quill, or to the refused public source?")
    print("=" * 66)

    def pair(name, A, B):
        sh = sorted(set(A) & set(B))
        d = [dist(A[c], B[c]) for c in sh]
        ex = sum(1 for c in sh if A[c] == B[c])
        print(f"  {name:<32} n={len(sh):3d}  median={st.median(d):6.1f}  exact={ex}")

    pair("Quill    vs Concepts (DLL)", quill, con)
    pair("Quill    vs meodai extracted", quill, meo_x)
    pair("Quill    vs meodai hex", quill, meo_h)
    pair("Concepts vs meodai extracted", con, meo_x)
    pair("Concepts vs meodai hex", con, meo_h)

    verdict = ("SAME calibration - safe to add"
               if st.median(ds) < 8 else
               "DIFFERENT calibration - adding from Concepts creates a seam")
    print(f"\n  VERDICT: {verdict}")


if __name__ == "__main__":
    main()
