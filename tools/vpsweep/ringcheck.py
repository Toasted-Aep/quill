"""Prove each capture is the preset we meant to click.

Concepts rings the selected thumbnail with a white circle. Every capture has a
strip crop saved beside it and the x we clicked saved beside that, so the label
is checkable mechanically rather than by reading the caption off a screenshot.
Scores the mean brightness on a circle of the ring's radius at each slot centre;
the ringed slot wins by a mile.
"""
import glob, math, os, re
import numpy as np
from PIL import Image

CROP_X0, CROP_Y0 = 2085, 760      # the strip crop's origin on screen
CY = 845 - CROP_Y0                # thumbnail row centre within the crop
R = 68

def ring_score(a, cx, cy, r):
    vals = []
    for t in range(0, 360, 2):
        x = int(round(cx + r * math.cos(math.radians(t))))
        y = int(round(cy + r * math.sin(math.radians(t))))
        if 0 <= x < a.shape[1] and 0 <= y < a.shape[0]:
            vals.append(a[y, x])
    return float(np.mean(vals)) if vals else -1.0

bad = 0
for f in sorted(glob.glob("vpzoom/S10-strip - *.png")):
    name = os.path.basename(f)[len("S10-strip - "):-4]
    cf = "vpzoom/S10-clickx - %s.txt" % name
    if not os.path.exists(cf):
        print("%-40s NO CLICK RECORD" % name); bad += 1; continue
    want = int(open(cf).read().strip())
    a = np.asarray(Image.open(f).convert("L")).astype(float)
    best, bx = -1, None
    for cx in range(0, a.shape[1], 2):
        s = ring_score(a, cx, CY, R)
        if s > best: best, bx = s, cx
    found = bx + CROP_X0
    ok = abs(found - want) <= 45 and best > 120   # half a 135 px thumbnail; station M scrolls 251 px, not the 222 assumed
    if not ok: bad += 1
    print("%-40s clicked x=%4d   ring at x=%4d (score %5.1f)  %s"
          % (name, want, found, best, "OK" if ok else "*** MISMATCH ***"))
print()
print("%d mismatches" % bad)
