"""Internal-consistency probe on the calibrated set.

A palette measured from real ink gives every marker its own hex; collisions
between unrelated codes are the signature of a list that was approximated
rather than measured. This prints every hex the calibrated palette reuses, and
every pair of DIFFERENT-family codes sharing one.

Run from the repo root:  python scratchpad/copic_dupes.py
"""
import collections
import copic_audit as A


def main():
    cur = A.palette_codes()
    byhex = collections.defaultdict(list)
    for code, hexv in cur.items():
        byhex[hexv].append(code)

    shared = {h: cs for h, cs in byhex.items() if len(cs) > 1}
    print(f"palette codes: {len(cur)}   distinct hexes: {len(byhex)}")
    print(f"hexes used by more than one code: {len(shared)}\n")

    cross, within = [], []
    for h, cs in sorted(shared.items()):
        fams = {A.fam(c) for c in cs}
        (cross if len(fams) > 1 else within).append((h, cs))

    print(f"-- ACROSS families ({len(cross)}) — two unrelated markers rendered identically")
    for h, cs in cross:
        print(f"   #{h}   {'  '.join(sorted(cs))}")
    print(f"\n-- within one family ({len(within)}) — two steps of one family collapsed")
    for h, cs in within:
        print(f"   #{h}   {'  '.join(sorted(cs))}")

    n = sum(len(cs) for _, cs in cross + within)
    print(f"\n{n} of {len(cur)} codes ({100 * n / len(cur):.0f}%) do not have a hex to themselves.")


if __name__ == '__main__':
    main()
