#!/usr/bin/env python3
"""Rasterise UndoRound AT THE SIZE THE DIAL DRAWS IT and prove the symmetry.

SatSize is 21 DIP (ToolWheel.cs) and the top bar's PathIcon is 24, so a
240 px preview proves nothing: at 21 px the band is one pixel wide and a
barb is four.  Every judgement here is made on the 21 DIP strip.

    python scratchpad/sweep_undo.py

Writes undo_sweep_21dip.png (candidates) and undo_fixed_21dip.png (the
shipped head beside the chosen one, undo above redo) beside this script,
and prints two measurements per candidate:

  CENTRELINE  the exact reach of each tip past the band's outer / inner
              edge, from the closed form - no raster involved.
  INK         the same thing measured off a 64x supersampled raster, which
              includes every cap, join and overlap, so nothing can hide in
              the difference between the outline and what actually fills.
"""
from __future__ import annotations

import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_undo as G                                            # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# (label, arm, sweep, pin the vertex to the band centreline?)
CANDIDATES = [
    ("shipped 4.9/54", 4.9, 54.0, True),
    ("4.9/54 bal",     4.9, 54.0, False),
    ("4.4/52 CHOSEN",  G.ARM, G.SWEEP, False),
    ("4.0/48 bal",     4.0, 48.0, False),
]


def subs_for(arm, sweep, on_shaft):
    return [G.ccw(G.band()), G.ccw(G.chevron(arm, sweep, on_shaft))]


def mask(subs, px, ss, mirror=False):
    """Fill the 24-grid into a px*ss square and box-downsample to px.

    gen_undo.ccw forces one winding on every subpath and the literal is
    F1, so NONZERO fill is exactly their union - which is what OR does.
    (The shipped literal has no F1 and therefore filled EVEN-ODD; that is
    what XOR'd a white notch out of its vertex.  Rendering the union here
    is rendering what the fixed literal does, not what the old one did.)
    """
    n = px * ss
    k = n / 24.0
    acc = np.zeros((n, n), dtype=bool)
    for sub in subs:
        img = Image.new("1", (n, n), 0)
        pts = [(((24 - x) if mirror else x) * k, y * k) for x, y in sub]
        ImageDraw.Draw(img).polygon(pts, fill=1)
        acc |= np.array(img, dtype=bool)
    return acc.astype(np.float32).reshape(px, ss, px, ss).mean(axis=(1, 3))


def ink_reach(subs, n=24, ss=64):
    """Max / min ink radius about the band centre, in grid units.

    Measured on the filled raster, so caps, the vertex join and the
    band-under-barb overlap are all included.
    """
    full = np.zeros((n * ss, n * ss), dtype=bool)
    for sub in subs:
        img = Image.new("1", (n * ss, n * ss), 0)
        ImageDraw.Draw(img).polygon(
            [(x * n * ss / 24.0, y * n * ss / 24.0) for x, y in sub], fill=1)
        full |= np.array(img, dtype=bool)
    ys, xs = np.nonzero(full)
    gx = (xs + 0.5) * 24.0 / (n * ss)
    gy = (ys + 0.5) * 24.0 / (n * ss)
    r = np.hypot(gx - G.CX, gy - G.CY)
    return r.max() - (G.RMID + G.W), (G.RMID - G.W) - r.min()


def strip(rows, px, ss, path, gap=3, scale=10):
    cols = len(rows[0])
    h = len(rows) * px + (len(rows) - 1) * gap
    w = cols * px + (cols - 1) * gap
    canvas = np.zeros((h, w), dtype=np.float32)
    for r, row in enumerate(rows):
        for c, t in enumerate(row):
            canvas[r * (px + gap):r * (px + gap) + px,
                   c * (px + gap):c * (px + gap) + px] = t
    arr = (255 - 255 * canvas).clip(0, 255).astype(np.uint8)
    Image.fromarray(arr, "L").resize((w * scale, h * scale),
                                     Image.NEAREST).save(path)
    return path


def main():
    print(f"{'candidate':<16} {'Rv':>6} | {'centreline out/in':>24} "
          f"{'imbal':>7} {'px@21':>7} | {'ink out/in':>17} {'imbal':>7}")
    for name, arm, sw, on in CANDIDATES:
        rv = G.RMID if on else G.vertex_radius(arm, sw)
        out, inn, imb = G.measure(arm, sw, on)
        io, ii = ink_reach(subs_for(arm, sw, on))
        print(f"{name:<16} {rv:6.3f} | {out:11.3f} /{inn:11.3f} {imb:7.3f} "
              f"{imb * 21 / 24:7.3f} | {io:8.3f} /{ii:7.3f} {io - ii:7.3f}")
    print()

    rows = [[mask(subs_for(a, s, o), 21, 16) for _, a, s, o in CANDIDATES]]
    print("wrote", strip(rows, 21, 16,
                         os.path.join(HERE, "undo_sweep_21dip.png")))

    # The pair as the dial shows it: shipped beside chosen, undo over redo.
    pair = [("shipped", 4.9, 54.0, True), ("fixed", G.ARM, G.SWEEP, False)]
    rows = [[mask(subs_for(a, s, o), 21, 16, mirror=m) for _, a, s, o in pair]
            for m in (False, True)]
    print("wrote", strip(rows, 21, 16,
                         os.path.join(HERE, "undo_fixed_21dip.png"), scale=14))


if __name__ == "__main__":
    main()
