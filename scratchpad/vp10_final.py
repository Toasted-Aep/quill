"""Consolidate run 14's wheel evidence:
  - every press log, pooled
  - which of the 72 columns were seen DRAWN, across the three docks
  - the family order right round the circle, as drawn
  - the outer ink radius, MEASURED on screen, against the reference's 647.9 DIP
"""
import csv
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vp10_scan import decode  # noqa: E402
from PIL import Image  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(HERE, 'vp10')
MODEL = json.load(open(os.path.join(SHOTS, 'model.json')))

# (capture, centre px) for each dial dock used
DOCKS = [
    ('c01-wheel.png', 285.24, 366.00, 'TopLeft'),
    ('f01-wheel-br.png', 2594.76, 1354.76, 'BottomRight'),
    ('g01-wheel.png', 2594.76, 366.00, 'TopRight'),
    ('h01-wheel.png', 285.24, 1354.76, 'BottomLeft'),
]
ROUTBASE, BAND = 664.82, 56.20
SCALE = 2.0          # physical px per DIP
WHEEL_SCALE = 0.900  # Layout()'s s, from QUILL_GEOM_PROBE

print('=== PRESS LOGS, POOLED ===')
tot = ok = dbad = pbad = 0
seams = 0
percol = {}
for name in ('sweep-seams.csv', 'sweep-b.csv', 'sweep-br.csv',
             'sweep-tr.csv', 'sweep-bl.csv', 'sweep-bl2.csv', 'sweep-tr2.csv'):
    p = os.path.join(SHOTS, name)
    if not os.path.exists(p):
        continue
    n = 0
    for r in csv.DictReader(open(p)):
        n += 1
        tot += 1
        if r['drawnOk'] != '1':
            dbad += 1
        if r['pickOk'] != '1':
            pbad += 1
        if r['drawnOk'] == '1' and r['pickOk'] == '1':
            ok += 1
        if r['kind'].startswith('seam'):
            seams += 1
        percol.setdefault(int(r['col']), 0)
        percol[int(r['col'])] += 1
    print('  %-16s %4d presses' % (name, n))
print('  TOTAL PRESSES                     : %d' % tot)
print('  drawn pixel == palette expectation: %d   (mismatches %d)' % (tot - dbad, dbad))
print('  dial dot    == drawn pixel        : %d   (mismatches %d)' % (tot - pbad, pbad))
print('  both        : %d' % ok)
print('  at a family boundary sliver       : %d' % seams)
print('  distinct columns pressed          : %d of 72' % len(percol))
print()

print('=== EVERY COLUMN SEEN DRAWN, ACROSS THE FOUR DOCKS ===')
drawn_ok = {}
famseq_all = {}
for fn, cx, cy, dock in DOCKS:
    img = Image.open(os.path.join(SHOTS, fn)).convert('RGB')
    W, H = img.size
    px = img.load()
    seen = []
    for c in range(72):
        m = MODEL[c]
        a = 12.5 + 5.0 * c
        r = ROUTBASE + 0.5 * BAND
        x = int(round(cx + r * math.cos(math.radians(a))))
        y = int(round(cy + r * math.sin(math.radians(a))))
        if not (0 <= x < W and 0 <= y < H and y >= 150):
            continue
        hx = '#%02X%02X%02X' % px[x, y][:3]
        good = (hx == m['hexes'][0])
        drawn_ok[c] = drawn_ok.get(c, True) and good
        seen.append((c, m['family'], m['series'], good))
    famseq_all[dock] = seen
    bad = [s for s in seen if not s[3]]
    print('  %-12s %2d columns on screen, %d wrong' % (dock, len(seen), len(bad)))
    for b in bad:
        print('     *** col %d %s %s' % (b[0], b[1], b[2]))

allseen = sorted(drawn_ok)
print('  columns seen drawn at least once : %d of 72' % len(allseen))
print('  columns whose ring-0 ink != table: %d' % sum(1 for c in allseen if not drawn_ok[c]))
missing = [c for c in range(72) if c not in drawn_ok]
print('  never seen                       : %s' % (missing or 'none'))
print()

print('=== FAMILY ORDER RIGHT ROUND THE CIRCLE, AS DRAWN ===')
order = []
for c in allseen:
    f = MODEL[c]['family']
    if not order or order[-1][0] != f:
        order.append([f, []])
    order[-1][1].append(MODEL[c]['series'])
SHORT = {'yellow-red': 'YR', 'earth': 'E', 'yellow': 'Y', 'yellow-green': 'YG',
         'green': 'G', 'blue-green': 'BG', 'blue': 'B', 'blue-violet': 'BV',
         'violet': 'V', 'red-violet': 'RV', 'red': 'R'}
print('  clockwise from column 0 (bearing 10 deg):')
print('    ' + ' '.join(SHORT[f] for f, _ in order))
print('  brief expects:')
print('    YR E Y YG G BG B BV V RV R')
print()
for f, ser in order:
    d = [int(s[-1]) for s in ser]
    print('  %-13s %-48s %s' % (
        SHORT[f], ' '.join(ser),
        'DESCENDING' if all(d[i] >= d[i + 1] for i in range(len(d) - 1)) else '*** NOT DESCENDING ***'))
print()

print('=== OUTER INK RADIUS, MEASURED ON SCREEN ===')
# The deepest column is E0 (col 14, 9 rings) at bearing 82.5.  Walk outward
# along its mid-bearing until the ink stops.
img = Image.open(os.path.join(SHOTS, 'c01-wheel.png')).convert('RGB')
px = img.load()
W, H = img.size
cx, cy = 285.24, 366.00
for col, a, label in ((14, 82.5, 'E0 (9 rings, deepest)'),
                      (5, 37.5, 'YR0 (8 rings)'),
                      (24, 132.5, 'YG0 (8 rings)')):
    if col == 24:
        continue
    last = None
    r = ROUTBASE
    while r < 1400:
        x = int(round(cx + r * math.cos(math.radians(a))))
        y = int(round(cy + r * math.sin(math.radians(a))))
        if not (0 <= x < W and 0 <= y < H):
            break
        if decode(px[x, y][:3]) not in (None, 'PAGE'):
            last = r
        r += 0.25
    depth = MODEL[col]['depth']
    # extrapolate the 9-ring edge from this column's own last ink
    full = last + (9 - depth) * BAND
    print('  %-24s last ink at %.2f px = %.2f DIP;  9-ring edge %.2f DIP'
          % (label, last, last / SCALE, full / SCALE))
    print('    %-22s at wheel scale 1.0      : %.2f DIP   (reference 647.9, %+.2f%%)'
          % ('', full / SCALE / WHEEL_SCALE, (full / SCALE / WHEEL_SCALE / 647.9 - 1) * 100))
print('  Layout() says rOut = 585.33 DIP at s=0.900  ->  %.2f DIP at s=1.0'
      % (585.33 / WHEEL_SCALE))
