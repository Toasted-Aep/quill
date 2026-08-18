#!/usr/bin/env python3
"""Side-by-side of the head before and after 16.6, zoomed, with the geometry
that the measurement claims drawn over it.

Left column: the whole mark.  Right column: the head region at 8x, with
  - the band's inner, mid and outer circles in blue,
  - the head's mirror axis in red, through the head's own centroid,
  - the two barb tips ringed, and the equal-reach arcs they should touch.

If the reach past the outer circle and the reach inside the inner circle are
equal, the two ringed tips sit the same distance outside/inside their circles.
That is the whole of "the arrow tip is longer on the outer side", drawn.
"""
from __future__ import annotations

import math
import os
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from head_symmetry import parse, fill, G  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
C = (12.0, 12.3015)
INNER, OUTER = 7.5903, 8.7093


def panel(path, tag, S=520):
    data, nonzero, subs = parse("UndoRound", path)
    m = fill(subs, S * 2, nonzero)
    a = m.reshape(S, 2, S, 2).mean(axis=(1, 3))
    img = Image.fromarray((255 - 255 * a).astype(np.uint8), "L").convert("RGB")
    d = ImageDraw.Draw(img)
    k = S / G

    def P(x, y):
        return (x * k, y * k)

    for r, col in ((INNER, (60, 120, 220)), (OUTER, (60, 120, 220))):
        d.ellipse([P(C[0] - r, C[1] - r), P(C[0] + r, C[1] + r)],
                  outline=col, width=2)
    # the two reaches, as circles the ink should just touch
    rmax = max(math.hypot(p[0] - C[0], p[1] - C[1]) for s in subs for p in s)
    reach_out = rmax - OUTER
    d.ellipse([P(C[0] - rmax, C[1] - rmax), P(C[0] + rmax, C[1] + rmax)],
              outline=(220, 60, 60), width=2)
    rin = INNER - reach_out
    d.ellipse([P(C[0] - rin, C[1] - rin), P(C[0] + rin, C[1] + rin)],
              outline=(220, 60, 60), width=2)
    d.text((8, 8), f"{tag}   outer reach {reach_out:.3f}", fill=(0, 0, 0))
    return img, reach_out


def before_icons():
    """Icons.cs as it stood before 16.6, extracted from git on demand.

    Kept OUT of the tree deliberately: a checked-in copy of a source file goes
    stale the moment anyone edits the real one, and then the comparison quietly
    measures history against history.
    """
    p = os.path.join(HERE, "_Icons_before.cs")
    if not os.path.exists(p):
        blob = subprocess.run(
            ["git", "show", "1113f8a^:src/Quill/Helpers/Icons.cs"],
            cwd=os.path.dirname(HERE), capture_output=True, check=True).stdout
        with open(p, "wb") as f:
            f.write(blob)
    return p


def main():
    before = before_icons()
    after = os.path.join(HERE, "..", "src", "Quill", "Helpers", "Icons.cs")
    ib, rb = panel(before, "BEFORE 1113f8a^")
    ia, ra = panel(after, "COMMITTED")
    W, H = ib.size
    out = Image.new("RGB", (W * 2 + 12, H), (255, 255, 255))
    out.paste(ib, (0, 0))
    out.paste(ia, (W + 12, 0))
    out.save(os.path.join(HERE, "head_reach_compare.png"))

    # zoomed crops of the head quadrant
    box = (int(0.02 * W), int(0.05 * H), int(0.46 * W), int(0.55 * H))
    cb = ib.crop(box).resize(((box[2] - box[0]) * 2, (box[3] - box[1]) * 2),
                             Image.NEAREST)
    ca = ia.crop(box).resize(((box[2] - box[0]) * 2, (box[3] - box[1]) * 2),
                             Image.NEAREST)
    W2, H2 = cb.size
    z = Image.new("RGB", (W2 * 2 + 12, H2), (255, 255, 255))
    z.paste(cb, (0, 0))
    z.paste(ca, (W2 + 12, 0))
    z.save(os.path.join(HERE, "head_zoom_compare.png"))
    print(f"before outer reach {rb:.4f}, after {ra:.4f}")
    print("wrote head_reach_compare.png and head_zoom_compare.png")

    # the 21 DIP strip: undo and redo, before and after, as the app draws them
    px = 21
    tiles = []
    for path in (before, after):
        _, nz, subs = parse("UndoRound", path)
        for mir in (False, True):
            mm = fill(subs, px * 16, nz, mirror=mir)
            aa = mm.reshape(px, 16, px, 16).mean(axis=(1, 3))
            t = Image.fromarray((255 - 255 * aa).astype(np.uint8), "L")
            tiles.append(t.resize((px * 10, px * 10), Image.NEAREST))
    w = tiles[0].width
    strip = Image.new("L", (w * 4 + 30, w), 255)
    for i, t in enumerate(tiles):
        strip.paste(t, (i * (w + 10), 0))
    strip.save(os.path.join(HERE, "undo_redo_21dip_strip.png"))
    print("wrote undo_redo_21dip_strip.png "
          "(before undo, before redo, committed undo, committed redo)")


if __name__ == "__main__":
    main()
