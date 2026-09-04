"""Decode the wheel that is actually DRAWN on a capture.

Given the centre and the radial geometry straight out of QUILL_GEOM_PROBE, walk
each ring round the circle, decode every sample against CopicPalette's own 360
hexes, and report the runs.  This measures the picture, not Layout().
"""
import os
import sys
import math
import re
from collections import Counter

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'mirror'))
import palmodel  # noqa: E402

SRC = os.path.normpath(os.path.join(HERE, '..', 'src', 'Quill', 'Models', 'CopicPalette.cs'))

# hex -> [codes]
BY_HEX = {}
_txt = open(SRC, encoding='utf-8').read()
for m in re.finditer(r'([A-Za-z]{1,2}\d{1,5})\s*:\s*#?([0-9a-fA-F]{6})', _txt):
    BY_HEX.setdefault(m.group(2).lower(), []).append(m.group(1))

PAGE = 'ff00ff'


def decode(px):
    h = '%02x%02x%02x' % px
    if h == PAGE:
        return 'PAGE'
    if h in BY_HEX:
        return '/'.join(BY_HEX[h])
    return None


def scan(img, cx, cy, r, a_lo=-180.0, a_hi=180.0, step=0.05):
    """-> list of (angle_deg, code_or_None, hex) samples that are on-screen."""
    W, H = img.size
    px = img.load()
    out = []
    a = a_lo
    while a < a_hi:
        rad = math.radians(a)
        x = int(round(cx + r * math.cos(rad)))
        y = int(round(cy + r * math.sin(rad)))
        if 0 <= x < W and 0 <= y < H:
            c = px[x, y][:3]
            out.append((a, decode(c), '%02X%02X%02X' % c))
        a += step
    return out


def runs(samples):
    """Collapse consecutive equal codes into runs, dropping label-glyph noise
    (a run shorter than 0.3 deg)."""
    out = []
    for a, code, h in samples:
        if out and out[-1][2] == code:
            out[-1][1] = a
            out[-1][3] += 1
        else:
            out.append([a, a, code, 1])
    return out


if __name__ == '__main__':
    path = sys.argv[1]
    cx = float(sys.argv[2])
    cy = float(sys.argv[3])
    r_out_base = float(sys.argv[4])
    band = float(sys.argv[5])
    ring = int(sys.argv[6]) if len(sys.argv) > 6 else 0
    img = Image.open(path).convert('RGB')
    r = r_out_base + (ring + 0.5) * band
    s = scan(img, cx, cy, r)
    print('ring %d  r=%.2f px   on-screen samples: %d' % (ring, r, len(s)))
    ident = sum(1 for _, c, _ in s if c not in (None, 'PAGE'))
    page = sum(1 for _, c, _ in s if c == 'PAGE')
    unk = sum(1 for _, c, _ in s if c is None)
    print('identified %d   page %d   unknown %d' % (ident, page, unk))
    print()
    print('%-9s %-9s %-6s %s' % ('from', 'to', 'width', 'code'))
    for a0, a1, code, n in runs(s):
        if n < 4 and code is None:
            continue
        print('%9.2f %9.2f %6.2f %s' % (a0, a1, a1 - a0 + 0.05, code))
