"""Dump the 72-column outer table exactly as ColorWheel.BuildOuterColumns
produces it, for run 14's screen check.  Uses scratchpad/mirror/palmodel.py,
which parses CopicPalette.cs itself so the model cannot drift from source."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'mirror'))
import palmodel  # noqa: E402

SRC = os.path.join(HERE, '..', 'src', 'Quill', 'Models', 'CopicPalette.cs')
SRC = os.path.normpath(SRC)

cols, ends, owner, sector_ids = palmodel.outer_columns(SRC)
sectors, order = palmodel.read_sectors(SRC)

print('within-family ordering direction in BuildColumns :', order)
print('sector order in SectorsRaw                       :', ' '.join(sector_ids))
print('columns                                          :', len(cols))
print('MaxRings (deepest column)                        :', max(len(s) for _, s in cols))
print()

COLSTEP = 360.0 / len(cols)
ROT = 100.0            # _rot default, degrees

out = []
fam_first = {}
for i, ((series, stack), end, own) in enumerate(zip(cols, ends, owner)):
    if own not in fam_first:
        fam_first[own] = i
    # ColStart[i] = i * ColStep at FamGap = 0; a0 measured from OuterStart(-90)
    a0 = -90.0 + i * COLSTEP + ROT
    out.append({
        'col': i, 'family': own, 'series': series, 'endsFamily': end,
        'a0': a0, 'aMid': a0 + COLSTEP * 0.5,
        'depth': len(stack),
        'codes': [c for c, h in stack],
        'hexes': ['#' + h.upper() for c, h in stack],
    })

print('%-4s %-4s %-6s %-5s %-9s %s' % ('col', 'fam', 'series', 'depth', 'aMid(deg)', 'codes'))
for r in out:
    print('%-4d %-4s %-6s %-5d %9.3f  %s' % (
        r['col'], r['family'], r['series'], r['depth'], r['aMid'] % 360,
        ' '.join(r['codes'])))

print()
print('FAMILY SPANS (screen bearing, degrees clockwise from 3 o\'clock,')
print('y-down screen coords so +angle is clockwise):')
fams = []
for own in dict.fromkeys(owner):
    idxs = [i for i, o in enumerate(owner) if o == own]
    a_start = (-90.0 + idxs[0] * COLSTEP + ROT) % 360
    a_end = (-90.0 + (idxs[-1] + 1) * COLSTEP + ROT) % 360
    fams.append((own, idxs[0], idxs[-1], a_start, a_end))
    print('  %-3s cols %2d..%-2d  %7.2f -> %7.2f deg   (%s)' % (
        own, idxs[0], idxs[-1], a_start, a_end,
        clock := '%d:%02d' % ((int(a_start / 30) + 3) % 12 or 12,
                              int((a_start % 30) * 2))))

json.dump(out, open(os.path.join(HERE, 'vp10', 'model.json'), 'w'), indent=1)
print()
print('wrote vp10/model.json')
