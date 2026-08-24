"""Per-family sourcing agreement, for the families that are actually short.

The global agreement figure hides the thing that matters. The calibrated set
deviates from published Copic values by very different amounts family to
family - the calibration commit says outright that it DARKENED the Earth
family - so "can a source supply the absent codes" has to be asked per family,
over the families that have absent codes, not over all 309 at once.

Run from the repo root:  python scratchpad/copic_famfit.py
"""
import collections
import copic_audit as A


def main():
    cur = A.palette_codes()
    off = A.official()
    markers = {k for k in cur if k not in ('White', 'Black')}
    missing = sorted(set(off) - markers, key=lambda c: (A.fam(c), A.numkey(c)))
    short = sorted({A.fam(c) for c in missing})

    srcs = {
        'meodai.extracted': {k: v['extractedColor'].lstrip('#').lower() for k, v in off.items()},
        'meodai.hex': {k: v['hex'].lstrip('#').lower() for k, v in off.items()},
        'alexjunq': A.aj_codes(),
    }

    # The scale that matters: how far apart two ADJACENT markers sit, per family.
    byfam = collections.defaultdict(list)
    for c in markers:
        byfam[A.fam(c)].append(c)
    fam_step = {}
    for f, cs in byfam.items():
        cs = sorted(cs, key=A.numkey)
        d = sorted(A.dist(cur[a], cur[b]) for a, b in zip(cs, cs[1:]))
        if d:
            fam_step[f] = d[len(d) // 2]

    print("For each short family: how far the source sits from the CALIBRATED")
    print("hexes of the codes already present, against that family's own median")
    print("step between adjacent markers. A ratio near or above 1.0 means a")
    print("sourced swatch would land about as far from its true place as its")
    print("neighbour does - i.e. visibly wrong in the ring.\n")

    hdr = f"{'family':7s} {'absent':>6s} {'present':>7s} {'step':>6s}"
    for s in srcs:
        hdr += f" | {s:>17s}"
    print(hdr)
    print(f"{'':7s} {'':>6s} {'':>7s} {'':>6s}" + " | {:>17s}".format("median  ratio") * len(srcs))
    print("-" * len(hdr))

    summary = {}
    for f in short:
        present = [c for c in byfam[f]]
        nmiss = sum(1 for c in missing if A.fam(c) == f)
        step = fam_step.get(f, float('nan'))
        row = f"{f:7s} {nmiss:6d} {len(present):7d} {step:6.1f}"
        for sname, ds in srcs.items():
            sh = [c for c in present if c in ds and len(ds[c]) == 6]
            if not sh:
                row += f" | {'-':>17s}"
                continue
            dl = sorted(A.dist(cur[c], ds[c]) for c in sh)
            med = dl[len(dl) // 2]
            ratio = med / step if step else float('nan')
            summary.setdefault(sname, []).append((f, med, ratio))
            row += f" | {med:8.1f} {ratio:7.2f}"
        print(row)

    print("\nverdict per source, over the short families only:")
    for sname, rows in summary.items():
        good = [f for f, _, r in rows if r < 0.5]
        bad = [f for f, _, r in rows if r >= 0.5]
        worst = max(rows, key=lambda t: t[2])
        print(f"  {sname:18s} ratio<0.5 in {len(good)}/{len(rows)} families "
              f"{good if good else ''}   worst: {worst[0]} {worst[2]:.2f}")


if __name__ == '__main__':
    main()
