"""Generate the press-probe table for run 7's method: read the DRAWN pixel at a
point, press it, read the dial's dot.  Emits a CSV the PowerShell sweep reads.

Geometry comes straight from QUILL_GEOM_PROBE (physical px, client origin 0,0,
scale 2.0):  centre 285.24,366.00   rOutBase 664.82   band 56.20
Column c spans [10 + 5c, 15 + 5c) degrees at _rot = 100 (every open makes a new
ColorWheel, so _rot is 100 on every open).
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL = json.load(open(os.path.join(HERE, 'vp10', 'model.json')))

CX, CY = 285.24, 366.00
ROUTBASE, BAND = 664.82, 56.20
W, H = 2880, 1800
MARGIN = 60          # keep clear of the window edge and the top bar


def col_of(a):
    return int(((a - 10.0) / 5.0) % 72)


def point(a, ring):
    r = ROUTBASE + (ring + 0.5) * BAND
    return (CX + r * math.cos(math.radians(a)), CY + r * math.sin(math.radians(a)))


def onscreen(x, y):
    return MARGIN <= x < W - MARGIN and 150 <= y < H - MARGIN


rows = []


def add(kind, a, ring):
    c = col_of(a)
    m = MODEL[c]
    if ring >= m['depth']:
        return
    x, y = point(a, ring)
    if not onscreen(x, y):
        return
    rows.append({
        'kind': kind, 'a': round(a, 3), 'ring': ring, 'col': c,
        'family': m['family'], 'series': m['series'],
        'code': m['codes'][ring], 'hex': m['hexes'][ring],
        'x': int(round(x)), 'y': int(round(y)),
    })


# --- the family seams that are on screen at _rot = 100 -----------------------
# R<->YR at 10 deg is the reflection axis and goes FIRST.
SEAMS = [(10.0, 'R|YR'), (-25.0, 'RV|R'), (40.0, 'YR|E'),
         (85.0, 'E|Y'), (105.0, 'Y|YG')]
for a, name in SEAMS:
    for d in (-0.8, +0.8):
        for ring in (0, 1):
            add('seam ' + name, a + d, ring)

# --- one press at the centre of every column that is on screen ---------------
for c in range(72):
    add('centre', 12.5 + 5.0 * c, 0)

# --- a radial run down the deepest column (E0, col 14) -----------------------
for ring in range(9):
    add('radial E0', 82.5, ring)

# --- mid rings, spread ------------------------------------------------------
for c in range(72):
    for ring in (2, 4, 6):
        add('ring %d' % ring, 12.5 + 5.0 * c, ring)

seen = set()
uniq = []
for r in rows:
    k = (r['x'], r['y'])
    if k in seen:
        continue
    seen.add(k)
    uniq.append(r)

out = os.path.join(HERE, 'vp10', 'probes.csv')
with open(out, 'w', newline='') as f:
    f.write('kind,a,ring,col,family,series,code,hex,x,y\n')
    for r in uniq:
        f.write('%s,%s,%d,%d,%s,%s,%s,%s,%d,%d\n' % (
            r['kind'], r['a'], r['ring'], r['col'], r['family'],
            r['series'], r['code'], r['hex'], r['x'], r['y']))
print('wrote', out, len(uniq), 'probes')
by = {}
for r in uniq:
    by[r['kind'].split()[0]] = by.get(r['kind'].split()[0], 0) + 1
print(by)
print('families covered:', sorted({r['family'] for r in uniq}))
