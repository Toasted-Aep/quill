"""Plan where the absent Sketch codes would go, and where each hex comes from.

Writes scratchpad/copic_additions.json. Writes NOTHING into the palette: the
colour data is gated on the user's approval, so the plan stays a side file and
the renderer applies it in memory.

PLACEMENT follows the convention the wheel actually uses. Measured on the
shipped palette, 30 of 36 columns are in strictly descending Copic order over
all their members, while only 10 of 36 run dark-to-light outward. So the
ordering rule is the CODE, not the luminance - which is why the inherited
place.py, which minimised luminance inversions, was solving for the wrong
invariant. Each absent code is inserted at its sorted position, into the column
of its family whose existing members bracket it most tightly.

SOURCING is the part that is not equivalent to how the 316 were derived, and
the file records that per code rather than hiding it. The 316 were lifted
verbatim from the user's web copicColors.js; that file has no entry for any of
these, and no available source reproduces the calibration (see copic_famfit).
The hexes here are meodai's swatch-extracted values - the closest-agreeing
source of the three tested - and they are a PROPOSAL to look at in the render,
not a claim of equivalence.

Run from the repo root:  python scratchpad/copic_plan.py
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import copic_audit as A

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'copic_additions.json')

# Which sector id each family letter belongs to, from the palette's own layout.
FAM_SECTOR = {
    'R': 'red', 'RV': 'red-violet', 'V': 'violet', 'BV': 'blue-violet',
    'B': 'blue', 'BG': 'blue-green', 'G': 'green', 'YG': 'yellow-green',
    'Y': 'yellow', 'E': 'earth', 'YR': 'yellow-red',
}


def copic_key(code):
    """Descending-order key: (saturation digit, brightness digits).

    Copic numbers a marker as <family><saturation><brightness>, and the
    trailing-zero forms run BELOW 1: R0000 < R000 < R00 < R01.
    """
    f = A.fam(code)
    d = code[len(f):]
    rest = d[1:]
    if rest == '':
        br = 0
    elif set(rest) == {'0'}:
        br = -len(rest)
    else:
        br = int(rest)
    return (int(d[0]), br)


def main():
    cur = A.palette_codes()
    off = A.official()
    secs = A.sectors()
    markers = {k for k in cur if k not in ('White', 'Black')}
    missing = sorted(set(off) - markers, key=lambda c: (A.fam(c), A.numkey(c)))

    # sector id -> list of columns, each a list of (code, hex)
    cols = {sid: [list(cells) for _, _, cells in sl] for sid, _, sl in secs}
    angles = {sid: [(a0, a1) for a0, a1, _ in sl] for sid, _, sl in secs}
    before_depth = {sid: [len(c) for c in cols[sid]] for sid in cols}

    src = {k: v['extractedColor'].lstrip('#').lower() for k, v in off.items()}

    # Deal each family's absent codes across that family's OWN columns, always
    # into the currently shallowest one. Two reasons this rather than "the
    # column that brackets it":
    #
    #  * The gap is not scattered. RV is missing RV3x-RV9x entire, BG is missing
    #    BG0x-BG2x entire, B is missing B9x entire (see copic_shape.py). A whole
    #    block sorts above or below EVERYTHING present in its family, so nothing
    #    brackets it and a bracketing rule dumps all 13 into one column - which
    #    took RV's first column from 7 cells to 20 and grew the whole wheel.
    #  * Dealing across columns is what the reference itself does: a family's
    #    codes are spread over its columns rather than stacked into the first.
    #
    # Existing cells never move; the new ones are inserted at their sorted
    # position among their own family's members, so every column stays in
    # descending Copic order - the invariant 30 of 36 columns already hold.
    additions = []
    byfam_missing = {}
    for code in missing:
        byfam_missing.setdefault(A.fam(code), []).append(code)

    for f, codes in byfam_missing.items():
        sid = FAM_SECTOR[f]
        for code in sorted(codes, key=copic_key, reverse=True):
            k = copic_key(code)
            ci = min(range(len(cols[sid])), key=lambda i: (len(cols[sid][i]), i))
            cells = cols[sid][ci]

            # Insert among this family's members only, keeping them descending.
            same_idx = [i for i, (c, _) in enumerate(cells) if A.fam(c) == f]
            pos = (same_idx[-1] + 1) if same_idx else len(cells)
            for i in same_idx:
                if copic_key(cells[i][0]) < k:
                    pos = i
                    break
            hexv = src.get(code, '')
            cells.insert(pos, (code, hexv))
            additions.append((code, f, sid, ci, pos, hexv))

    additions = [
        {
            'code': code,
            'name': off[code]['name'],
            'family': f,
            'sector': sid,
            'column': ci,
            'angles': angles[sid][ci],
            'index': pos,
            'hex': hexv,
            'source': 'meodai.extractedColor',
            'calibrated_like_the_316': False,
        }
        for code, f, sid, ci, pos, hexv in additions
    ]
    additions.sort(key=lambda a: (a['family'], A.numkey(a['code'])))

    after_depth = {sid: [len(c) for c in cols[sid]] for sid in cols}
    before_max = max(d for v in before_depth.values() for d in v)
    after_max = max(d for v in after_depth.values() for d in v)

    json.dump({
        'note': 'PROPOSAL ONLY - not calibrated the way the existing 316 were.',
        'source': 'meodai extractedColor; the calibration source has no entry for these codes',
        'before_max_rings': before_max,
        'after_max_rings': after_max,
        'additions': additions,
        'columns_after': {sid: [[list(x) for x in c] for c in cols[sid]] for sid in cols},
    }, open(OUT, 'w'), indent=1)

    print(f"planned {len(additions)} additions -> {OUT}\n")
    print(f"MaxRings before {before_max}   after {after_max}   "
          f"=> wheel extent {'GROWS' if after_max > before_max else 'UNCHANGED'}")
    if after_max > before_max:
        print(f"   outer radius scales with MaxRings, so it grows by "
              f"{100 * (after_max - before_max) / before_max:.0f}% of a column's worth of rings")
    print("\nper column, depth before -> after:")
    for sid in cols:
        b, a = before_depth[sid], after_depth[sid]
        if b != a:
            print(f"  {sid:14s} " + '  '.join(
                f"{x}->{y}{'*' if y > x else ''}" for x, y in zip(b, a)))
    print("\nby family:")
    byf = {}
    for a in additions:
        byf.setdefault(a['family'], []).append(a)
    for f in sorted(byf):
        print(f"  {f:4s} {len(byf[f]):2d}  " + '  '.join(
            f"{a['code']}#{a['hex']}" for a in byf[f]))


if __name__ == '__main__':
    main()
