"""What SHAPE is the gap? Are the absent codes scattered, or whole blocks?

This decides whether the job is "slot 49 chips into existing columns" or
"the wheel is missing entire stretches of a family's number range". Prints,
per short family, the family's full Sketch range with present codes plain and
absent codes bracketed, in Copic order.

Run from the repo root:  python scratchpad/copic_shape.py
"""
import collections, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import copic_audit as A
from copic_plan import copic_key


def main():
    cur = A.palette_codes()
    off = A.official()
    markers = {k for k in cur if k not in ('White', 'Black')}
    missing = set(off) - markers

    fams = sorted({A.fam(c) for c in missing})
    print("Each family's full Sketch range in Copic order; [X] = absent from the wheel.\n")
    for f in fams:
        allc = sorted([c for c in off if A.fam(c) == f], key=copic_key, reverse=True)
        line = '  '.join(f"[{c}]" if c in missing else c for c in allc)
        print(f"{f} ({sum(1 for c in allc if c in missing)} of {len(allc)} absent)")
        print(f"   {line}\n")

    print("=" * 70)
    print("Saturation coverage: which <family><saturation> series the wheel has")
    print("=" * 70)
    print("Copic's second character is the saturation series. If a whole series")
    print("is absent, the wheel is missing a block of the family, not a gap.\n")
    for f in fams:
        pres = collections.defaultdict(list)
        absent = collections.defaultdict(list)
        for c in off:
            if A.fam(c) != f:
                continue
            (absent if c in missing else pres)[copic_key(c)[0]].append(c)
        sats = sorted(set(pres) | set(absent))
        bits = []
        for s in sats:
            if s in pres and s in absent:
                bits.append(f"{f}{s}x:partial({len(pres[s])}/{len(pres[s]) + len(absent[s])})")
            elif s in pres:
                bits.append(f"{f}{s}x:have")
            else:
                bits.append(f"{f}{s}x:MISSING-ALL")
        print(f"  {f:4s} {'  '.join(bits)}")


if __name__ == '__main__':
    main()
