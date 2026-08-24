"""Does a proposed swatch sit right beside the ones it lands between?

copic_famfit measures a source against the calibrated hexes of codes that are
already present. This measures the thing the eye actually sees in the ring: how
far a NEW cell sits from the cells immediately above and below it in its
column, against how far the existing cells of that column sit from each other.

A newcomer whose gap to its neighbours is in line with the column's own rhythm
disappears into the ramp. One whose gap is several times the column's typical
step is a seam - a visible break in a family that is supposed to run smoothly
from dark to light.

Run from the repo root:  python scratchpad/copic_seam.py
"""
import json, os, statistics, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import copic_audit as A

PLAN = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'copic_additions.json')


def fam_step(name, cells, added):
    """Median step between adjacent markers of this family, across the palette."""
    cur = A.palette_codes()
    fams = {A.fam(c) for c, _ in cells if c not in added}
    if not fams:
        fams = {A.fam(c) for c, _ in cells}
    f = max(fams, key=lambda x: sum(1 for c, _ in cells if A.fam(c) == x))
    cs = sorted([c for c in cur if c not in ('White', 'Black') and A.fam(c) == f],
                key=A.numkey)
    d = sorted(A.dist(cur[a], cur[b]) for a, b in zip(cs, cs[1:]))
    return d[len(d) // 2] if d else float('nan')


def main():
    plan = json.load(open(PLAN))
    added = {a['code'] for a in plan['additions']}
    secs = A.sectors()
    cols_after = plan['columns_after']

    print("For every column that gains a cell: the column's own median step")
    print("between ADJACENT existing cells, then each newcomer's gap to the")
    print("neighbours it lands between, as a multiple of that step.\n")
    print(f"{'column':22s} {'step':>6s}  newcomers (gap / step)")
    print("-" * 78)

    ratios = []
    for sid, name, sl in secs:
        for ci in range(len(sl)):
            cells = [tuple(x) for x in cols_after[sid][ci]]
            if not any(c in added for c, _ in cells):
                continue
            # the column's own rhythm, measured over pairs of EXISTING cells only
            old_steps = [A.dist(h1, h2)
                         for (c1, h1), (c2, h2) in zip(cells, cells[1:])
                         if c1 not in added and c2 not in added]
            # A column can have too few existing cells to have a rhythm of its
            # own - RV's third column holds only RV09 today. Fall back to the
            # family's median step across the whole palette.
            step = statistics.median(old_steps) if old_steps else fam_step(name, cells, added)
            bits = []
            for i, (code, hexv) in enumerate(cells):
                if code not in added:
                    continue
                gaps = []
                if i > 0:
                    gaps.append(A.dist(hexv, cells[i - 1][1]))
                if i < len(cells) - 1:
                    gaps.append(A.dist(hexv, cells[i + 1][1]))
                g = min(gaps) if gaps else float('nan')
                r = g / step if step and step == step else float('nan')
                ratios.append((code, r))
                bits.append(f"{code}={r:.1f}x")
            print(f"{name[:14]:14s} col{ci}   {step:6.1f}  " + '  '.join(bits))

    ok = [c for c, r in ratios if r <= 1.0]
    loud = sorted([(r, c) for c, r in ratios if r > 2.0], reverse=True)
    print(f"\n{len(ratios)} newcomers: {len(ok)} sit within their column's own step,")
    print(f"{len(loud)} sit more than TWICE it - each of those is a visible seam.")
    if loud:
        print("\nloudest:")
        for r, c in loud[:15]:
            print(f"   {c:8s} {r:5.1f}x its column's median step")


if __name__ == '__main__':
    main()
