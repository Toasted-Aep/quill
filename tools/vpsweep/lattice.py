# Does the Concepts top bar OVERLAY the canvas or SHRINK it?
# The page carries a dot grid. If the bar overlays, the dot lattice keeps its
# phase and pitch straight through the bar band and up to y=0. If the bar
# shrinks the canvas, the lattice starts BELOW the bar and there are no dots
# above it. Measured, not eyeballed.
import numpy as np
from PIL import Image
import sys

im = np.asarray(Image.open(sys.argv[1]).convert("L")).astype(np.int16)
h, w = im.shape
# a column band well clear of the dial (x<600) and of the right cluster
band = im[:, 1200:2400]
rows = (band > 18).sum(axis=1)          # dots are faint but above the black page
print("image %dx%d" % (w, h))
ys = np.nonzero(rows > 8)[0]
print("first row with dots: y=%s   last: y=%s" % (ys.min() if len(ys) else None,
                                                  ys.max() if len(ys) else None))
# lattice pitch and phase from the row profile
peaks = [y for y in range(2, h-2) if rows[y] > 8 and rows[y] >= rows[y-1] and rows[y] > rows[y+1]]
print("dot rows found: %d, first 12: %s" % (len(peaks), peaks[:12]))
if len(peaks) > 3:
    d = np.diff(peaks)
    d = d[d > 3]
    print("row pitch: median=%.1f px  min=%d max=%d" % (np.median(d), d.min(), d.max()))
    print("dot rows above y=200 (the top-bar band): %s" % [p for p in peaks if p < 200])
