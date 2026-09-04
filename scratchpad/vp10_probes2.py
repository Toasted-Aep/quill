"""Probe generator, parameterised on the wheel centre so it can be re-run for
each dial dock.  usage: vp10_probes2.py <cx> <cy> <outname> [cols...]"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL = json.load(open(os.path.join(HERE, 'vp10', 'model.json')))

CX, CY = float(sys.argv[1]), float(sys.argv[2])
NAME = sys.argv[3]
WANT = set(int(c) for c in sys.argv[4:]) if len(sys.argv) > 4 else set(range(72))

ROUTBASE, BAND = 664.82, 56.20
W, H = 2880, 1800


def point(a, ring):
    r = ROUTBASE + (ring + 0.5) * BAND
    return (CX + r * math.cos(math.radians(a)), CY + r * math.sin(math.radians(a)))


def onscreen(x, y):
    return 60 <= x < W - 60 and 150 <= y < H - 60


rows = []


def add(kind, a, ring):
    c = int(((a - 10.0) / 5.0) % 72)
    if c not in WANT:
        return
    m = MODEL[c]
    if ring >= m['depth']:
        return
    x, y = point(a, ring)
    if not onscreen(x, y):
        return
    rows.append((kind, round(a, 3), ring, c, m['family'], m['series'],
                 m['codes'][ring], m['hexes'][ring], int(round(x)), int(round(y))))


# family seams: the boundary angle is 10 + 5*firstColOfFamily
firsts = {}
for i, m in enumerate(MODEL):
    firsts.setdefault(m['family'], i)
for fam, i in firsts.items():
    b = 10.0 + 5.0 * i
    for d in (-0.8, +0.8):
        for ring in (0, 1):
            add('seam@%s' % fam, b + d, ring)

for c in range(72):
    add('centre', 12.5 + 5.0 * c, 0)
    for ring in (1, 2, 4, 6, 8):
        add('ring%d' % ring, 12.5 + 5.0 * c, ring)

seen, uniq = set(), []
for r in rows:
    if (r[8], r[9]) in seen:
        continue
    seen.add((r[8], r[9]))
    uniq.append(r)

out = os.path.join(HERE, 'vp10', NAME)
with open(out, 'w', newline='') as f:
    f.write('kind,a,ring,col,family,series,code,hex,x,y\n')
    for r in uniq:
        f.write(','.join(str(v) for v in r) + '\n')
print('wrote', out, len(uniq), 'probes')
print('families:', sorted({r[4] for r in uniq}))
print('columns  :', sorted({r[3] for r in uniq}))
