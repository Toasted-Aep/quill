#!/usr/bin/env python3
"""Rasterise an Icons.cs mark the way XAML actually fills it, and report ink.

XAML's path mini-language defaults to EVEN-ODD (only a leading `F1` selects
nonzero), so subpaths that overlap punch holes.  Each subpath here is simple,
so even-odd across the set is exactly the XOR of the per-subpath masks.

    python scratchpad/undo_raster.py [--size 21] [--name UndoRound]

Writes PNGs beside this script and prints the ink measurements 16.6 turns on:
how far the head's two barbs reach past the band's outer and inner edges.
"""
from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from verify_icons import ICONS, literals  # noqa: E402
from undo_measure import subpaths, circle_fit  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))


def mask(subs, px, ss, pad=0.0):
    """Even-odd fill of the 24-grid into a px*ss square, as a float 0..1 mask."""
    n = px * ss
    k = n / 24.0
    acc = np.zeros((n, n), dtype=bool)
    for sub in subs:
        img = Image.new("1", (n, n), 0)
        d = ImageDraw.Draw(img)
        d.polygon([((x + pad) * k, (y + pad) * k) for x, y in sub], fill=1)
        acc ^= np.array(img, dtype=bool)          # even-odd == XOR
    a = acc.astype(np.float32)
    # box-downsample the supersampled mask to the real pixel grid
    return a.reshape(px, ss, px, ss).mean(axis=(1, 3))


def render(subs, px, ss, path, bg=255, ink=0):
    m = mask(subs, px, ss)
    arr = (bg + (ink - bg) * m).clip(0, 255).astype(np.uint8)
    Image.fromarray(arr, "L").resize((px * 8, px * 8), Image.NEAREST).save(path)
    return m


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=float, default=21.0)
    ap.add_argument("--name", default="UndoRound")
    ap.add_argument("--tag", default="before")
    a = ap.parse_args()

    text = open(ICONS, "r", encoding="utf-8").read()
    data = dict((n, v) for n, v, _ in literals(text))[a.name]
    subs = subpaths(data)

    px = int(round(a.size))
    render(subs, px, 16, os.path.join(HERE, f"{a.name}_{a.tag}_{px}dip.png"))
    render(subs, 240, 4, os.path.join(HERE, f"{a.name}_{a.tag}_240.png"))
    print(f"wrote {a.name}_{a.tag}_{px}dip.png and {a.name}_{a.tag}_240.png")

    # ---- ink protrusion, measured on a fine grid in GRID UNITS -------------
    band = subs[0]
    cx, cy, _ = circle_fit(band)
    radii = sorted(math.hypot(p[0] - cx, p[1] - cy) for p in band)
    outer = sum(radii[-60:]) / 60
    inner = sum(radii[:60]) / 60

    N, SS = 24, 64                      # 1536 samples across the grid
    m = mask(subs, N, SS)
    ys, xs = np.nonzero(m > 0.5)
    gx = (xs + 0.5) / (N * 1.0) * 24.0 / 1.0
    gy = (ys + 0.5) / (N * 1.0) * 24.0 / 1.0
    # (mask() already returns the N-resolution grid; recompute at full res)
    full = np.zeros((N * SS, N * SS), dtype=bool)
    for sub in subs:
        img = Image.new("1", (N * SS, N * SS), 0)
        ImageDraw.Draw(img).polygon(
            [(x * N * SS / 24.0, y * N * SS / 24.0) for x, y in sub], fill=1)
        full ^= np.array(img, dtype=bool)
    ys, xs = np.nonzero(full)
    gx = (xs + 0.5) * 24.0 / (N * SS)
    gy = (ys + 0.5) * 24.0 / (N * SS)
    r = np.hypot(gx - cx, gy - cy)
    ang = np.degrees(np.arctan2(gy - cy, gx - cx))

    print(f"\nband: centre ({cx:.3f},{cy:.3f}) inner {inner:.3f} outer {outer:.3f}")
    print(f"ink radius range: {r.min():.3f} .. {r.max():.3f}")
    print(f"  reaches {r.max()-outer:+.3f} past the OUTER edge")
    print(f"  reaches {inner-r.min():+.3f} inside the INNER edge")
    print(f"ink bbox in grid units: x {gx.min():.3f}..{gx.max():.3f}"
          f"  y {gy.min():.3f}..{gy.max():.3f}")
    print(f"ink area (grid units^2): {full.mean()*24*24:.3f}")

    # holes: even-odd overlap shows up as unfilled pixels enclosed by ink
    from PIL import ImageMorph  # noqa: F401  (presence check only)
    lo = mask(subs, 240, 4)
    print(f"coverage at 240px: {lo.mean():.4f}")


if __name__ == "__main__":
    main()
