"""Run 25 / 49.8 - WHICH INK IS ON TOP, measured off the glass.

The seed puts two opaque bars on one scanline:

    layer key 0  BLUE   #1B5FC1  page x 200..700
    layer key 1  ORANGE #E07A1F  page x 500..1000

They overlap on page x 500..700 and nowhere else, so the BOUNDARY between the
two colours is the whole measurement:

    boundary at page x = 500  ->  ORANGE painted last, orange owns the overlap
    boundary at page x = 700  ->  BLUE   painted last, blue   owns the overlap

The blue-only run (200..500) and the orange-only run (700..1000) are the
built-in control: BOTH bars must still be on the page in BOTH orders. A reorder
that made one disappear would be a different defect, not a reorder.

The page->screen mapping is derived from the measured bar ENDS rather than
assumed from the window rect, so a chrome change cannot silently move it.

usage:  python vp25_measure.py <png> [label]
"""
import sys
from collections import Counter

from PIL import Image

BLUE = (0x1B, 0x5F, 0xC1)
ORANGE = (0xE0, 0x7A, 0x1F)


def near(px, ref, tol=26):
    return all(abs(px[i] - ref[i]) <= tol for i in range(3))


path = sys.argv[1]
label = sys.argv[2] if len(sys.argv) > 2 else path
im = Image.open(path).convert("RGB")
W, H = im.size

# Find the scanline carrying the most blue+orange: that is the y=400 bar.
best_y, best_n = None, -1
for y in range(0, H, 2):
    row = [im.getpixel((x, y)) for x in range(0, W, 4)]
    n = sum(1 for p in row if near(p, BLUE) or near(p, ORANGE))
    if n > best_n:
        best_n, best_y = n, y

y = best_y
row = [im.getpixel((x, y)) for x in range(W)]
blue_xs = [x for x, p in enumerate(row) if near(p, BLUE)]
orange_xs = [x for x, p in enumerate(row) if near(p, ORANGE)]

print("=" * 70)
print(f"{label}   image {W}x{H}   bar scanline y={y}")
if not blue_xs or not orange_xs:
    print(f"  BLUE px={len(blue_xs)}  ORANGE px={len(orange_xs)}  -- a bar is MISSING")
    sys.exit(1)

b0, b1 = min(blue_xs), max(blue_xs)
o0, o1 = min(orange_xs), max(orange_xs)
print(f"  BLUE   run  x {b0}..{b1}   ({len(blue_xs)} px)")
print(f"  ORANGE run  x {o0}..{o1}   ({len(orange_xs)} px)")

# The boundary is where blue stops and orange starts.
boundary = None
for x in range(b0, o1 + 1):
    if near(row[x], ORANGE) and any(near(row[xx], BLUE) for xx in range(max(b0, x - 6), x)):
        boundary = x
        break
print(f"  boundary (blue -> orange) at screen x = {boundary}")

# Map page -> screen from the two OUTER ends, which no reorder can move:
#   blue starts at page 200, orange ends at page 1000.
# A stroke of Size 40 is drawn with a round cap, so each end overhangs the
# centreline by about half the width; the two overhangs cancel in the SCALE and
# are carried by the offset, so the scale is the trustworthy half.
scale = (o1 - b0) / (1000.0 - 200.0)
def to_page(sx):
    return 200.0 + (sx - b0) / scale

print(f"  derived scale = {scale:.4f} screen px per page unit")
print(f"  blue  left  end -> page x {to_page(b0):7.1f}   (seeded 200)")
print(f"  orange right end -> page x {to_page(o1):7.1f}   (seeded 1000)")
if boundary is not None:
    pb = to_page(boundary)
    print(f"  BOUNDARY         -> page x {pb:7.1f}")
    if abs(pb - 500) < abs(pb - 700):
        print(f"  => ORANGE (layer key 1) owns the overlap: layer 1 PAINTS LAST / ON TOP")
    else:
        print(f"  => BLUE   (layer key 0) owns the overlap: layer 0 PAINTS LAST / ON TOP")
print("=" * 70)
