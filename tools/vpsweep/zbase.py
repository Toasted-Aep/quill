"""Build a chrome-only baseline for the zoomed run, without driving the UI.

The 100% run captured a real `No Grid` frame for differencing. The zoomed run
never got one: the machine locked before the 3-Point list, and a baseline
captured after an unlock would carry different chrome anyway.

A pixelwise MEDIAN over the nine 2-Point captures is a better baseline than the
single No Grid frame, not a worse one. Chrome is identical in all nine, so the
median returns it exactly. Grid ink is a sparse set of 1 px lines whose position
changes completely from preset to preset, so at any given pixel a clear majority
of the nine frames show bare page and the median returns the page. The only
places this fails are pixels where five or more presets happen to put ink, which
is the horizon band a few presets share - and those are excluded below anyway
because a shared horizon is not what we solve for.
"""
import glob, os, sys
import numpy as np
from PIL import Image

d = "vpzoom"
files = sorted(glob.glob(os.path.join(d, "S10 - *.png")))
assert files, "no captures"
print("stacking %d frames" % len(files))
stack = np.stack([np.asarray(Image.open(f).convert("L")) for f in files]).astype(np.uint8)
med = np.median(stack, axis=0).astype(np.uint8)
Image.fromarray(med).save(os.path.join(d, "BASELINE-median10.png"))
print("wrote vpzoom/BASELINE-median10.png", med.shape)

# how much ink does each frame carry above the median?
for f in files:
    g = np.asarray(Image.open(f).convert("L")).astype(np.int16)
    ink = ((g - med.astype(np.int16)) >= 6).sum()
    print("%-44s ink px above median: %7d" % (os.path.basename(f), ink))

# sanity: the readout crops must all say the same zoom
crops = sorted(glob.glob(os.path.join(d, "S10-zoom - *.png")))
ref = np.asarray(Image.open(crops[0]).convert("L"))
same = all((np.asarray(Image.open(c).convert("L")) == ref).all() for c in crops)
print("all %d zoom readouts bit-identical: %s" % (len(crops), same))
