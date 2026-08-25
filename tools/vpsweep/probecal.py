import numpy as np
from PIL import Image
REG = {
  "tabWork":  (2180, 206, 200, 12),   # underline under "Workspace"
  "tabInter": (2385, 206, 200, 12),   # underline under "Interaction"
  "backPill": (2130, 508, 115, 46),   # the white "< Back" pill (editor only)
  "stripRow": (2130, 790, 620, 110),  # thumbnail row at editor-top scroll
  "typeRow":  (2130, 950, 700, 110),  # grid-type row on the root page
}
def mean(im, r):
    x,y,w,h = r
    return float(np.asarray(im.convert("L")).astype(float)[y:y+h, x:x+w].mean())
import sys
for f in sys.argv[1:]:
    im = Image.open(f)
    print("%-32s" % f.split("/")[-1], "  ".join("%s=%6.1f" % (k, mean(im, r)) for k,r in REG.items()))
