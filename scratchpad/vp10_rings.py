"""Walk every ring of the drawn wheel and check, on the PICTURE:
  - family order clockwise
  - series order inside a family
  - the radial run dark -> pale, inward -> outward
  - column width / count
"""
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vp10_scan import decode, BY_HEX  # noqa: E402
from PIL import Image  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL = json.load(open(os.path.join(HERE, 'vp10', 'model.json')))

path, CX, CY, ROUTBASE, BAND = sys.argv[1], 285.24, 366.00, 664.82, 56.20
img = Image.open(path).convert('RGB')
W, H = img.size
px = img.load()


def at(a, ring, frac=0.5):
    r = ROUTBASE + (ring + frac) * BAND
    x = int(round(CX + r * math.cos(math.radians(a))))
    y = int(round(CY + r * math.sin(math.radians(a))))
    if not (0 <= x < W and 0 <= y < H):
        return None, None
    c = px[x, y][:3]
    return decode(c), '#%02X%02X%02X' % c


def lum(h):
    r, g, b = (int(h[i:i + 2], 16) / 255.0 for i in (1, 3, 5))

    def li(v):
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
    return 0.2126 * li(r) + 0.7152 * li(g) + 0.0722 * li(b)


print('=== COLUMN BY COLUMN, off the drawn pixels ===')
print('%-4s %-4s %-6s %-5s %-7s %s' % ('col', 'fam', 'series', 'a(mid)', 'onscr', 'ring0..N  (drawn code : expected)'))
bad = mono_bad = 0
seen_cols = []
famseq = []
for c in range(72):
    m = MODEL[c]
    a = 12.5 + 5.0 * c
    cells = []
    lums = []
    onscreen = 0
    for ring in range(m['depth']):
        code, hx = at(a, ring)
        if code is None and hx is None:
            cells.append('--')
            continue
        onscreen += 1
        exp = m['codes'][ring]
        expx = m['hexes'][ring]
        ok = (hx == expx)
        if not ok:
            bad += 1
        cells.append('%s%s' % (exp, '' if ok else '!=%s(%s)' % (hx, code)))
        lums.append((ring, lum(hx)))
    if onscreen:
        seen_cols.append(c)
        if not famseq or famseq[-1][0] != m['family']:
            famseq.append([m['family'], []])
        famseq[-1][1].append(m['series'])
        # dark -> pale outward: luminance must rise monotonically
        ls = [v for _, v in lums]
        mono = all(ls[i] <= ls[i + 1] + 1e-9 for i in range(len(ls) - 1))
        if not mono:
            mono_bad += 1
        print('%-4d %-4s %-6s %6.1f %5d/%-2d %s   lum %s %s' % (
            c, m['family'][:4], m['series'], a % 360, onscreen, m['depth'],
            ' '.join(cells), 'RISING' if mono else 'NOT MONOTONIC',
            ' '.join('%.3f' % v for v in ls)))

print()
print('columns with at least one cell on screen : %d' % len(seen_cols))
print('drawn hex != expected hex                : %d' % bad)
print('columns whose ink does NOT go dark->pale : %d' % mono_bad)
print()
print('=== FAMILY ORDER, CLOCKWISE, AS DRAWN ===')
for fam, series in famseq:
    digits = [int(s[-1]) for s in series]
    desc = all(digits[i] >= digits[i + 1] for i in range(len(digits) - 1))
    print('  %-13s %-45s %s' % (fam, ' '.join(series),
                                'DESCENDING' if desc else '*** NOT DESCENDING ***'))
